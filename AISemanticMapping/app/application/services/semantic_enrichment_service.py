"""
SemanticEnrichmentService — Semantic schema enrichment service.
Infers meanings, expands abbreviations, and enriches field metadata.
"""
import time
import uuid
from typing import Optional

from loguru import logger

from app.ai.prompts.prompt_manager import PromptManager, PromptVersioningService
from app.ai.rag.context_builder_service import ContextBuilderService
from app.ai.rag.semantic_retriever_service import SemanticRetrieverService
from app.ai.validators.ai_response_validator import AIResponseValidator
from app.application.dto.schema_enrichment_dto import (
    SemanticEnrichmentRequestDTO,
    SemanticEnrichmentResponseDTO,
)
from app.domain.enums.confidence_enums import classify_confidence
from app.domain.models.audit_models import AITokenUsageAudit, estimate_cost
from app.infrastructure.cache.semantic_cache import get_semantic_cache
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import SemanticMappingException
from app.shared.utilities.hash_utils import build_cache_key
from app.shared.utilities.sanitizer import sanitize_prompt

_PROMPT_ID = "schema_enrichment_v1"


class SemanticEnrichmentService:
    """
    Enriches field names with semantic meaning, business context, and abbreviation expansions.
    Advisory only — does not own workflow logic.
    """

    def __init__(
        self,
        llm_provider: Optional[ILLMProvider] = None,
        retriever: Optional[SemanticRetrieverService] = None,
        context_builder: Optional[ContextBuilderService] = None,
        validator: Optional[AIResponseValidator] = None,
        prompt_manager: Optional[PromptManager] = None,
        prompt_versioning: Optional[PromptVersioningService] = None,
    ):
        self._config = get_app_config()
        self._llm = llm_provider or LLMProviderFactory.create(task="schema_enrichment")
        self._retriever = retriever or SemanticRetrieverService()
        self._context_builder = context_builder or ContextBuilderService()
        self._validator = validator or AIResponseValidator()
        self._prompt_manager = prompt_manager or PromptManager()
        self._prompt_versioning = prompt_versioning or PromptVersioningService(manager=self._prompt_manager)
        self._cache = get_semantic_cache()
        self._model_config = self._config.get_model_config("schema_enrichment")

        logger.info(
            f"[SemanticEnrichmentService] Initialized | provider={self._llm.provider_name} | "
            f"model={self._llm.model_name}",
            category=LogCategories.API_REQUEST,
        )

    async def enrich_field(
        self,
        request: SemanticEnrichmentRequestDTO,
        request_id: Optional[str] = None,
    ) -> SemanticEnrichmentResponseDTO:
        """Enrich a field name with semantic meaning and business context."""
        effective_request_id = request_id or request.requestId or str(uuid.uuid4())
        job_id = request.jobId or ""

        logger.info(
            f"[SemanticEnrichmentService] Enrichment START | "
            f"fieldName={request.fieldName} | job_id={job_id} | request_id={effective_request_id}",
            category=LogCategories.API_REQUEST,
        )

        # Cache lookup
        cache_key = build_cache_key(
            operation="schema_enrichment",
            source_field=request.fieldName,
            prompt_version=_PROMPT_ID,
        )
        cached = self._cache.get(cache_key, request_id=effective_request_id, job_id=job_id)
        if cached:
            cached["cached"] = True
            cached["requestId"] = effective_request_id
            return SemanticEnrichmentResponseDTO(**cached)

        # RAG retrieval
        semantic_context = "No semantic context available."
        if self._config.enable_rag:
            try:
                import asyncio
                glossary, field_desc = await asyncio.gather(
                    self._retriever.retrieve_glossary_terms(
                        request.fieldName, job_id=job_id, request_id=effective_request_id
                    ),
                    self._retriever.retrieve_field_descriptions(
                        request.fieldName, job_id=job_id, request_id=effective_request_id
                    ),
                    return_exceptions=True,
                )
                semantic_context = self._context_builder.build_enrichment_context(
                    glossary_terms=glossary if not isinstance(glossary, Exception) else [],
                    field_descriptions=field_desc if not isinstance(field_desc, Exception) else [],
                    job_id=job_id,
                    request_id=effective_request_id,
                )
            except Exception as exc:
                logger.warning(
                    f"[SemanticEnrichmentService] RAG failed (continuing) | error={str(exc)[:100]}",
                    category=LogCategories.RAG,
                )

        # Prompt rendering
        prompt_version_tag = self._prompt_versioning.audit_prompt_usage(
            _PROMPT_ID, job_id=job_id, request_id=effective_request_id,
            workflow_step=WorkflowStepConstants.SCHEMA_ENRICHMENT,
        )
        system_prompt = self._prompt_manager.render_system_prompt(_PROMPT_ID)
        user_prompt = self._prompt_manager.render_user_prompt(
            _PROMPT_ID,
            field_name=request.fieldName,
            additional_context=sanitize_prompt(request.additionalContext or "N/A"),
            semantic_context=semantic_context,
        )

        # LLM inference
        start_time = time.monotonic()
        try:
            llm_response = await self._llm.complete(
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                temperature=float(self._model_config.get("temperature", 0.1)),
                max_tokens=int(self._model_config.get("max_tokens", 1000)),
                job_id=job_id,
                request_id=effective_request_id,
                workflow_step=WorkflowStepConstants.SCHEMA_ENRICHMENT,
            )
        except Exception as exc:
            raise SemanticMappingException(
                f"Schema enrichment LLM inference failed: {str(exc)[:200]}",
                source_field=request.fieldName,
            )

        latency_ms = (time.monotonic() - start_time) * 1000

        # Validate response
        validated_data, confidence = self._validator.validate_enrichment_response(
            llm_response.content, job_id=job_id, request_id=effective_request_id
        )

        # Token audit
        audit = AITokenUsageAudit(
            job_id=job_id,
            request_id=effective_request_id,
            workflow_step=WorkflowStepConstants.SCHEMA_ENRICHMENT,
            model_name=self._llm.model_name,
            provider_name=self._llm.provider_name,
            prompt_tokens=llm_response.prompt_tokens,
            completion_tokens=llm_response.completion_tokens,
            total_tokens=llm_response.total_tokens,
            estimated_cost_usd=estimate_cost(
                llm_response.prompt_tokens, llm_response.completion_tokens, self._llm.model_name
            ),
            latency_ms=latency_ms,
            prompt_version=prompt_version_tag,
        )
        logger.info(
            "[SemanticEnrichmentService] Token usage | " + str(audit.to_log_dict()).replace("{", "{{").replace("}", "}}"),
            category=LogCategories.TOKEN_USAGE,
        )

        confidence_level = classify_confidence(confidence).value
        response_dict = {
            "fieldName": request.fieldName,
            "possibleMeaning": validated_data.get("possibleMeaning", ""),
            "businessCategory": validated_data.get("businessCategory", ""),
            "description": validated_data.get("description"),
            "dataTypeHint": validated_data.get("dataTypeHint"),
            "abbreviationsExpanded": validated_data.get("abbreviationsExpanded"),
            "confidence": confidence,
            "confidenceLevel": confidence_level,
            "promptVersion": prompt_version_tag,
            "cached": False,
            "requestId": effective_request_id,
        }

        self._cache.set(
            cache_key, response_dict, confidence=confidence,
            request_id=effective_request_id, job_id=job_id,
        )

        logger.info(
            f"[SemanticEnrichmentService] Enrichment COMPLETE | "
            f"fieldName={request.fieldName} | meaning={validated_data.get('possibleMeaning')} | "
            f"confidence={confidence:.3f} | latency_ms={latency_ms:.1f} | job_id={job_id}",
            category=LogCategories.API_RESPONSE,
        )
        return SemanticEnrichmentResponseDTO(**response_dict)
