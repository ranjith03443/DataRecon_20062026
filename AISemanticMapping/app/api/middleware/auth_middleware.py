"""
Authentication and Authorization middleware.
Supports demo mode (bypass all auth) and production API key validation.
All bypass actions are logged per security audit requirements.
"""
from typing import Callable

from fastapi import Request, Response
from loguru import logger
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.responses import JSONResponse

from app.infrastructure.config.config_loader import get_app_config
from app.shared.constants.app_constants import AppConstants, LogCategories


_EXCLUDED_PATHS = {"/api/health", "/docs", "/openapi.json", "/redoc"}


class AuthMiddleware(BaseHTTPMiddleware):
    """
    API key authentication middleware.

    In demo/bypass mode: all requests pass through with audit log.
    In production mode: validates X-API-Key header against configured keys.
    """

    def __init__(self, app, **kwargs):
        super().__init__(app, **kwargs)
        self._config = get_app_config()

    async def dispatch(self, request: Request, call_next: Callable) -> Response:
        # Skip auth for excluded paths
        if request.url.path in _EXCLUDED_PATHS:
            return await call_next(request)

        config = self._config
        request_id = getattr(request.state, "request_id", "")
        job_id = getattr(request.state, "job_id", "")

        if not config.security_enabled:
            logger.debug(
                f"[AuthMiddleware] Security disabled — request trusted | "
                f"path={request.url.path} | request_id={request_id}",
                category=LogCategories.SECURITY,
            )
            return await call_next(request)

        if config.bypass_authentication:
            logger.warning(
                f"[AuthMiddleware] BYPASS_AUTHENTICATION=true — demo mode | "
                f"path={request.url.path} | request_id={request_id} | job_id={job_id}",
                category=LogCategories.SECURITY,
            )
            request.state.authenticated = True
            request.state.authorized = True
            return await call_next(request)

        # Production API key validation
        api_key = request.headers.get(AppConstants.DEFAULT_API_KEY_HEADER, "")
        valid_keys = config.valid_api_keys

        if not api_key:
            logger.warning(
                f"[AuthMiddleware] REJECTED — missing API key | "
                f"path={request.url.path} | request_id={request_id}",
                category=LogCategories.SECURITY,
            )
            return JSONResponse(
                status_code=401,
                content={"error": "AUTHENTICATION_ERROR", "message": "API key required."},
            )

        if valid_keys and api_key not in valid_keys:
            logger.warning(
                f"[AuthMiddleware] REJECTED — invalid API key | "
                f"path={request.url.path} | request_id={request_id}",
                category=LogCategories.SECURITY,
            )
            return JSONResponse(
                status_code=401,
                content={"error": "AUTHENTICATION_ERROR", "message": "Invalid API key."},
            )

        logger.info(
            f"[AuthMiddleware] Authenticated | path={request.url.path} | request_id={request_id}",
            category=LogCategories.SECURITY,
        )
        request.state.authenticated = True
        request.state.authorized = True
        return await call_next(request)
