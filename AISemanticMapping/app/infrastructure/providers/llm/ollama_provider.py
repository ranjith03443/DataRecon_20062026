"""
Ollama LLM Provider stub implementation.
Supports future local LLM inference via Ollama REST API.
Does NOT download local models at startup.
"""
import asyncio
import time
from typing import Any

import httpx
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider, LLMResponse
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import ProviderException


class OllamaProvider(ILLMProvider):
    """Ollama REST API provider. Connects to a locally running Ollama service."""

    def __init__(self, model_config: dict = None):
        config = get_app_config()
        provider_cfg = config.get_provider_config("ollama")
        self._model_config = model_config or config.get_model_config("semantic_mapping")
        self._model = self._model_config.get("model_name", provider_cfg.get("default_model", "llama3"))
        self._base_url = provider_cfg.get("base_url", "http://localhost:11434")
        self._timeout = float(self._model_config.get("request_timeout", 120))
        logger.info(
            f"[OllamaProvider] Initialized | model={self._model} | base_url={self._base_url}",
            category=LogCategories.LLM_CALL,
        )

    @property
    def provider_name(self) -> str:
        return "ollama"

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
        url = f"{self._base_url}/api/chat"
        payload = {
            "model": self._model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "stream": False,
            "options": {"temperature": temperature, "num_predict": max_tokens},
        }
        start_time = time.monotonic()
        try:
            async with httpx.AsyncClient(timeout=self._timeout) as client:
                response = await client.post(url, json=payload)
                latency_ms = (time.monotonic() - start_time) * 1000
                response.raise_for_status()
                data = response.json()
            content = data.get("message", {}).get("content", "")
            return LLMResponse(
                content=content,
                model_name=self._model,
                provider_name=self.provider_name,
                latency_ms=latency_ms,
            )
        except Exception as exc:
            raise ProviderException(
                f"Ollama request failed: {str(exc)[:200]}",
                provider_name=self.provider_name,
                model_name=self._model,
            )

    async def health_check(self) -> bool:
        try:
            async with httpx.AsyncClient(timeout=5.0) as client:
                response = await client.get(f"{self._base_url}/api/tags")
                return response.status_code == 200
        except Exception:
            return False
