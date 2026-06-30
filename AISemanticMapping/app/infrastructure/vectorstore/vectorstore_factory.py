"""
Vector Store Factory — Configuration-driven vector store selection.
"""
from loguru import logger

from app.domain.enums.provider_enums import VectorStoreProviderType
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.vectorstore.ivectorstore import IVectorStore
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import ConfigurationException


class VectorStoreFactory:
    """Factory for creating IVectorStore provider instances."""

    @staticmethod
    def create(provider_override: str = None) -> IVectorStore:
        config = get_app_config()
        provider_name = (provider_override or config.vectorstore_provider).lower()
        logger.info(
            f"[VectorStoreFactory] Creating vector store | provider={provider_name}",
            category=LogCategories.VECTOR_STORE,
        )
        if provider_name == VectorStoreProviderType.CHROMADB:
            from app.infrastructure.vectorstore.chromadb_store import ChromaDBStore
            return ChromaDBStore()
        else:
            raise ConfigurationException(
                f"Unknown vector store provider: '{provider_name}'. "
                f"Supported: {[e.value for e in VectorStoreProviderType]}",
                config_key="vectorstore_provider",
            )
