"""
IEmbeddingProvider — Abstract interface for all embedding providers.
"""
from abc import ABC, abstractmethod
from typing import List


class EmbeddingResult:
    """Structured result from an embedding provider."""

    def __init__(
        self,
        embeddings: List[List[float]],
        model_name: str,
        provider_name: str,
        total_tokens: int = 0,
        latency_ms: float = 0.0,
    ):
        self.embeddings = embeddings
        self.model_name = model_name
        self.provider_name = provider_name
        self.total_tokens = total_tokens
        self.latency_ms = latency_ms


class IEmbeddingProvider(ABC):
    """Abstract base for all embedding provider implementations."""

    @property
    @abstractmethod
    def provider_name(self) -> str:
        ...

    @property
    @abstractmethod
    def model_name(self) -> str:
        ...

    @abstractmethod
    async def embed(self, texts: List[str]) -> EmbeddingResult:
        """
        Generate embeddings for a list of text inputs.

        Args:
            texts: List of strings to embed.

        Returns:
            EmbeddingResult with embedding vectors and metadata.
        """
        ...

    @abstractmethod
    async def health_check(self) -> bool:
        ...
