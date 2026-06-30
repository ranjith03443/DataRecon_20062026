"""
Global exception handler middleware for FastAPI.
Converts all platform exceptions to structured JSON error responses.
"""
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from loguru import logger

from app.shared.constants.app_constants import LogCategories
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
        logger.warning(
            f"[ExceptionHandler] AuthenticationException | path={request.url.path} | "
            f"message={exc.message}",
            category=LogCategories.SECURITY,
        )
        return JSONResponse(status_code=401, content=exc.to_dict())

    @app.exception_handler(AuthorizationException)
    async def authz_exception_handler(request: Request, exc: AuthorizationException):
        logger.warning(
            f"[ExceptionHandler] AuthorizationException | path={request.url.path}",
            category=LogCategories.SECURITY,
        )
        return JSONResponse(status_code=403, content=exc.to_dict())

    @app.exception_handler(ValidationException)
    async def validation_exception_handler(request: Request, exc: ValidationException):
        logger.warning(
            f"[ExceptionHandler] ValidationException | {exc.message}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(status_code=422, content=exc.to_dict())

    @app.exception_handler(ConfigurationException)
    async def config_exception_handler(request: Request, exc: ConfigurationException):
        logger.error(
            f"[ExceptionHandler] ConfigurationException | {exc.message}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(ProviderException)
    async def provider_exception_handler(request: Request, exc: ProviderException):
        logger.error(
            f"[ExceptionHandler] ProviderException | provider={exc.provider_name} | {exc.message}",
            category=LogCategories.LLM_CALL,
        )
        return JSONResponse(status_code=502, content=exc.to_dict())

    @app.exception_handler(EmbeddingException)
    async def embedding_exception_handler(request: Request, exc: EmbeddingException):
        logger.error(
            f"[ExceptionHandler] EmbeddingException | {exc.message}",
            category=LogCategories.EMBEDDING,
        )
        return JSONResponse(status_code=502, content=exc.to_dict())

    @app.exception_handler(VectorStoreException)
    async def vectorstore_exception_handler(request: Request, exc: VectorStoreException):
        logger.error(
            f"[ExceptionHandler] VectorStoreException | {exc.message}",
            category=LogCategories.VECTOR_STORE,
        )
        return JSONResponse(status_code=503, content=exc.to_dict())

    @app.exception_handler(AIResponseValidationException)
    async def ai_validation_exception_handler(request: Request, exc: AIResponseValidationException):
        logger.error(
            f"[ExceptionHandler] AIResponseValidationException | {exc.message}",
            category=LogCategories.AUDIT,
        )
        return JSONResponse(status_code=422, content=exc.to_dict())

    @app.exception_handler(SemanticMappingException)
    async def mapping_exception_handler(request: Request, exc: SemanticMappingException):
        logger.error(
            f"[ExceptionHandler] SemanticMappingException | {exc.message}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(RuleInferenceException)
    async def rule_exception_handler(request: Request, exc: RuleInferenceException):
        logger.error(
            f"[ExceptionHandler] RuleInferenceException | {exc.message}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(SemanticPlatformException)
    async def platform_exception_handler(request: Request, exc: SemanticPlatformException):
        logger.error(
            f"[ExceptionHandler] SemanticPlatformException | {exc.error_code} | {exc.message}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(status_code=500, content=exc.to_dict())

    @app.exception_handler(Exception)
    async def generic_exception_handler(request: Request, exc: Exception):
        logger.exception(
            f"[ExceptionHandler] Unhandled exception | path={request.url.path} | "
            f"type={type(exc).__name__} | error={str(exc)[:200]}",
            category=LogCategories.API_REQUEST,
        )
        return JSONResponse(
            status_code=500,
            content={
                "error_code": "INTERNAL_SERVER_ERROR",
                "message": "An unexpected error occurred. Please review server logs.",
            },
        )
