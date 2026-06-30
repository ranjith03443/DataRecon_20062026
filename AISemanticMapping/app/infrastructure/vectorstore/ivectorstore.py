"""
IVectorStore — Abstract interface for vector database providers.
"""
from abc import ABC, abstractmethod
from typing import Any, Dict, List, Optional


class VectorSearchResult:
    """Single result from a vector similarity search."""

    def __init__(
        self,
        document: str,
        metadata: Dict[str, Any],
        score: float,
        document_id: str,
    ):
        self.document = document
        self.metadata = metadata
        self.score = score
        self.document_id = document_id

    def to_dict(self) -> dict:
        return {
            "document": self.document,
            "metadata": self.metadata,
            "score": self.score,
            "document_id": self.document_id,
        }


class IVectorStore(ABC):
    """Abstract interface for vector store providers."""

    @abstractmethod
    async def index_documents(
        self,
        collection_name: str,
        documents: List[str],
        embeddings: List[List[float]],
        metadatas: Optional[List[Dict[str, Any]]] = None,
        ids: Optional[List[str]] = None,
    ) -> int:
        """Index documents with their embeddings into a collection."""
        ...

    @abstractmethod
    async def search(
        self,
        collection_name: str,
        query_embedding: List[float],
        top_k: int = 5,
        metadata_filter: Optional[Dict[str, Any]] = None,
    ) -> List[VectorSearchResult]:
        """Search a collection by vector similarity."""
        ...

    @abstractmethod
    async def collection_exists(self, collection_name: str) -> bool:
        """Check if a collection exists."""
        ...

    @abstractmethod
    async def create_collection(self, collection_name: str) -> None:
        """Create a collection if it does not exist."""
        ...

    @abstractmethod
    async def delete_collection(self, collection_name: str) -> None:
        """Delete a collection."""
        ...

    @abstractmethod
    async def get_collection_count(self, collection_name: str) -> int:
        """Return the number of documents in a collection."""
        ...

    @abstractmethod
    async def health_check(self) -> bool:
        """Verify vector store connectivity."""
        ...
