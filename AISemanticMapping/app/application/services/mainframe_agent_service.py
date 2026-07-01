"""
MainframeAgentService — AI-powered Mainframe Development Assistant.

Provides the following AI agents:
  explain        — Business explanation of COBOL skeleton logic
  review         — COBOL code review: missing validations, issues, concerns
  enhance        — Enhanced COBOL snippets (IF, EVALUATE, routines)
  validation     — Generate COBOL validation logic examples
  error_handling — Generate COBOL error handling recommendations
  documentation  — Generate developer documentation
  jcl_improvements — JCL review and improvement suggestions
  optimization   — Performance and structure optimisations

Architecture:
  - Additive only. Does NOT modify the deterministic MainframeAssetGenerationService.
  - Uses RAG retrieval from MAINFRAME_PATTERNS ChromaDB collection.
  - Prompt templates loaded from prompts/mainframe_*_v1.yaml.
  - All output carries AI governance header.
"""
import json
import re
import time
import uuid
from typing import Optional

from loguru import logger

from app.ai.embeddings.embedding_service import EmbeddingService
from app.ai.prompts.prompt_manager import PromptManager, PromptVersioningService
from app.ai.rag.semantic_retriever_service import SemanticRetrieverService
from app.application.dto.mainframe_agent_dto import (
    MainframeAgentRequestDTO,
    MainframeAgentResponseDTO,
    RecommendationItem,
)
from app.domain.enums.confidence_enums import classify_confidence
from app.domain.models.audit_models import AITokenUsageAudit, estimate_cost
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.shared.constants.app_constants import (
    CollectionNames,
    LogCategories,
    WorkflowStepConstants,
)
from app.shared.exceptions.base_exceptions import SemanticMappingException

# ── Agent → prompt mapping ───────────────────────────────────────────────────
_PROMPT_MAP = {
    "explain":          "mainframe_explain_v1",
    "review":           "mainframe_review_v1",
    "enhance":          "mainframe_enhance_v1",
    "validation":       "mainframe_validation_v1",
    "error_handling":   "mainframe_error_handling_v1",
    "documentation":    "mainframe_documentation_v1",
    "jcl_improvements": "mainframe_jcl_improvements_v1",
    "optimization":     "mainframe_optimization_v1",
    "generate_cobol":         "mainframe_generate_cobol_v1",
    "generate_jcl":           "mainframe_generate_jcl_v1",
    "generate_recon_cobol":   "mainframe_generate_recon_cobol_v1",
    "generate_recon_jcl":     "mainframe_generate_recon_jcl_v1",
}

_AGENT_NAMES = {
    "explain":          "COBOL Explain Agent",
    "review":           "COBOL Review Agent",
    "enhance":          "COBOL Enhancement Agent",
    "validation":       "Validation Logic Agent",
    "error_handling":   "Error Handling Agent",
    "documentation":    "Developer Documentation Agent",
    "jcl_improvements": "JCL Improvement Agent",
    "optimization":     "Optimisation Agent",
    "generate_cobol":         "COBOL Generation Agent",
    "generate_jcl":           "JCL Generation Agent",
    "generate_recon_cobol":   "Recon COBOL Generation Agent",
    "generate_recon_jcl":     "Recon JCL Generation Agent",
}

_GOVERNANCE = (
    "*** AI Generated Developer Guidance ***\n"
    "*** Developer Review Required       ***\n"
    "*** Not Production Ready            ***"
)

# Code-generation prompts produce complete programs — need a much larger token budget
# than the default 2 000 used for advisory prompts (explain, review, etc.)
_CODE_GEN_PROMPT_TYPES = {
    "generate_cobol",
    "generate_jcl",
    "generate_recon_cobol",
    "generate_recon_jcl",
}
_CODE_GEN_MAX_TOKENS = 16000
_DEFAULT_MAX_TOKENS  = 4000


