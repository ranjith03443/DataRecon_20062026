"""
Google Gemini LLM Provider implementation.
Uses the Generative Language REST API (v1beta).
"""
import asyncio
import os
import time
from typing import Any, Optional

import httpx
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider, LLMResponse
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import ProviderException
from app.shared.utilities.sanitizer import safe_exc


_MAX_RETRIES = 3
_RETRY_BACKOFF_SECONDS = [1.0, 2.0, 4.0]
_GEMINI_BASE_URL = "https://generativelanguage.googleapis.com/v1beta/models"


class GeminiProvider(ILLMProvider):
    """
    Google Gemini LLM provider using the generateContent REST API.
    Reads GEMINI_API_KEY from environment variables.
    """

    def __init__(self, model_config: Optional[dict] = None):
        config = get_app_config()
        self._model_config = model_config or config.get_model_config("semantic_mapping")
        self._model = self._model_config.get("model_name", "gemini-1.5-flash")
        self._timeout = float(self._model_config.get("request_timeout", 60))

        logger.info(
            f"[GeminiProvider] Initialized | model={self._model}",
            category=LogCategories.LLM_CALL,
        )

    @property
    def provider_name(self) -> str:
        return "gemini"

    @property
    def model_name(self) -> str:
        return self._model

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        temperature: float = 0.1,
        max_tokens: int = 2000,
        job_id: str = "",
        request_id: str = "",
        workflow_step: str = WorkflowStepConstants.LLM_INFERENCE,
        **kwargs: Any,
    ) -> LLMResponse:
        api_key = os.getenv("GEMINI_API_KEY", "")
        if not api_key:
            raise ProviderException(
                message="GEMINI_API_KEY not configured.",
                provider_name=self.provider_name,
                model_name=self._model,
            )

        url = f"{_GEMINI_BASE_URL}/{self._model}:generateContent?key={api_key}"
        # Gemini combines system + user into a single contents array
        payload = {
            "contents": [
                {
                    "role": "user",
                    "parts": [{"text": f"{system_prompt}\n\n{user_prompt}"}],
                }
            ],
            "generationConfig": {
                "temperature": temperature,
                "maxOutputTokens": max_tokens,
            },
        }

        last_exception = None
        for attempt in range(_MAX_RETRIES):
            start_time = time.monotonic()
            try:
                logger.debug(
                    f"[GeminiProvider] Sending request | model={self._model} | "
                    f"attempt={attempt + 1}/{_MAX_RETRIES} | job_id={job_id}",
                    category=LogCategories.LLM_CALL,
                )
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, json=payload)
                    latency_ms = (time.monotonic() - start_time) * 1000
                    response.raise_for_status()
                    data = response.json()

                content = data["candidates"][0]["content"]["parts"][0]["text"]
                usage = data.get("usageMetadata", {})
                prompt_tokens = usage.get("promptTokenCount", 0)
                completion_tokens = usage.get("candidatesTokenCount", 0)
                total_tokens = usage.get("totalTokenCount", prompt_tokens + completion_tokens)

                logger.info(
                    f"[GeminiProvider] Completion SUCCESS | model={self._model} | "
                    f"prompt_tokens={prompt_tokens} | completion_tokens={completion_tokens} | "
                    f"latency_ms={latency_ms:.1f} | job_id={job_id}",
                    category=LogCategories.LLM_CALL,
                )
                return LLMResponse(
                    content=content,
                    model_name=self._model,
                    provider_name=self.provider_name,
                    prompt_tokens=prompt_tokens,
                    completion_tokens=completion_tokens,
                    total_tokens=total_tokens,
                    latency_ms=latency_ms,
                    raw_response=data,
                )

            except httpx.HTTPStatusError as exc:
                latency_ms = (time.monotonic() - start_time) * 1000
                status = exc.response.status_code
                logger.warning(
                    f"[GeminiProvider] HTTP error | status={status} | attempt={attempt + 1} | "
                    f"job_id={job_id} | latency_ms={latency_ms:.1f}",
                    category=LogCategories.LLM_CALL,
                )
                last_exception = ProviderException(
                    message=f"Gemini HTTP {status}: {exc.response.text[:200]}",
                    provider_name=self.provider_name,
                    model_name=self._model,
                    retry_count=attempt + 1,
                )
                if status in (429, 500, 502, 503, 504) and attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                    continue
                break

            except (httpx.ConnectError, httpx.TimeoutException) as exc:
                latency_ms = (time.monotonic() - start_time) * 1000
                logger.warning(
                    f"[GeminiProvider] Connection/Timeout | attempt={attempt + 1} | "
                    f"error={safe_exc(exc, 100)} | job_id={job_id}",
                    category=LogCategories.LLM_CALL,
                )
                last_exception = ProviderException(
                    message=f"Gemini connection error: {str(exc)[:100]}",
                    provider_name=self.provider_name,
                    model_name=self._model,
                    retry_count=attempt + 1,
                )
                if attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                    continue
                break

        logger.error(
            f"[GeminiProvider] All {_MAX_RETRIES} attempts FAILED | job_id={job_id}",
            category=LogCategories.LLM_CALL,
        )
        raise last_exception or ProviderException(
            "Gemini request failed after all retries.",
            provider_name=self.provider_name,
        )

    async def health_check(self) -> bool:
        try:
            logger.debug("[GeminiProvider] Running health check...", category=LogCategories.HEALTH)
            response = await self.complete(
                system_prompt="You are a health check assistant.",
                user_prompt="Reply with the word: OK",
                temperature=0.0,
                max_tokens=10,
                workflow_step="health_check",
            )
            healthy = "OK" in response.content.upper()
            logger.info(
                f"[GeminiProvider] Health check: {'HEALTHY' if healthy else 'DEGRADED'}",
                category=LogCategories.HEALTH,
            )
            return healthy
        except Exception as exc:
            logger.error(
                f"[GeminiProvider] Health check FAILED: {safe_exc(exc)}",
                category=LogCategories.HEALTH,
            )
            return False
