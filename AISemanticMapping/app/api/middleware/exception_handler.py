"""
Global exception handler middleware for FastAPI.
Converts all platform exceptions to structured JSON error responses.
"""
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from loguru import logger

from app.shared.constants.app_constants import LogCategories
from app.shared.utilities.sanitizer import safe_exc
from app.shared.exceptions.base_exceptions import (
    AIResponseValidationException,
    AuthenticationException,
    AuthorizationException,
    ConfigurationException,
    EmbeddingException,
    PromptException,
    ProviderException,
    RAGException,
    RuleInferenceException,
    SemanticMappingException,
    SemanticPlatformException,
    ValidationException,
    VectorStoreException,
)


def register_exception_handlers(app: FastAPI) -> None:
    """Register all exception handlers on the FastAPI app."""

    @app.exception_handler(AuthenticationException)
    async def auth_exception_handler(request: Request, exc: AuthenticationException):
        logger.bind(category=LogCategories.SECURITY).warning(
            f"[ExceptionHandler] AuthenticationException | path={request.url.path} | "
            f"message={exc.message}"
        )
        return JSONResponse(status_code=401, content=exc.to_dict())

    @app.exception_handler(AuthorizationException)
    async def authz_exception_handler(request: Request, exc: AuthorizationException):
        logger.bind(category=LogCategories.SECURITY).warning(
            f"[ExceptionHandler] AuthorizationException | path={request.url.path}"
        )
        return JSONResponse(status_code=403, content=exc.to_dict())

    @app.exception_handler(ValidationException)
    async def validation_exception_handler(request: Request, exc: ValidationException):
        logger.bind(category=LogCategories.API_REQUEST).warning(
            f"[ExceptionHandler] ValidationException | {exc.message}"
        )
        return JSONResponse(status_code=422, content=exc.to_dict())

    @app.exception_handler(ConfigurationException)
    async def config_exception_handler(request: Request, exc: ConfigurationException):
        logger.bind(category=LogCategories.API_REQUEST).error(
            f"[ExceptionHandler] ConfigurationException | {exc.message}"
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(ProviderException)
    async def provider_exception_handler(request: Request, exc: ProviderException):
        logger.bind(category=LogCategories.LLM_CALL).error(
            f"[ExceptionHandler] ProviderException | provider={exc.provider_name} | {exc.message}"
        )
        return JSONResponse(status_code=502, content=exc.to_dict())

    @app.exception_handler(EmbeddingException)
    async def embedding_exception_handler(request: Request, exc: EmbeddingException):
        logger.bind(category=LogCategories.EMBEDDING).error(
            f"[ExceptionHandler] EmbeddingException | {exc.message}"
        )
        return JSONResponse(status_code=502, content=exc.to_dict())

    @app.exception_handler(VectorStoreException)
    async def vectorstore_exception_handler(request: Request, exc: VectorStoreException):
        logger.bind(category=LogCategories.VECTOR_STORE).error(
            f"[ExceptionHandler] VectorStoreException | {exc.message}"
        )
        return JSONResponse(status_code=503, content=exc.to_dict())

    @app.exception_handler(AIResponseValidationException)
    async def ai_validation_exception_handler(request: Request, exc: AIResponseValidationException):
        logger.bind(category=LogCategories.AUDIT).error(
            f"[ExceptionHandler] AIResponseValidationException | {exc.message}"
        )
        return JSONResponse(status_code=422, content=exc.to_dict())

    @app.exception_handler(SemanticMappingException)
    async def mapping_exception_handler(request: Request, exc: SemanticMappingException):
        logger.bind(category=LogCategories.API_REQUEST).error(
            f"[ExceptionHandler] SemanticMappingException | {exc.message}"
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(RuleInferenceException)
    async def rule_exception_handler(request: Request, exc: RuleInferenceException):
        logger.bind(category=LogCategories.API_REQUEST).error(
            f"[ExceptionHandler] RuleInferenceException | {exc.message}"
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(SemanticPlatformException)
    async def platform_exception_handler(request: Request, exc: SemanticPlatformException):
        logger.bind(category=LogCategories.API_REQUEST).error(
            f"[ExceptionHandler] SemanticPlatformException | {exc.error_code} | {exc.message}"
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(Exception)
    async def generic_exception_handler(request: Request, exc: Exception):
        logger.bind(category=LogCategories.API_REQUEST).exception(
            f"[ExceptionHandler] Unhandled exception | path={request.url.path} | "
            f"type={type(exc).__name__} | error={safe_exc(exc)}"
        )
        return JSONResponse(
            status_code=500,
            content={
                "error_code": "INTERNAL_SERVER_ERROR",
                "message": "An unexpected error occurred. Please review server logs.",
            },
        )
