"""
Health check router — GET /api/health
Validates FastAPI, Azure OpenAI, ChromaDB, and cache availability.
"""
import time
from fastapi import APIRouter
from loguru import logger

from app.application.dto.health_dto import HealthResponseDTO, ProviderStatusDTO
from app.infrastructure.cache.semantic_cache import get_semantic_cache
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.infrastructure.providers.embeddings.embedding_factory import EmbeddingProviderFactory
from app.infrastructure.vectorstore.vectorstore_factory import VectorStoreFactory
from app.shared.constants.app_constants import AppConstants, LogCategories

router = APIRouter(tags=["Health"])
_start_time = time.monotonic()


@router.get(
    "/ping",
    summary="Lightweight liveness probe",
    description="Instant liveness probe — no external provider checks. Used by .NET availability guard.",
)
async def ping():
    """Returns immediately. Used by the C# AI availability check."""
    return {"status": "ok", "uptime": round(time.monotonic() - _start_time, 2)}


@router.get(
    "/health",
    response_model=HealthResponseDTO,
    summary="Platform health check",
    description="Validates connectivity to LLM provider, vector store, and cache.",
)
async def health_check() -> HealthResponseDTO:
    config = get_app_config()
    logger.info("[HealthRouter] Health check requested", category=LogCategories.HEALTH)

    provider_statuses = []

    # LLM provider health
    try:
        llm = LLMProviderFactory.create()
        llm_healthy = await llm.health_check()
        provider_statuses.append(
            ProviderStatusDTO(
                name=f"llm/{config.llm_provider}",
                healthy=llm_healthy,
                details=f"model={llm.model_name}",
            )
        )
    except Exception as exc:
        provider_statuses.append(
            ProviderStatusDTO(name=f"llm/{config.llm_provider}", healthy=False, details=str(exc)[:100])
        )

    # Embedding provider health
    try:
        emb = EmbeddingProviderFactory.create()
        emb_healthy = await emb.health_check()
        provider_statuses.append(
            ProviderStatusDTO(
                name=f"embeddings/{config.embedding_provider}",
                healthy=emb_healthy,
                details=f"model={emb.model_name}",
            )
        )
    except Exception as exc:
        provider_statuses.append(
            ProviderStatusDTO(
                name=f"embeddings/{config.embedding_provider}", healthy=False, details=str(exc)[:100]
            )
        )

    # Vector store health
    try:
        vs = VectorStoreFactory.create()
        vs_healthy = await vs.health_check()
        provider_statuses.append(
            ProviderStatusDTO(
                name=f"vectorstore/{config.vectorstore_provider}",
                healthy=vs_healthy,
            )
        )
    except Exception as exc:
        provider_statuses.append(
            ProviderStatusDTO(
                name=f"vectorstore/{config.vectorstore_provider}", healthy=False, details=str(exc)[:100]
            )
        )

    # Cache status
    cache = get_semantic_cache()
    cache_stats = cache.stats()
    cache_status = "HEALTHY" if cache_stats["enabled"] else "DISABLED"

    all_healthy = all(p.healthy for p in provider_statuses)
    any_healthy = any(p.healthy for p in provider_statuses)
    if all_healthy:
        overall_status = "HEALTHY"
    elif any_healthy:
        overall_status = "DEGRADED"
    else:
        overall_status = "UNHEALTHY"

    uptime = time.monotonic() - _start_time

    logger.info(
        f"[HealthRouter] Health check complete | status={overall_status} | "
        f"providers={len(provider_statuses)}",
        category=LogCategories.HEALTH,
    )

    return HealthResponseDTO(
        status=overall_status,
        version=AppConstants.API_VERSION,
        environment=config.environment,
        providers=provider_statuses,
        cacheStatus=cache_status,
        ragEnabled=config.enable_rag,
        semanticCacheEnabled=config.enable_semantic_cache,
        uptimeSeconds=round(uptime, 2),
    )
