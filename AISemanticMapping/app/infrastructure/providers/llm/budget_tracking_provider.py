"""
BudgetTrackingProvider — transparent decorator that wraps any ILLMProvider
and records each completed call to the budget log.
"""
from typing import Any

from app.infrastructure.budget import budget_logger
from app.infrastructure.providers.llm.illm_provider import ILLMProvider, LLMResponse


class BudgetTrackingProvider(ILLMProvider):
    """Delegates all calls to the inner provider, then logs token usage."""

    def __init__(self, inner: ILLMProvider, task: str = "unknown"):
        self._inner = inner
        self._task = task

    @property
    def provider_name(self) -> str:
        return self._inner.provider_name

    @property
    def model_name(self) -> str:
        return self._inner.model_name

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        temperature: float = 0.1,
        max_tokens: int = 2000,
        **kwargs: Any,
    ) -> LLMResponse:
        response = await self._inner.complete(
            system_prompt, user_prompt, temperature, max_tokens, **kwargs
        )
        budget_logger.record(
            provider_name=response.provider_name,
            model_name=response.model_name,
            task=self._task,
            prompt_tokens=response.prompt_tokens,
            completion_tokens=response.completion_tokens,
            total_tokens=response.total_tokens,
            latency_ms=response.latency_ms,
        )
        return response

    async def health_check(self) -> bool:
        return await self._inner.health_check()
