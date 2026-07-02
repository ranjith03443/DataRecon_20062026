"""
Azure OpenAI LLM Provider implementation.
Communicates with Azure OpenAI REST API via httpx for async support.
Implements retry logic, token tracking, and structured logging.
"""
import asyncio
import json
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


class AzureOpenAIProvider(ILLMProvider):
    """
    Azure OpenAI LLM provider using the Chat Completions REST API.

    Reads all credentials from environment variables via AppConfig.
    Never stores secrets as class attributes in plaintext.
    """

    def __init__(self, model_config: Optional[dict] = None):
        config = get_app_config()
        self._config = config
        self._model_config = model_config or config.get_model_config("semantic_mapping")
        self._model = self._model_config.get("model_name", "gpt-4o-mini")
        self._deployment = config.azure_openai_deployment_name or self._model
        self._api_version = config.azure_openai_api_version
        self._endpoint = config.azure_openai_endpoint
        self._timeout = float(self._model_config.get("request_timeout", 60))

        if not self._endpoint:
            logger.warning(
                "[AzureOpenAIProvider] AZURE_OPENAI_ENDPOINT not set — provider will fail on inference.",
                category=LogCategories.LLM_CALL,
            )

        logger.info(
            f"[AzureOpenAIProvider] Initialized | model={self._model} | "
            f"deployment={self._deployment} | api_version={self._api_version}",
            category=LogCategories.LLM_CALL,
        )

    @property
    def provider_name(self) -> str:
        return "azure_openai"

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
        """
        Send a chat completion request to Azure OpenAI.
        Implements exponential backoff retry on transient failures.
        """
        api_key = self._config.azure_openai_api_key
        if not self._endpoint or not api_key:
            raise ProviderException(
                message="Azure OpenAI endpoint or API key not configured.",
                provider_name=self.provider_name,
                model_name=self._model,
            )

        url = (
            f"{self._endpoint.rstrip('/')}/openai/deployments/"
            f"{self._deployment}/chat/completions?api-version={self._api_version}"
        )
        headers = {
            "Content-Type": "application/json",
            "api-key": api_key,
        }
        payload = {
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": temperature,
            "max_tokens": max_tokens,
            "top_p": float(self._model_config.get("top_p", 1.0)),
        }
        if kwargs.get("json_mode"):
            payload["response_format"] = {"type": "json_object"}

        last_exception = None
        for attempt in range(_MAX_RETRIES):
            start_time = time.monotonic()
            try:
                logger.debug(
                    f"[AzureOpenAIProvider] Sending completion request | "
                    f"model={self._model} | attempt={attempt + 1}/{_MAX_RETRIES} | "
                    f"job_id={job_id} | request_id={request_id} | workflow_step={workflow_step}",
                    category=LogCategories.LLM_CALL,
                )
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, headers=headers, json=payload)
                    latency_ms = (time.monotonic() - start_time) * 1000
                    response.raise_for_status()
                    data = response.json()

                content = data["choices"][0]["message"]["content"]
                usage = data.get("usage", {})
                prompt_tokens = usage.get("prompt_tokens", 0)
                completion_tokens = usage.get("completion_tokens", 0)
                total_tokens = usage.get("total_tokens", 0)

                logger.info(
                    f"[AzureOpenAIProvider] Completion SUCCESS | model={self._model} | "
                    f"prompt_tokens={prompt_tokens} | completion_tokens={completion_tokens} | "
                    f"total_tokens={total_tokens} | latency_ms={latency_ms:.1f} | "
                    f"job_id={job_id} | request_id={request_id}",
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
                    f"[AzureOpenAIProvider] HTTP error | status={status} | "
                    f"attempt={attempt + 1} | job_id={job_id} | request_id={request_id} | "
                    f"latency_ms={latency_ms:.1f}",
                    category=LogCategories.LLM_CALL,
                )
                last_exception = ProviderException(
                    message=f"Azure OpenAI HTTP {status}: {exc.response.text[:200]}",
                    provider_name=self.provider_name,
                    model_name=self._model,
                    retry_count=attempt + 1,
                )
                if status in (429, 500, 502, 503, 504) and attempt < _MAX_RETRIES - 1:
                    backoff = _RETRY_BACKOFF_SECONDS[attempt]
                    logger.info(
                        f"[AzureOpenAIProvider] Retrying after {backoff}s | attempt={attempt + 1}",
                        category=LogCategories.LLM_CALL,
                    )
                    await asyncio.sleep(backoff)
                    continue
                break

            except (httpx.ConnectError, httpx.TimeoutException) as exc:
                latency_ms = (time.monotonic() - start_time) * 1000
                logger.warning(
                    f"[AzureOpenAIProvider] Connection/Timeout error | attempt={attempt + 1} | "
                    f"error={safe_exc(exc, 100)} | job_id={job_id} | latency_ms={latency_ms:.1f}",
                    category=LogCategories.LLM_CALL,
                )
                last_exception = ProviderException(
                    message=f"Azure OpenAI connection error: {str(exc)[:100]}",
                    provider_name=self.provider_name,
                    model_name=self._model,
                    retry_count=attempt + 1,
                )
                if attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                    continue
                break

        logger.error(
            f"[AzureOpenAIProvider] All {_MAX_RETRIES} attempts FAILED | "
            f"job_id={job_id} | request_id={request_id}",
            category=LogCategories.LLM_CALL,
        )
        raise last_exception or ProviderException(
            "Azure OpenAI request failed after all retries.",
            provider_name=self.provider_name,
        )

    async def health_check(self) -> bool:
        """Verify Azure OpenAI connectivity with a minimal request."""
        try:
            logger.debug("[AzureOpenAIProvider] Running health check...", category=LogCategories.HEALTH)
            response = await self.complete(
                system_prompt="You are a health check assistant.",
                user_prompt="Reply with the word: OK",
                temperature=0.0,
                max_tokens=10,
                workflow_step="health_check",
            )
            healthy = "OK" in response.content.upper()
            logger.info(
                f"[AzureOpenAIProvider] Health check result: {'HEALTHY' if healthy else 'DEGRADED'}",
                category=LogCategories.HEALTH,
            )
            return healthy
        except Exception as exc:
            logger.error(
                f"[AzureOpenAIProvider] Health check FAILED: {safe_exc(exc)}",
                category=LogCategories.HEALTH,
            )
            return False
