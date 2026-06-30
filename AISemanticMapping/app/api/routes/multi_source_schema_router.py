"""
Router: POST /api/multi-source-schema
Accepts schemas from N source files, returns a unified target schema with
source-field-to-target-field mappings and merge rules.
"""
from fastapi import APIRouter, Request
from loguru import logger

from app.application.dto.multi_source_schema_dto import (
    MultiSourceSchemaRequestDto,
    MultiSourceSchemaResponseDto,
)
from app.application.services.multi_source_schema_service import MultiSourceSchemaService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/multi-source-schema", tags=["Multi-Source Schema"])


@router.post("", response_model=MultiSourceSchemaResponseDto)
async def generate_multi_source_schema(
    request: Request,
    body: MultiSourceSchemaRequestDto,
) -> MultiSourceSchemaResponseDto:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[MultiSourceSchemaRouter] POST /multi-source-schema | "
        f"schema={body.schema_name} | files={len(body.source_files)} | rid={request_id}",
        category=LogCategories.API_REQUEST,
    )
    service = MultiSourceSchemaService()
    return await service.generate(body)
