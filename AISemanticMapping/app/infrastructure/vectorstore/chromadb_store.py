"""
ChromaDB Vector Store implementation.
Provides persistent local vector storage using ChromaDB.
"""
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional

import chromadb
from chromadb.config import Settings
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.vectorstore.ivectorstore import IVectorStore, VectorSearchResult
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import VectorStoreException
from app.shared.utilities.sanitizer import safe_exc


class ChromaDBStore(IVectorStore):
    """
    ChromaDB persistent vector store implementation.

    Manages collections for semantic metadata, rules, glossary, and field descriptions.
    Does NOT store raw CSV rows or transactional data.
    """

    def __init__(self):
        config = get_app_config()
        persist_dir = config.chromadb_persist_dir
        Path(persist_dir).mkdir(parents=True, exist_ok=True)

        try:
            self._client = chromadb.PersistentClient(
                path=persist_dir,
                settings=Settings(anonymized_telemetry=False),
            )
            logger.info(
                f"[ChromaDBStore] Initialized | persist_dir={persist_dir}",
                category=LogCategories.VECTOR_STORE,
            )
        except Exception as exc:
            logger.error(
                f"[ChromaDBStore] Failed to initialize ChromaDB | error={safe_exc(exc)}",
                category=LogCategories.VECTOR_STORE,
            )
            raise VectorStoreException(
                f"ChromaDB initialization failed: {str(exc)[:200]}",
                operation="init",
            )

    def _get_collection(self, collection_name: str):
        """Get or create a ChromaDB collection."""
        try:
            return self._client.get_or_create_collection(
                name=collection_name,
                metadata={"hnsw:space": "cosine"},
            )
        except Exception as exc:
            raise VectorStoreException(
                f"Failed to get/create collection '{collection_name}': {str(exc)[:200]}",
                collection=collection_name,
                operation="get_or_create",
            )

    async def index_documents(
        self,
        collection_name: str,
        documents: List[str],
        embeddings: List[List[float]],
        metadatas: Optional[List[Dict[str, Any]]] = None,
        ids: Optional[List[str]] = None,
    ) -> int:
        """Index documents with embeddings into a ChromaDB collection."""
        if not documents:
            logger.warning(
                f"[ChromaDBStore] index_documents called with empty document list | "
                f"collection={collection_name}",
                category=LogCategories.VECTOR_STORE,
            )
            return 0

        if len(documents) != len(embeddings):
            raise VectorStoreException(
                f"Mismatch: {len(documents)} documents vs {len(embeddings)} embeddings.",
                collection=collection_name,
                operation="index",
            )

        doc_ids = ids or [str(uuid.uuid4()) for _ in documents]
        raw_metadatas = metadatas or [{} for _ in documents]
        # ChromaDB 0.5+ requires non-empty metadata dicts — add a sentinel if empty
        doc_metadatas = [m if m else {"_src": "indexed"} for m in raw_metadatas]

        try:
            collection = self._get_collection(collection_name)
            collection.upsert(
                documents=documents,
                embeddings=embeddings,
                metadatas=doc_metadatas,
                ids=doc_ids,
            )
            logger.info(
                f"[ChromaDBStore] Indexed {len(documents)} documents | "
                f"collection={collection_name}",
                category=LogCategories.VECTOR_STORE,
            )
            return len(documents)
        except VectorStoreException:
            raise
        except Exception as exc:
            logger.error(
                f"[ChromaDBStore] Indexing FAILED | collection={collection_name} | "
                f"error={safe_exc(exc)}",
                category=LogCategories.VECTOR_STORE,
            )
            raise VectorStoreException(
                f"Failed to index documents: {str(exc)[:200]}",
                collection=collection_name,
                operation="index",
            )

    async def search(
        self,
        collection_name: str,
        query_embedding: List[float],
        top_k: int = 5,
        metadata_filter: Optional[Dict[str, Any]] = None,
    ) -> List[VectorSearchResult]:
        """Search collection by vector similarity using cosine distance."""
        try:
            collection = self._get_collection(collection_name)
            count = collection.count()
            if count == 0:
                logger.debug(
                    f"[ChromaDBStore] Collection empty — no results | collection={collection_name}",
                    category=LogCategories.VECTOR_STORE,
                )
                return []

            effective_top_k = min(top_k, count)
            query_kwargs: Dict[str, Any] = {
                "query_embeddings": [query_embedding],
                "n_results": effective_top_k,
                "include": ["documents", "metadatas", "distances"],
            }
            if metadata_filter:
                query_kwargs["where"] = metadata_filter

            results = collection.query(**query_kwargs)

            search_results = []
            for i, (doc, meta, dist, doc_id) in enumerate(zip(
                results["documents"][0],
                results["metadatas"][0],
                results["distances"][0],
                results["ids"][0],
            )):
                # Convert cosine distance to similarity score
                score = round(1.0 - dist, 4)
                search_results.append(
                    VectorSearchResult(
                        document=doc,
                        metadata=meta,
                        score=score,
                        document_id=doc_id,
                    )
                )

            logger.info(
                f"[ChromaDBStore] Search complete | collection={collection_name} | "
                f"top_k={effective_top_k} | results={len(search_results)}",
                category=LogCategories.VECTOR_STORE,
            )
            return search_results

        except VectorStoreException:
            raise
        except Exception as exc:
            logger.error(
                f"[ChromaDBStore] Search FAILED | collection={collection_name} | "
                f"error={safe_exc(exc)}",
                category=LogCategories.VECTOR_STORE,
            )
            raise VectorStoreException(
                f"Vector search failed: {str(exc)[:200]}",
                collection=collection_name,
                operation="search",
            )

    async def collection_exists(self, collection_name: str) -> bool:
        try:
            existing = [c.name for c in self._client.list_collections()]
            return collection_name in existing
        except Exception:
            return False

    async def create_collection(self, collection_name: str) -> None:
        self._get_collection(collection_name)
        logger.info(
            f"[ChromaDBStore] Collection ensured | collection={collection_name}",
            category=LogCategories.VECTOR_STORE,
        )

    async def delete_collection(self, collection_name: str) -> None:
        try:
            self._client.delete_collection(collection_name)
            logger.warning(
                f"[ChromaDBStore] Collection deleted | collection={collection_name}",
                category=LogCategories.VECTOR_STORE,
            )
        except Exception as exc:
            raise VectorStoreException(
                f"Failed to delete collection '{collection_name}': {str(exc)[:200]}",
                collection=collection_name,
                operation="delete",
            )

    async def get_collection_count(self, collection_name: str) -> int:
        try:
            collection = self._get_collection(collection_name)
            return collection.count()
        except Exception:
            return 0

    async def health_check(self) -> bool:
        try:
            self._client.list_collections()
            logger.debug("[ChromaDBStore] Health check: HEALTHY", category=LogCategories.HEALTH)
            return True
        except Exception as exc:
            logger.error(
                f"[ChromaDBStore] Health check FAILED: {safe_exc(exc)}",
                category=LogCategories.HEALTH,
            )
            return False
