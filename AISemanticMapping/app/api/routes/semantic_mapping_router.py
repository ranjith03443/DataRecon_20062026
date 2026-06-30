"""
Router for POST /api/semantic-mapping
"""
from fastapi import APIRouter, Depends, Request
from loguru import logger

from app.application.dto.semantic_mapping_dto import SemanticMappingRequestDTO, SemanticMappingResponseDTO
from app.application.services.semantic_mapping_service import SemanticMappingService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/semantic-mapping", tags=["Semantic Mapping"])


def _get_service() -> SemanticMappingService:
    return SemanticMappingService()


@router.post(
    "",
    response_model=SemanticMappingResponseDTO,
    summary="Infer semantic field mapping",
    description=(
        "Infers the best source-to-target field mapping using semantic similarity, "
        "RAG-retrieved historical mappings, and LLM inference. "
        "Advisory only — does not execute transformations."
    ),
)
async def infer_semantic_mapping(
    request: Request,
    body: SemanticMappingRequestDTO,
    service: SemanticMappingService = Depends(_get_service),
) -> SemanticMappingResponseDTO:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[SemanticMappingRouter] POST /semantic-mapping | "
        f"jobId={body.jobId} | sourceField={body.sourceField} | request_id={request_id}",
        category=LogCategories.API_REQUEST,
    )
    return await service.infer_mapping(body, request_id=request_id)