class MainframeAgentService:
    """
    Orchestrates all Mainframe Development AI Agent prompts.
    Stateless — one instance per request (FastAPI DI).
    """

    def __init__(
        self,
        llm_provider: Optional[ILLMProvider] = None,
        embedding_service: Optional[EmbeddingService] = None,
        retriever: Optional[SemanticRetrieverService] = None,
        prompt_manager: Optional[PromptManager] = None,
        prompt_versioning: Optional[PromptVersioningService] = None,
    ):
        self._config = get_app_config()
        self._llm = llm_provider or LLMProviderFactory.create(task="mainframe_agent")
        self._embedding_service = embedding_service or EmbeddingService()
        self._retriever = retriever or SemanticRetrieverService(
            embedding_service=self._embedding_service
        )
        self._prompt_manager = prompt_manager or PromptManager()
        self._prompt_versioning = prompt_versioning or PromptVersioningService(
            manager=self._prompt_manager
        )
        logger.info(
            f"[MainframeAgentService] Initialized | provider={self._llm.provider_name} | "
            f"model={self._llm.model_name}",
            category=LogCategories.API_REQUEST,
        )

    # ── Public entry point ────────────────────────────────────────────────────

    async def run_agent(
        self,
        request: MainframeAgentRequestDTO,
        request_id: Optional[str] = None,
    ) -> MainframeAgentResponseDTO:
        """Dispatch to the correct agent based on request.promptType."""
        t0 = time.monotonic()
        rid = request_id or request.requestId or str(uuid.uuid4())
        prompt_type = (request.promptType or "").lower().strip()

        logger.bind(
            category=LogCategories.MAINFRAME_AI_AGENT,
            jobId=request.jobId,
            promptType=prompt_type,
            userId=request.userId or "anonymous",
        ).info(
            f"[MainframeAgentService] Agent request | jobId={request.jobId} | "
            f"type={prompt_type} | request_id={rid}"
        )

        if prompt_type not in _PROMPT_MAP:
            raise SemanticMappingException(
                f"Unknown promptType '{prompt_type}'. "
                f"Supported: {list(_PROMPT_MAP.keys())}",
                job_id=request.jobId,
            )

        # ── RAG retrieval ─────────────────────────────────────────────────────
        rag_context = await self._retrieve_context(request, prompt_type, rid)

        # ── Build prompt ──────────────────────────────────────────────────────
        prompt_id = _PROMPT_MAP[prompt_type]
        system_prompt, user_prompt, prompt_version = self._build_prompt(
            prompt_id, request, rag_context, prompt_type
        )

        # ── Call LLM ──────────────────────────────────────────────────────────
        max_tokens = (
            _CODE_GEN_MAX_TOKENS
            if prompt_type in _CODE_GEN_PROMPT_TYPES
            else _DEFAULT_MAX_TOKENS
        )
        t_llm = time.monotonic()
        llm_response = await self._llm.complete(
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            json_mode=True,
            max_tokens=max_tokens,
        )
        llm_ms = (time.monotonic() - t_llm) * 1000

        # ── Parse response ────────────────────────────────────────────────────
        result = self._parse_response(
            raw=llm_response.content,
            prompt_type=prompt_type,
            job_id=request.jobId,
            request_id=rid,
        )

        total_ms = (time.monotonic() - t0) * 1000

        token_usage = {
            "prompt_tokens": int(getattr(llm_response, "prompt_tokens", 0) or 0),
            "completion_tokens": int(getattr(llm_response, "completion_tokens", 0) or 0),
            "total_tokens": int(getattr(llm_response, "total_tokens", 0) or 0),
            "model_name": getattr(llm_response, "model_name", None),
            "provider_name": getattr(llm_response, "provider_name", None),
        }
        if token_usage["total_tokens"] <= 0:
            token_usage = None

        # ── Audit log ─────────────────────────────────────────────────────────
        logger.bind(
            category=LogCategories.MAINFRAME_AI_AGENT,
            jobId=request.jobId,
            promptType=prompt_type,
            userId=request.userId or "anonymous",
            promptVersion=prompt_version,
            tokenUsage=token_usage,
            responseTimeMs=round(total_ms, 1),
            confidence=result.confidence,
        ).info(
            f"[MainframeAgentService] Agent completed | jobId={request.jobId} | "
            f"type={prompt_type} | confidence={result.confidence:.2f} | "
            f"total_ms={total_ms:.0f} | llm_ms={llm_ms:.0f}"
        )

        result.responseTimeMs = round(total_ms, 1)
        result.promptVersion = prompt_version
        result.requestId = rid
        if token_usage:
            result.tokenUsage = token_usage

        return result

    # ── RAG Retrieval ─────────────────────────────────────────────────────────

    async def _retrieve_context(
        self,
        request: MainframeAgentRequestDTO,
        prompt_type: str,
        request_id: str,
    ) -> str:
        """Retrieve relevant mainframe patterns from ChromaDB."""
        if not self._config.enable_rag:
            return ""

        try:
            query_parts = [f"mainframe COBOL JCL {prompt_type} development guidance"]
            if request.cobolContent:
                # Use first 400 chars of COBOL as the query
                query_parts.append(request.cobolContent[:400])
            if request.fieldMappings:
                field_names = [f.get("targetField", "") for f in (request.fieldMappings or [])[:5]]
                query_parts.append(" ".join(field_names))

            query = " ".join(query_parts)

            results = await self._retriever.retrieve_from_collection(
                collection=CollectionNames.MAINFRAME_PATTERNS,
                query=query,
                top_k=4,
                job_id=request.jobId,
                request_id=request_id,
            )

            if not results:
                return "No prior mainframe patterns found in knowledge base."

            ctx_lines = ["Retrieved mainframe patterns from knowledge base:"]
            for i, r in enumerate(results, 1):
                ctx_lines.append(
                    f"  [{i}] (score={r.score:.2f}) {r.document[:300]}"
                )
            return "\n".join(ctx_lines)

        except Exception as exc:
            logger.bind(category=LogCategories.RAG).warning(
                f"[MainframeAgentService] RAG retrieval failed (non-fatal): {exc}"
            )
            return "RAG retrieval unavailable."

    # ── Prompt building ───────────────────────────────────────────────────────

    def _build_prompt(
        self,
        prompt_id: str,
        request: MainframeAgentRequestDTO,
        rag_context: str,
        prompt_type: str,
    ):
        """Load YAML prompt and render variables."""
        # Use PromptVersioningService.get_version_tag() — the correct method name
        version = self._prompt_versioning.get_version_tag(prompt_id)

        cobol_snippet = (request.cobolContent or "")[:6000]
        jcl_snippet = (request.jclContent or "")[:3000]
        copybook_snippet = (request.copybookContent or "")[:2000]

        value_map_str = ""
        if request.valueMappings:
            try:
                value_map_str = json.dumps(request.valueMappings, indent=2)[:3000]
            except Exception:
                value_map_str = str(request.valueMappings)[:3000]

        rules_str = ""
        if request.transformationRules:
            try:
                rules_str = json.dumps(request.transformationRules, indent=2)[:3000]
            except Exception:
                rules_str = str(request.transformationRules)[:3000]

        mappings_str = ""
        if request.fieldMappings:
            try:
                mappings_str = json.dumps(request.fieldMappings[:30], indent=2)[:3000]
            except Exception:
                mappings_str = str(request.fieldMappings)[:3000]

        field_details_str = ""
        if request.fieldDetails:
            try:
                field_details_str = json.dumps(request.fieldDetails, indent=2)[:4000]
            except Exception:
                field_details_str = str(request.fieldDetails)[:4000]

        recon_checks_str = ""
        if request.reconChecks:
            try:
                recon_checks_str = json.dumps(request.reconChecks, indent=2)[:4000]
            except Exception:
                recon_checks_str = str(request.reconChecks)[:4000]

        source_datasets_str = ""
        if request.sourceDatasets:
            try:
                source_datasets_str = json.dumps(request.sourceDatasets, indent=2)[:2000]
            except Exception:
                source_datasets_str = str(request.sourceDatasets)[:2000]

        variables = {
            "cobol_content":          cobol_snippet or "(not provided)",
            "jcl_content":            jcl_snippet or "(not provided)",
            "copybook_content":       copybook_snippet or "(not provided)",
            "value_mappings":         value_map_str or "(not provided)",
            "transformation_rules":   rules_str or "(not provided)",
            "field_mappings":         mappings_str or "(not provided)",
            "rag_context":            rag_context or "(none)",
            "prompt_type":            prompt_type,
            "job_id":                 request.jobId,
            "program_name":           request.programName or "PGMNAME",
            "record_name":            request.recordName or "RECORD",
            "job_name":               request.jobName or "JOBNAME",
            "total_record_length":    str(request.totalRecordLength or 0),
            "field_details":          field_details_str or "(not provided)",
            "expected_record_count":  str(request.expectedRecordCount or 0),
            "recon_checks":           recon_checks_str or "(not provided)",
            "source_datasets":        source_datasets_str or "(not provided)",
        }

        # Use PromptManager.render_system_prompt() and render_user_prompt()
        # — the correct API; PromptManager has no load() or render() methods.
        system_prompt = self._prompt_manager.render_system_prompt(prompt_id)
        user_prompt = self._prompt_manager.render_user_prompt(prompt_id, **variables)

        return system_prompt, user_prompt, version

    # ── Response parsing ──────────────────────────────────────────────────────

    @staticmethod
    def _strip_markdown_fences(text: str) -> str:
        """Remove ```json / ``` fences that some LLMs wrap around JSON responses."""
        stripped = text.strip()
        # Remove opening fence (```json, ```JSON, ```, etc.)
        stripped = re.sub(r'^```[a-zA-Z]*\s*\n?', '', stripped)
        # Remove closing fence
        stripped = re.sub(r'\n?```\s*$', '', stripped)
        return stripped.strip()

    @staticmethod
    def _escape_json_strings(text: str) -> str:
        """
        Fix a common LLM JSON generation error: literal newlines/tabs/carriage-returns
        embedded inside JSON string values instead of the required \\n / \\t escapes.
        Walks the text character-by-character to stay in sync with the in/out-of-string state.
        """
        result: list[str] = []
        in_string = False
        escape_next = False
        for ch in text:
            if escape_next:
                result.append(ch)
                escape_next = False
                continue
            if ch == '\\' and in_string:
                result.append(ch)
                escape_next = True
                continue
            if ch == '"':
                in_string = not in_string
                result.append(ch)
                continue
            if in_string:
                if ch == '\n':
                    result.append('\\n')
                elif ch == '\r':
                    result.append('\\r')
                elif ch == '\t':
                    result.append('\\t')
                else:
                    result.append(ch)
            else:
                result.append(ch)
        return ''.join(result)

    def _parse_response(
        self,
        raw: str,
        prompt_type: str,
        job_id: str,
        request_id: str,
    ) -> MainframeAgentResponseDTO:
        """Parse LLM JSON response into MainframeAgentResponseDTO."""
        cleaned = self._strip_markdown_fences(raw)

        data = None

        # Pass 1: direct parse
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError:
            pass

        # Pass 2: escape unescaped control chars inside string values, then parse
        if data is None:
            try:
                data = json.loads(self._escape_json_strings(cleaned))
            except json.JSONDecodeError:
                pass

        # Pass 3: extract outermost {...} block, then apply escaping and parse
        if data is None:
            match = re.search(r'\{.*\}', cleaned, re.DOTALL)
            if match:
                try:
                    data = json.loads(self._escape_json_strings(match.group()))
                except json.JSONDecodeError:
                    pass

        # Pass 4: detect raw COBOL/JCL output (LLM ignored the JSON instruction)
        if data is None:
            stripped_raw = raw.strip()
            looks_like_cobol = re.match(r'^\s{6,}(IDENTIFICATION|\*)', stripped_raw, re.IGNORECASE)
            looks_like_jcl  = stripped_raw.startswith('//')
            if looks_like_cobol or looks_like_jcl:
                data = {
                    "generatedCode": stripped_raw,
                    "confidence": 0.65,
                    "reasoning": "Response was raw COBOL/JCL — JSON wrapper was absent.",
                }

        if data is None:
            logger.bind(category=LogCategories.MAINFRAME_AI_AGENT).warning(
                f"[MainframeAgentService] LLM response not valid JSON after all attempts | "
                f"job_id={job_id} | response_len={len(raw)} | "
                f"first_200_chars={raw[:200]!r}"
            )
            data = {
                "businessExplanation": raw,
                "confidence": 0.5,
                "reasoning": "Raw text response — JSON parsing failed.",
            }

        confidence = float(data.get("confidence", 0.5))
        confidence_level = classify_confidence(confidence)

        # Parse recommendations list
        raw_recs = data.get("recommendations", [])
        recommendations = []
        for r in raw_recs:
            if isinstance(r, dict):
                recommendations.append(
                    RecommendationItem(
                        category=r.get("category", "general"),
                        severity=r.get("severity", "INFO"),
                        message=r.get("message", ""),
                        suggestion=r.get("suggestion"),
                        codeExample=r.get("codeExample"),
                    )
                )
            elif isinstance(r, str):
                recommendations.append(
                    RecommendationItem(category="general", severity="INFO", message=r)
                )

        warnings = list(data.get("warnings", []))
        # Always prepend governance warning
        warnings.insert(0, _GOVERNANCE)

        return MainframeAgentResponseDTO(
            jobId=job_id,
            promptType=prompt_type,
            agentName=_AGENT_NAMES.get(prompt_type, "Mainframe AI Agent"),
            businessExplanation=data.get("businessExplanation") or data.get("explanation"),
            generatedCode=data.get("generatedCode") or data.get("code"),
            recommendations=recommendations,
            warnings=warnings,
            confidence=confidence,
            confidenceLevel=confidence_level,
            requestId=request_id,
            governanceNotice=(
                "AI Generated Developer Guidance | "
                "Developer Review Required | "
                "Not Production Ready"
            ),
        )
