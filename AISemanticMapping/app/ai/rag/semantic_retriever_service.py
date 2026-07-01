"""
SemanticRetrieverService — RAG retrieval from ChromaDB vector store.
Retrieves semantically relevant context for LLM prompt enrichment.
"""
from typing import Dict, List, Optional

from loguru import logger

from app.ai.embeddings.embedding_service import EmbeddingService
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.vectorstore.ivectorstore import IVectorStore, VectorSearchResult
from app.infrastructure.vectorstore.vectorstore_factory import VectorStoreFactory
from app.shared.constants.app_constants import CollectionNames, LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import RAGException
from app.shared.utilities.sanitizer import safe_exc


class SemanticRetrieverService:
    """
    Retrieves semantically relevant context from the vector store.

    Supports retrieval from:
    - Historical field mappings
    - Business rules
    - Semantic glossary terms
    - Target schema descriptions
    - Field descriptions
    - Onboarding metadata

    Does NOT store transactional data, raw CSVs, or reconciliation outputs.
    """

    def __init__(
        self,
        vector_store: Optional[IVectorStore] = None,
        embedding_service: Optional[EmbeddingService] = None,
    ):
        self._vector_store = vector_store or VectorStoreFactory.create()
        self._embedding_service = embedding_service or EmbeddingService()
        self._config = get_app_config()
        self._top_k = self._config.vectorstore_top_k

        logger.info(
            f"[SemanticRetrieverService] Initialized | top_k={self._top_k}",
            category=LogCategories.RAG,
        )

    async def retrieve_historical_mappings(
        self,
        query: str,
        top_k: Optional[int] = None,
        job_id: str = "",
        request_id: str = "",
    ) -> List[VectorSearchResult]:
        """Retrieve similar historical field mappings from vector store."""
        return await self._retrieve(
            collection=CollectionNames.HISTORICAL_MAPPINGS,
            query=query,
            top_k=top_k,
            job_id=job_id,
            request_id=request_id,
        )

    async def retrieve_business_rules(
        self,
        query: str,
        top_k: Optional[int] = None,
        job_id: str = "",
        request_id: str = "",
    ) -> List[VectorSearchResult]:
        """Retrieve relevant business rules from vector store."""
        return await self._retrieve(
            collection=CollectionNames.BUSINESS_RULES,
            query=query,
            top_k=top_k,
            job_id=job_id,
            request_id=request_id,
        )

    async def retrieve_glossary_terms(
        self,
        query: str,
        top_k: Optional[int] = None,
        job_id: str = "",
        request_id: str = "",
    ) -> List[VectorSearchResult]:
        """Retrieve semantic glossary terms relevant to the query."""
        return await self._retrieve(
            collection=CollectionNames.SEMANTIC_GLOSSARY,
            query=query,
            top_k=top_k,
            job_id=job_id,
            request_id=request_id,
        )

    async def retrieve_field_descriptions(
        self,
        query: str,
        top_k: Optional[int] = None,
        job_id: str = "",
        request_id: str = "",
    ) -> List[VectorSearchResult]:
        """Retrieve field descriptions from target schema."""
        return await self._retrieve(
            collection=CollectionNames.FIELD_DESCRIPTIONS,
            query=query,
            top_k=top_k,
            job_id=job_id,
            request_id=request_id,
        )

    async def retrieve_from_collection(
        self,
        collection: str,
        query: str,
        top_k: Optional[int] = None,
        job_id: str = "",
        request_id: str = "",
        metadata_filter: Optional[Dict] = None,
    ) -> List[VectorSearchResult]:
        """Generic retrieval from any named collection (e.g. mainframe_patterns)."""
        return await self._retrieve(
            collection=collection,
            query=query,
            top_k=top_k,
            job_id=job_id,
            request_id=request_id,
            metadata_filter=metadata_filter,
        )

    async def _retrieve(
        self,
        collection: str,
        query: str,
        top_k: Optional[int],
        job_id: str,
        request_id: str,
        metadata_filter: Optional[Dict] = None,
    ) -> List[VectorSearchResult]:
        """
        Internal retrieval implementation.
        Generates query embedding and searches the vector store.
        Fails gracefully — returns empty list on error (RAG optional).
        """
        effective_top_k = top_k or self._top_k
        logger.debug(
            f"[SemanticRetrieverService] Retrieving | collection={collection} | "
            f"top_k={effective_top_k} | job_id={job_id} | request_id={request_id} | "
            f"workflow_step={WorkflowStepConstants.RAG_RETRIEVAL}",
            category=LogCategories.RAG,
        )

        try:
            query_embedding = await self._embedding_service.embed_text(
                query, job_id=job_id, request_id=request_id
            )
            if not query_embedding:
                logger.warning(
                    f"[SemanticRetrieverService] Empty embedding — skipping retrieval | "
                    f"collection={collection} | job_id={job_id}",
                    category=LogCategories.RAG,
                )
                return []

            results = await self._vector_store.search(
                collection_name=collection,
                query_embedding=query_embedding,
                top_k=effective_top_k,
                metadata_filter=metadata_filter,
            )

            logger.info(
                f"[SemanticRetrieverService] Retrieval complete | collection={collection} | "
                f"results={len(results)} | job_id={job_id} | request_id={request_id}",
                category=LogCategories.RAG,
            )
            return results

        except Exception as exc:
            # RAG is optional — log and continue without retrieval
            logger.warning(
                f"[SemanticRetrieverService] Retrieval FAILED (graceful fallback) | "
                f"collection={collection} | error={safe_exc(exc)} | job_id={job_id}",
                category=LogCategories.RAG,
            )
            return []
