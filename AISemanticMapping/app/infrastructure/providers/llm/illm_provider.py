"""
ILLMProvider — Abstract interface for all LLM providers.
All concrete providers must implement this interface (SOLID/DIP).
"""
from abc import ABC, abstractmethod
from typing import Any, Dict, List, Optional


class LLMResponse:
    """Structured response from an LLM provider."""

    def __init__(
        self,
        content: str,
        model_name: str,
        provider_name: str,
        prompt_tokens: int = 0,
        completion_tokens: int = 0,
        total_tokens: int = 0,
        latency_ms: float = 0.0,
        raw_response: Optional[Any] = None,
    ):
        self.content = content
        self.model_name = model_name
        self.provider_name = provider_name
        self.prompt_tokens = prompt_tokens
        self.completion_tokens = completion_tokens
        self.total_tokens = total_tokens
        self.latency_ms = latency_ms
        self.raw_response = raw_response


class ILLMProvider(ABC):
    """
    Abstract base class for all LLM provider implementations.
    Enforces provider-agnostic contract for LLM inference.
    """

    @property
    @abstractmethod
    def provider_name(self) -> str:
        """Return the canonical provider name."""
        ...

    @property
    @abstractmethod
    def model_name(self) -> str:
        """Return the active model name."""
        ...

    @abstractmethod
    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        temperature: float = 0.1,
        max_tokens: int = 2000,
        **kwargs: Any,
    ) -> LLMResponse:
        """
        Send a completion request to the LLM provider.

        Args:
            system_prompt: System instruction prompt.
            user_prompt: User content prompt.
            temperature: Sampling temperature.
            max_tokens: Maximum completion tokens.

        Returns:
            LLMResponse with content and token usage.
        """
        ...

    @abstractmethod
    async def health_check(self) -> bool:
        """
        Verify connectivity to the LLM provider.

        Returns:
            True if provider is reachable and healthy.
        """
        ...
