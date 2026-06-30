"""
Domain enumerations for AI providers.
"""
from enum import Enum


class LLMProviderType(str, Enum):
    """Supported LLM provider types."""
    AZURE_OPENAI = "azure_openai"
    OPENAI = "openai"
    OLLAMA = "ollama"
    HUGGINGFACE = "huggingface"
    MISTRAL = "mistral"
    CLAUDE = "claude"
    GEMINI = "gemini"


class EmbeddingProviderType(str, Enum):
    """Supported embedding provider types."""
    AZURE_OPENAI = "azure_openai"
    OPENAI = "openai"
    HUGGINGFACE = "huggingface"


class VectorStoreProviderType(str, Enum):
    """Supported vector store provider types."""
    CHROMADB = "chromadb"
    PINECONE = "pinecone"
    QDRANT = "qdrant"
    WEAVIATE = "weaviate"
