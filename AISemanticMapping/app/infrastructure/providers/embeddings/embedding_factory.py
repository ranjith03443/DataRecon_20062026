"""
Embedding Provider Factory — Configuration-driven embedding provider selection.
"""
from loguru import logger

from app.domain.enums.provider_enums import EmbeddingProviderType
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.embeddings.iembedding_provider import IEmbeddingProvider
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import ConfigurationException


class EmbeddingProviderFactory:
    """Factory for creating embedding provider instances."""

    @staticmethod
    def create(provider_override: str = None) -> IEmbeddingProvider:
        """
        Create and return an IEmbeddingProvider implementation.

        Args:
            provider_override: Optional provider name override.

        Returns:
            IEmbeddingProvider implementation.
        """
        config = get_app_config()
        provider_name = (provider_override or config.embedding_provider).lower()

        logger.info(
            f"[EmbeddingProviderFactory] Creating embedding provider | provider={provider_name}",
            category=LogCategories.EMBEDDING,
        )

        if provider_name == EmbeddingProviderType.AZURE_OPENAI:
            from app.infrastructure.providers.embeddings.azure_openai_embedding_provider import (
                AzureOpenAIEmbeddingProvider,
            )
            return AzureOpenAIEmbeddingProvider()

        elif provider_name == EmbeddingProviderType.OPENAI:
            # Future implementation
            raise ConfigurationException(
                "OpenAI embedding provider not yet implemented.",
                config_key="embedding_provider",
            )

        elif provider_name == EmbeddingProviderType.HUGGINGFACE:
            raise ConfigurationException(
                "HuggingFace embedding provider not yet implemented.",
                config_key="embedding_provider",
            )

        else:
            raise ConfigurationException(
                f"Unknown embedding provider: '{provider_name}'. "
                f"Supported: {[e.value for e in EmbeddingProviderType]}",
                config_key="embedding_provider",
            )
