"""
SemanticMappingService — Core semantic field mapping inference service.

Implements the full RAG flow:
1. Check semantic cache
2. Generate query embedding
3. Retrieve historical mappings from vector store
4. Build enriched prompt with context
5. Call LLM provider
6. Validate AI response
7. Cache high-confidence results
8. Return structured response
"""
import time
import uuid
from typing import Optional

from loguru import logger

from app.ai.embeddings.embedding_service import EmbeddingService
from app.ai.prompts.prompt_manager import PromptManager, PromptVersioningService
from app.ai.rag.context_builder_service import ContextBuilderService
from app.ai.rag.semantic_retriever_service import SemanticRetrieverService
from app.ai.validators.ai_response_validator import AIResponseValidator
from app.application.dto.semantic_mapping_dto import (
    MappingAlternativeDTO,
    SemanticMappingRequestDTO,
    SemanticMappingResponseDTO,
)
from app.domain.models.audit_models import AITokenUsageAudit, estimate_cost
from app.domain.enums.confidence_enums import classify_confidence
from app.infrastructure.cache.semantic_cache import get_semantic_cache
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import SemanticMappingException
from app.shared.utilities.hash_utils import build_cache_key
from app.shared.utilities.sanitizer import sanitize_prompt, safe_exc

_PROMPT_ID = "semantic_mapping_v1"


