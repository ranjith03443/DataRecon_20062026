"""
RuleInferenceService — Interprets ambiguous business rules into structured transformation metadata.
"""
import time
import uuid
from typing import Optional

from loguru import logger

from app.ai.prompts.prompt_manager import PromptManager, PromptVersioningService
from app.ai.rag.context_builder_service import ContextBuilderService
from app.ai.rag.semantic_retriever_service import SemanticRetrieverService
from app.ai.validators.ai_response_validator import AIResponseValidator
from app.application.dto.rule_inference_dto import RuleInferenceRequestDTO, RuleInferenceResponseDTO
from app.domain.enums.confidence_enums import classify_confidence
from app.domain.models.audit_models import AITokenUsageAudit, estimate_cost
from app.infrastructure.cache.semantic_cache import get_semantic_cache
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import RuleInferenceException
from app.shared.utilities.hash_utils import build_cache_key
from app.shared.utilities.sanitizer import sanitize_prompt

_PROMPT_ID = "rule_inference_v1"


class RuleInferenceService:
    """
    Interprets business rule descriptions into actionable transformation operations.
    Returns structured metadata for use by the .NET orchestrator.
    Advisory only.
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
        self._llm = llm_provider or LLMProviderFactory.create(task="rule_inference")
        self._retriever = retriever or SemanticRetrieverService()
        self._context_builder = context_builder or ContextBuilderService()
        self._validator = validator or AIResponseValidator()
        self._prompt_manager = prompt_manager or PromptManager()
        self._prompt_versioning = prompt_versioning or PromptVersioningService(manager=self._prompt_manager)
        self._cache = get_semantic_cache()
        self._model_config = self._config.get_model_config("rule_inference")

        logger.info(
            f"[RuleInferenceService] Initialized | provider={self._llm.provider_name} | "
            f"model={self._llm.model_name}",
            category=LogCategories.API_REQUEST,
        )

    async def infer_rule(
        self,
        request: RuleInferenceRequestDTO,
        request_id: Optional[str] = None,
    ) -> RuleInferenceResponseDTO:
        """Interpret a business rule and infer transformation operation."""
        effective_request_id = request_id or request.requestId or str(uuid.uuid4())
        job_id = request.jobId or ""
        workflow_step = request.workflowStep or WorkflowStepConstants.RULE_INFERENCE

        logger.info(
            f"[RuleInferenceService] Rule inference START | "
            f"rule={request.rule[:80]}... | job_id={job_id} | request_id={effective_request_id}",
            category=LogCategories.API_REQUEST,
        )

        # Cache lookup
        cache_key = build_cache_key(
            operation="rule_inference",
            rule=request.rule,
            prompt_version=_PROMPT_ID,
        )
        cached = self._cache.get(cache_key, request_id=effective_request_id, job_id=job_id)
        if cached:
            cached["cached"] = True
            cached["requestId"] = effective_request_id
            return RuleInferenceResponseDTO(**cached)

        # RAG retrieval
        semantic_context = "No semantic context available."
        if self._config.enable_rag:
            try:
                import asyncio
                business_rules, glossary = await asyncio.gather(
                    self._retriever.retrieve_business_rules(
                        request.rule, job_id=job_id, request_id=effective_request_id
                    ),
                    self._retriever.retrieve_glossary_terms(
                        request.rule, job_id=job_id, request_id=effective_request_id
                    ),
                    return_exceptions=True,
                )
                semantic_context = self._context_builder.build_rule_context(
                    business_rules=business_rules if not isinstance(business_rules, Exception) else [],
                    glossary_terms=glossary if not isinstance(glossary, Exception) else [],
                    job_id=job_id,
                    request_id=effective_request_id,
                )
            except Exception as exc:
                logger.warning(
                    f"[RuleInferenceService] RAG failed (continuing) | error={str(exc)[:100]}",
                    category=LogCategories.RAG,
                )

        # Prompt rendering
        prompt_version_tag = self._prompt_versioning.audit_prompt_usage(
            _PROMPT_ID, job_id=job_id, request_id=effective_request_id, workflow_step=workflow_step
        )
        system_prompt = self._prompt_manager.render_system_prompt(_PROMPT_ID)
        user_prompt = self._prompt_manager.render_user_prompt(
            _PROMPT_ID,
            rule_description=sanitize_prompt(request.rule),
            field_context=sanitize_prompt(request.fieldContext or "N/A"),
            semantic_context=semantic_context,
        )

        # LLM inference
        start_time = time.monotonic()
        try:
            llm_response = await self._llm.complete(
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                temperature=float(self._model_config.get("temperature", 0.0)),
                max_tokens=int(self._model_config.get("max_tokens", 1000)),
                job_id=job_id,
                request_id=effective_request_id,
                workflow_step=workflow_step,
            )
        except Exception as exc:
            raise RuleInferenceException(
                f"Rule inference LLM call failed: {str(exc)[:200]}",
                rule=request.rule[:80],
            )

        latency_ms = (time.monotonic() - start_time) * 1000

        # Validate response
        validated_data, confidence = self._validator.validate_rule_inference_response(
            llm_response.content, job_id=job_id, request_id=effective_request_id
        )

        # Token audit
        audit = AITokenUsageAudit(
            job_id=job_id,
            request_id=effective_request_id,
            workflow_step=workflow_step,
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
            "[RuleInferenceService] Token usage | " + str(audit.to_log_dict()).replace("{", "{{").replace("}", "}}"),
            category=LogCategories.TOKEN_USAGE,
        )

        confidence_level = classify_confidence(confidence).value
        response_dict = {
            "operation": validated_data.get("operation", ""),
            "format": validated_data.get("format"),
            "parameters": validated_data.get("parameters"),
            "description": validated_data.get("description"),
            "validationRules": validated_data.get("validationRules"),
            "confidence": confidence,
            "confidenceLevel": confidence_level,
            "reasoning": validated_data.get("reasoning"),
            "promptVersion": prompt_version_tag,
            "cached": False,
            "requestId": effective_request_id,
        }

        self._cache.set(
            cache_key, response_dict, confidence=confidence,
            request_id=effective_request_id, job_id=job_id,
        )

        logger.info(
            f"[RuleInferenceService] Rule inference COMPLETE | "
            f"operation={validated_data.get('operation')} | confidence={confidence:.3f} | "
            f"latency_ms={latency_ms:.1f} | job_id={job_id}",
            category=LogCategories.API_RESPONSE,
        )
        return RuleInferenceResponseDTO(**response_dict)
