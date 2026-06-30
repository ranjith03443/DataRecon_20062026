"""
OpenAI LLM Provider implementation (non-Azure).
Provides a concrete ILLMProvider for the standard OpenAI API.
"""
import asyncio
import time
from typing import Any, Optional

import httpx
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider, LLMResponse
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import ProviderException
import os

_MAX_RETRIES = 3
_RETRY_BACKOFF_SECONDS = [1.0, 2.0, 4.0]


class OpenAIProvider(ILLMProvider):
    """Standard OpenAI API provider implementation."""

    def __init__(self, model_config: Optional[dict] = None):
        config = get_app_config()
        self._model_config = model_config or config.get_model_config("semantic_mapping")
        self._model = self._model_config.get("model_name", "gpt-4o-mini")
        self._base_url = config.get_provider_config("openai").get("base_url", "https://api.openai.com/v1")
        self._timeout = float(self._model_config.get("request_timeout", 60))
        logger.info(
            f"[OpenAIProvider] Initialized | model={self._model}",
            category=LogCategories.LLM_CALL,
        )

    @property
    def provider_name(self) -> str:
        return "openai"

    @property
    def model_name(self) -> str:
        return self._model

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        temperature: float = 0.1,
        max_tokens: int = 2000,
        **kwargs: Any,
    ) -> LLMResponse:
        api_key = os.getenv("OPENAI_API_KEY", "")
        if not api_key:
            raise ProviderException("OPENAI_API_KEY not configured.", provider_name=self.provider_name)

        url = f"{self._base_url}/chat/completions"
        headers = {"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"}
        payload = {
            "model": self._model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": temperature,
            "max_tokens": max_tokens,
        }

        for attempt in range(_MAX_RETRIES):
            start_time = time.monotonic()
            try:
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, headers=headers, json=payload)
                    latency_ms = (time.monotonic() - start_time) * 1000
                    response.raise_for_status()
                    data = response.json()
                content = data["choices"][0]["message"]["content"]
                usage = data.get("usage", {})
                return LLMResponse(
                    content=content,
                    model_name=self._model,
                    provider_name=self.provider_name,
                    prompt_tokens=usage.get("prompt_tokens", 0),
                    completion_tokens=usage.get("completion_tokens", 0),
                    total_tokens=usage.get("total_tokens", 0),
                    latency_ms=latency_ms,
                )
            except Exception as exc:
                if attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                else:
                    raise ProviderException(str(exc), provider_name=self.provider_name)

    async def health_check(self) -> bool:
        try:
            r = await self.complete("You are a health check.", "Reply: OK", max_tokens=10, temperature=0)
            return "OK" in r.content.upper()
        except Exception:
            return False