class SemanticMappingService:
    """
    Enterprise-grade semantic field mapping inference service.

    This service is ADVISORY ONLY. It provides mapping recommendations
    but does NOT orchestrate workflows or own persistence.
    """

    def __init__(
        self,
        llm_provider: Optional[ILLMProvider] = None,
        embedding_service: Optional[EmbeddingService] = None,
        retriever: Optional[SemanticRetrieverService] = None,
        context_builder: Optional[ContextBuilderService] = None,
        validator: Optional[AIResponseValidator] = None,
        prompt_manager: Optional[PromptManager] = None,
        prompt_versioning: Optional[PromptVersioningService] = None,
    ):
        self._config = get_app_config()
        self._llm = llm_provider or LLMProviderFactory.create(task="semantic_mapping")
        self._embedding_service = embedding_service or EmbeddingService()
        self._retriever = retriever or SemanticRetrieverService(embedding_service=self._embedding_service)
        self._context_builder = context_builder or ContextBuilderService()
        self._validator = validator or AIResponseValidator()
        self._prompt_manager = prompt_manager or PromptManager()
        self._prompt_versioning = prompt_versioning or PromptVersioningService(manager=self._prompt_manager)
        self._cache = get_semantic_cache()
        self._model_config = self._config.get_model_config("semantic_mapping")

        logger.info(
            f"[SemanticMappingService] Initialized | provider={self._llm.provider_name} | "
            f"model={self._llm.model_name} | rag_enabled={self._config.enable_rag}",
            category=LogCategories.API_REQUEST,
        )

    async def infer_mapping(
        self,
        request: SemanticMappingRequestDTO,
        request_id: Optional[str] = None,
    ) -> SemanticMappingResponseDTO:
        """
        Infer the best semantic mapping for a source field.

        Args:
            request: Semantic mapping request DTO.
            request_id: Correlation request ID.

        Returns:
            SemanticMappingResponseDTO with mapping, confidence, reasoning.
        """
        effective_request_id = request_id or request.requestId or str(uuid.uuid4())
        job_id = request.jobId
        workflow_step = request.workflowStep or WorkflowStepConstants.SEMANTIC_MAPPING

        logger.info(
            f"[SemanticMappingService] Mapping inference START | "
            f"sourceField={request.sourceField} | "
            f"candidates={request.targetFieldCandidates} | "
            f"job_id={job_id} | request_id={effective_request_id} | "
            f"workflow_step={workflow_step}",
            category=LogCategories.API_REQUEST,
        )

        # Step 1: Check semantic cache
        cache_key = build_cache_key(
            operation="semantic_mapping",
            source_field=request.sourceField,
            target_candidates=request.targetFieldCandidates,
            source_metadata=request.sourceMetadata.model_dump() if request.sourceMetadata else None,
            prompt_version=_PROMPT_ID,
        )
        cached = self._cache.get(cache_key, request_id=effective_request_id, job_id=job_id)
        if cached:
            logger.info(
                f"[SemanticMappingService] Cache HIT — returning cached mapping | "
                f"job_id={job_id} | request_id={effective_request_id}",
                category=LogCategories.CACHE,
            )
            cached["cached"] = True
            cached["requestId"] = effective_request_id
            return SemanticMappingResponseDTO(**cached)

        # Step 2: RAG retrieval (graceful — if unavailable, inference continues)
        semantic_context = "No semantic context available."
        if self._config.enable_rag:
            query = f"{request.sourceField} {request.sourceMetadata.description if request.sourceMetadata else ''}"
            query = sanitize_prompt(query)
            try:
                historical, glossary, field_desc = await _gather_mapping_context(
                    self._retriever, query, job_id, effective_request_id
                )
                semantic_context = self._context_builder.build_mapping_context(
                    historical_mappings=historical,
                    glossary_terms=glossary,
                    field_descriptions=field_desc,
                    job_id=job_id,
                    request_id=effective_request_id,
                )
            except Exception as exc:
                logger.warning(
                    f"[SemanticMappingService] RAG retrieval failed (continuing without context) | "
                    f"error={safe_exc(exc, 100)} | job_id={job_id}",
                    category=LogCategories.RAG,
                )

        # Step 3: Build enriched prompt
        prompt_version_tag = self._prompt_versioning.audit_prompt_usage(
            _PROMPT_ID, job_id=job_id, request_id=effective_request_id, workflow_step=workflow_step
        )
        system_prompt = self._prompt_manager.render_system_prompt(_PROMPT_ID)
        candidates_text = "\n".join(f"  - {c}" for c in request.targetFieldCandidates)
        user_prompt = self._prompt_manager.render_user_prompt(
            _PROMPT_ID,
            source_field=request.sourceField,
            source_datatype=request.sourceMetadata.datatype if request.sourceMetadata else "unknown",
            source_description=sanitize_prompt(
                request.sourceMetadata.description if request.sourceMetadata else "N/A"
            ),
            target_candidates=candidates_text,
            semantic_context=semantic_context,
        )

        # Step 4: LLM inference
        start_time = time.monotonic()
        try:
            llm_response = await self._llm.complete(
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                temperature=float(self._model_config.get("temperature", 0.1)),
                max_tokens=int(self._model_config.get("max_tokens", 2000)),
                job_id=job_id,
                request_id=effective_request_id,
                workflow_step=workflow_step,
            )
        except Exception as exc:
            logger.error(
                f"[SemanticMappingService] LLM inference FAILED | error={safe_exc(exc)} | "
                f"job_id={job_id} | request_id={effective_request_id}",
                category=LogCategories.LLM_CALL,
            )
            raise SemanticMappingException(
                f"Semantic mapping LLM inference failed: {str(exc)[:200]}",
                source_field=request.sourceField,
            )

        latency_ms = (time.monotonic() - start_time) * 1000

        # Step 5: Validate AI response
        validated_data, confidence = self._validator.validate_mapping_response(
            llm_response.content, job_id=job_id, request_id=effective_request_id
        )

        # Step 6: Audit token usage
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
            "[SemanticMappingService] Token usage audit | " + str(audit.to_log_dict()).replace("{", "{{").replace("}", "}}"),
            category=LogCategories.TOKEN_USAGE,
        )

        # Step 7: Build response
        confidence_level = classify_confidence(confidence).value
        alternatives = [
            MappingAlternativeDTO(field=a["field"], confidence=a.get("confidence", 0.0))
            for a in validated_data.get("alternatives", [])
            if isinstance(a, dict) and "field" in a
        ]

        response_dict = {
            "jobId": job_id,
            "mapping": validated_data["mapping"],
            "confidence": confidence,
            "confidenceLevel": confidence_level,
            "reasoning": validated_data.get("reasoning", ""),
            "alternatives": [a.model_dump() for a in alternatives],
            "promptVersion": prompt_version_tag,
            "cached": False,
            "requestId": effective_request_id,
            "promptTokens": llm_response.prompt_tokens if llm_response.prompt_tokens else None,
            "completionTokens": llm_response.completion_tokens if llm_response.completion_tokens else None,
            "totalTokens": llm_response.total_tokens if llm_response.total_tokens else None,
            "estimatedCostUsd": round(estimate_cost(
                llm_response.prompt_tokens, llm_response.completion_tokens, self._llm.model_name
            ), 6) if llm_response.total_tokens else None,
            "modelName": self._llm.model_name,
        }

        # Step 8: Cache high-confidence results
        self._cache.set(
            cache_key, response_dict, confidence=confidence,
            request_id=effective_request_id, job_id=job_id,
        )

        logger.info(
            f"[SemanticMappingService] Mapping inference COMPLETE | "
            f"mapping={validated_data['mapping']} | confidence={confidence:.3f} | "
            f"level={confidence_level} | latency_ms={latency_ms:.1f} | "
            f"job_id={job_id} | request_id={effective_request_id}",
            category=LogCategories.API_RESPONSE,
        )
        return SemanticMappingResponseDTO(**response_dict)


async def _gather_mapping_context(retriever, query, job_id, request_id):
    """Gather mapping context from multiple collections concurrently."""
    import asyncio
    historical, glossary, field_desc = await asyncio.gather(
        retriever.retrieve_historical_mappings(query, job_id=job_id, request_id=request_id),
        retriever.retrieve_glossary_terms(query, job_id=job_id, request_id=request_id),
        retriever.retrieve_field_descriptions(query, job_id=job_id, request_id=request_id),
        return_exceptions=True,
    )
    return (
        historical if not isinstance(historical, Exception) else [],
        glossary if not isinstance(glossary, Exception) else [],
        field_desc if not isinstance(field_desc, Exception) else [],
    )
