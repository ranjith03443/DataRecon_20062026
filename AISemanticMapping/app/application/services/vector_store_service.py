"""
VectorStoreService — Manages vector indexing and semantic search operations.
"""
import uuid
from typing import Optional

from loguru import logger

from app.ai.embeddings.embedding_service import EmbeddingService
from app.application.dto.vector_store_dto import (
    VectorIndexRequestDTO,
    VectorIndexResponseDTO,
    VectorSearchRequestDTO,
    VectorSearchResponseDTO,
    VectorSearchResultItemDTO,
)
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.vectorstore.ivectorstore import IVectorStore
from app.infrastructure.vectorstore.vectorstore_factory import VectorStoreFactory
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import VectorStoreException
from app.shared.utilities.sanitizer import sanitize_prompt, safe_exc


class VectorStoreService:
    """
    Manages vector indexing and semantic search.

    Only indexes:
    - Metadata documents
    - Semantic rules
    - Field descriptions
    - Semantic knowledge

    Does NOT index raw CSV rows or transactional data.
    """

    def __init__(
        self,
        vector_store: Optional[IVectorStore] = None,
        embedding_service: Optional[EmbeddingService] = None,
    ):
        self._config = get_app_config()
        self._vector_store = vector_store or VectorStoreFactory.create()
        self._embedding_service = embedding_service or EmbeddingService()

        logger.info(
            f"[VectorStoreService] Initialized",
            category=LogCategories.VECTOR_STORE,
        )

    async def index_documents(
        self,
        request: VectorIndexRequestDTO,
        request_id: Optional[str] = None,
    ) -> VectorIndexResponseDTO:
        """Index documents into a vector store collection."""
        effective_request_id = request_id or request.requestId or str(uuid.uuid4())
        job_id = request.jobId or ""

        logger.info(
            f"[VectorStoreService] Indexing documents | collection={request.collection} | "
            f"count={len(request.documents)} | job_id={job_id} | request_id={effective_request_id} | "
            f"workflow_step={WorkflowStepConstants.VECTOR_INDEXING}",
            category=LogCategories.VECTOR_STORE,
        )

        try:
            # Generate embeddings for all documents
            embeddings = await self._embedding_service.embed_texts(
                request.documents, job_id=job_id, request_id=effective_request_id
            )

            # Filter documents with failed embeddings
            valid_docs = []
            valid_embeddings = []
            valid_metas = []
            valid_ids = []

            for i, (doc, emb) in enumerate(zip(request.documents, embeddings)):
                if emb:
                    valid_docs.append(doc)
                    valid_embeddings.append(emb)
                    valid_metas.append(request.metadatas[i] if request.metadatas else {})
                    valid_ids.append(
                        request.ids[i] if request.ids else str(uuid.uuid4())
                    )
                else:
                    logger.warning(
                        f"[VectorStoreService] Skipping doc {i} — empty embedding | "
                        f"job_id={job_id}",
                        category=LogCategories.VECTOR_STORE,
                    )

            count = await self._vector_store.index_documents(
                collection_name=request.collection,
                documents=valid_docs,
                embeddings=valid_embeddings,
                metadatas=valid_metas,
                ids=valid_ids,
            )

            logger.info(
                f"[VectorStoreService] Indexing COMPLETE | collection={request.collection} | "
                f"indexed={count} | job_id={job_id}",
                category=LogCategories.VECTOR_STORE,
            )
            return VectorIndexResponseDTO(
                collection=request.collection,
                documentsIndexed=count,
                success=True,
                requestId=effective_request_id,
            )

        except Exception as exc:
            logger.error(
                f"[VectorStoreService] Indexing FAILED | collection={request.collection} | "
                f"error={safe_exc(exc)} | job_id={job_id}",
                category=LogCategories.VECTOR_STORE,
            )
            raise VectorStoreException(
                f"Vector indexing failed: {str(exc)[:200]}",
                collection=request.collection,
                operation="index",
            )

    async def search(
        self,
        request: VectorSearchRequestDTO,
        request_id: Optional[str] = None,
    ) -> VectorSearchResponseDTO:
        """Perform semantic similarity search in a vector collection."""
        effective_request_id = request_id or request.requestId or str(uuid.uuid4())
        job_id = request.jobId or ""

        logger.info(
            f"[VectorStoreService] Search START | collection={request.collection} | "
            f"top_k={request.topK} | job_id={job_id} | request_id={effective_request_id} | "
            f"workflow_step={WorkflowStepConstants.VECTOR_SEARCH}",
            category=LogCategories.VECTOR_STORE,
        )

        try:
            query = sanitize_prompt(request.query)
            query_embedding = await self._embedding_service.embed_text(
                query, job_id=job_id, request_id=effective_request_id
            )
            if not query_embedding:
                logger.warning(
                    f"[VectorStoreService] Empty query embedding | job_id={job_id}",
                    category=LogCategories.VECTOR_STORE,
                )
                return VectorSearchResponseDTO(
                    collection=request.collection,
                    query=request.query,
                    results=[],
                    totalResults=0,
                    requestId=effective_request_id,
                )

            raw_results = await self._vector_store.search(
                collection_name=request.collection,
                query_embedding=query_embedding,
                top_k=request.topK,
                metadata_filter=request.metadataFilter,
            )

            results = [
                VectorSearchResultItemDTO(
                    documentId=r.document_id,
                    document=r.document,
                    metadata=r.metadata,
                    score=r.score,
                )
                for r in raw_results
            ]

            logger.info(
                f"[VectorStoreService] Search COMPLETE | collection={request.collection} | "
                f"results={len(results)} | job_id={job_id}",
                category=LogCategories.VECTOR_STORE,
            )
            return VectorSearchResponseDTO(
                collection=request.collection,
                query=request.query,
                results=results,
                totalResults=len(results),
                requestId=effective_request_id,
            )

        except Exception as exc:
            logger.error(
                f"[VectorStoreService] Search FAILED | collection={request.collection} | "
                f"error={safe_exc(exc)} | job_id={job_id}",
                category=LogCategories.VECTOR_STORE,
            )
            raise VectorStoreException(
                f"Vector search failed: {str(exc)[:200]}",
                collection=request.collection,
                operation="search",
            )
