"""
Request logging and correlation tracking middleware.
Adds X-Request-ID and X-Correlation-ID to all requests.
Logs all incoming requests and response times.
"""
import time
import uuid
from typing import Callable

from fastapi import Request, Response
from loguru import logger
from starlette.middleware.base import BaseHTTPMiddleware

from app.shared.constants.app_constants import AppConstants, LogCategories


class RequestLoggingMiddleware(BaseHTTPMiddleware):
    """
    Middleware that:
    - Assigns X-Request-ID and X-Correlation-ID to every request
    - Logs all incoming API requests with method, path, headers context
    - Logs response status and execution time
    - Attaches correlation IDs to response headers
    """

    async def dispatch(self, request: Request, call_next: Callable) -> Response:
        # Generate or inherit correlation IDs
        request_id = request.headers.get(AppConstants.DEFAULT_REQUEST_ID_HEADER, str(uuid.uuid4()))
        correlation_id = request.headers.get(AppConstants.DEFAULT_CORRELATION_ID_HEADER, request_id)
        job_id = request.headers.get(AppConstants.DEFAULT_JOB_ID_HEADER, "")

        # Store in request state for downstream access
        request.state.request_id = request_id
        request.state.correlation_id = correlation_id
        request.state.job_id = job_id

        start_time = time.monotonic()

        logger.info(
            f"[RequestLoggingMiddleware] Incoming request | "
            f"method={request.method} | path={request.url.path} | "
            f"request_id={request_id} | correlation_id={correlation_id} | "
            f"job_id={job_id} | client={request.client.host if request.client else 'unknown'}",
            category=LogCategories.API_REQUEST,
        )

        try:
            response = await call_next(request)
        except Exception as exc:
            latency_ms = (time.monotonic() - start_time) * 1000
            logger.error(
                f"[RequestLoggingMiddleware] Unhandled exception | "
                f"method={request.method} | path={request.url.path} | "
                f"error={str(exc)[:200]} | latency_ms={latency_ms:.1f} | "
                f"request_id={request_id} | correlation_id={correlation_id}",
                category=LogCategories.API_REQUEST,
            )
            raise

        latency_ms = (time.monotonic() - start_time) * 1000
        logger.info(
            f"[RequestLoggingMiddleware] Response sent | "
            f"method={request.method} | path={request.url.path} | "
            f"status={response.status_code} | latency_ms={latency_ms:.1f} | "
            f"request_id={request_id} | correlation_id={correlation_id}",
            category=LogCategories.API_RESPONSE,
        )

        # Inject correlation IDs into response headers
        response.headers[AppConstants.DEFAULT_REQUEST_ID_HEADER] = request_id
        response.headers[AppConstants.DEFAULT_CORRELATION_ID_HEADER] = correlation_id
        return response
