"""
Router for /api/mainframe/* — Mainframe Development AI Agent endpoints.

POST /api/mainframe/explain          — COBOL Explain Agent
POST /api/mainframe/review           — COBOL Review Agent
POST /api/mainframe/enhance          — COBOL Enhancement Agent
POST /api/mainframe/validation       — Validation Logic Agent
POST /api/mainframe/error-handling   — Error Handling Agent
POST /api/mainframe/documentation    — Developer Documentation Agent
POST /api/mainframe/jcl-improvements — JCL Improvement Agent
POST /api/mainframe/optimization     — Optimisation Agent
"""
from fastapi import APIRouter, Depends, Request
from loguru import logger

from app.application.dto.mainframe_agent_dto import (
    MainframeAgentRequestDTO,
    MainframeAgentResponseDTO,
)
from app.application.services.mainframe_agent_service import MainframeAgentService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/mainframe", tags=["Mainframe Development AI Agent"])

# Module-level singleton — avoids recreating EmbeddingService/LLMProvider on every request.
_service: MainframeAgentService | None = None


def _get_service() -> MainframeAgentService:
    global _service
    if _service is None:
        _service = MainframeAgentService()
    return _service


def _log(request: Request, prompt_type: str, body: MainframeAgentRequestDTO) -> None:
    rid = getattr(request.state, "request_id", None)
    logger.info(
        f"[MainframeAgentRouter] POST /mainframe/{prompt_type} | "
        f"jobId={body.jobId} | request_id={rid}",
        category=LogCategories.MAINFRAME_AI_AGENT,
    )


@router.post(
    "/explain",
    response_model=MainframeAgentResponseDTO,
    summary="Explain COBOL skeleton in plain English",
    description=(
        "Produces a business-language explanation of each section and field in the "
        "provided COBOL skeleton, including value-mapping logic. "
        "AI Generated — Developer Review Required."
    ),
)
async def explain_cobol(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "explain", body)
    body.promptType = "explain"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/review",
    response_model=MainframeAgentResponseDTO,
    summary="Review COBOL skeleton for issues",
    description=(
        "Analyses the COBOL skeleton and reports missing validations, error handling, "
        "PIC clause issues, field length concerns, and performance risks. "
        "AI Generated — Developer Review Required."
    ),
)
async def review_cobol(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "review", body)
    body.promptType = "review"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/enhance",
    response_model=MainframeAgentResponseDTO,
    summary="Generate COBOL enhancement suggestions",
    description=(
        "Generates COBOL enhancement snippets: IF/EVALUATE blocks, validation sections, "
        "reusable routines, and naming improvements. "
        "Does NOT overwrite the original skeleton. "
        "AI Generated — Developer Review Required."
    ),
)
async def enhance_cobol(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "enhance", body)
    body.promptType = "enhance"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/validation",
    response_model=MainframeAgentResponseDTO,
    summary="Generate COBOL validation logic",
    description=(
        "Generates COBOL validation examples for mandatory field checks, length checks, "
        "date validation, numeric validation, and business rule validation. "
        "AI Generated — Developer Review Required."
    ),
)
async def generate_validation(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "validation", body)
    body.promptType = "validation"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/error-handling",
    response_model=MainframeAgentResponseDTO,
    summary="Generate COBOL error handling recommendations",
    description=(
        "Generates recommendations for invalid record handling, reject files, "
        "error counters, audit logging, and exception handling. "
        "AI Generated — Developer Review Required."
    ),
)
async def generate_error_handling(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "error-handling", body)
    body.promptType = "error_handling"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/documentation",
    response_model=MainframeAgentResponseDTO,
    summary="Generate developer documentation",
    description=(
        "Generates a structured developer guide covering business overview, "
        "field mapping summary, transformation summary, value mapping summary, "
        "copybook explanation, COBOL guidance, JCL guidance, and implementation notes. "
        "AI Generated — Developer Review Required."
    ),
)
async def generate_documentation(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "documentation", body)
    body.promptType = "documentation"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/jcl-improvements",
    response_model=MainframeAgentResponseDTO,
    summary="Generate JCL improvement recommendations",
    description=(
        "Analyses the JCL skeleton and provides recommendations for missing DD statements, "
        "dataset parameters, scheduling suggestions, and best practices. "
        "AI Generated — Developer Review Required."
    ),
)
async def generate_jcl_improvements(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "jcl-improvements", body)
    body.promptType = "jcl_improvements"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))


@router.post(
    "/optimization",
    response_model=MainframeAgentResponseDTO,
    summary="Generate COBOL optimisation suggestions",
    description=(
        "Provides optimisation suggestions: replacing nested IFs with EVALUATE, "
        "reusable routines, modular structure, and performance improvements. "
        "AI Generated — Developer Review Required."
    ),
)
async def generate_optimization(
    request: Request,
    body: MainframeAgentRequestDTO,
    service: MainframeAgentService = Depends(_get_service),
) -> MainframeAgentResponseDTO:
    _log(request, "optimization", body)
    body.promptType = "optimization"
    return await service.run_agent(body, request_id=getattr(request.state, "request_id", None))
