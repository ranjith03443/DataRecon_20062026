"""
Router for POST /api/rule-inference
"""
from fastapi import APIRouter, Depends, Request
from loguru import logger

from app.application.dto.rule_inference_dto import RuleInferenceRequestDTO, RuleInferenceResponseDTO
from app.application.services.rule_inference_service import RuleInferenceService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/rule-inference", tags=["Rule Inference"])


def _get_service() -> RuleInferenceService:
    return RuleInferenceService()


@router.post(
    "",
    response_model=RuleInferenceResponseDTO,
    summary="Infer transformation operation from business rule",
    description=(
        "Interprets an ambiguous business rule description and infers the structured "
        "transformation operation. Advisory only — does not execute transformations."
    ),
)
async def infer_rule(
    request: Request,
    body: RuleInferenceRequestDTO,
    service: RuleInferenceService = Depends(_get_service),
) -> RuleInferenceResponseDTO:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[RuleInferenceRouter] POST /rule-inference | "
        f"rule={body.rule[:60]}... | request_id={request_id}",
        category=LogCategories.API_REQUEST,
    )
    return await service.infer_rule(body, request_id=request_id)
