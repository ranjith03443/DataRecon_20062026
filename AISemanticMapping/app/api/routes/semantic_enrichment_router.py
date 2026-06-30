"""
Router for POST /api/semantic-enrichment
"""
from fastapi import APIRouter, Depends, Request
from loguru import logger

from app.application.dto.schema_enrichment_dto import SemanticEnrichmentRequestDTO, SemanticEnrichmentResponseDTO
from app.application.services.semantic_enrichment_service import SemanticEnrichmentService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/semantic-enrichment", tags=["Schema Enrichment"])


def _get_service() -> SemanticEnrichmentService:
    return SemanticEnrichmentService()


@router.post(
    "",
    response_model=SemanticEnrichmentResponseDTO,
    summary="Enrich field with semantic meaning",
    description=(
        "Infers the semantic meaning, expands abbreviations, and enriches a field name "
        "with business context using LLM and RAG retrieval."
    ),
)
async def enrich_schema_field(
    request: Request,
    body: SemanticEnrichmentRequestDTO,
    service: SemanticEnrichmentService = Depends(_get_service),
) -> SemanticEnrichmentResponseDTO:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[SemanticEnrichmentRouter] POST /semantic-enrichment | "
        f"fieldName={body.fieldName} | request_id={request_id}",
        category=LogCategories.API_REQUEST,
    )
    return await service.enrich_field(body, request_id=request_id)
