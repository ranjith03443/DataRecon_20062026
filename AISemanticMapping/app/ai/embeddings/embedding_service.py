"""
EmbeddingService — Embedding generation with caching and normalization.
Wraps the embedding provider with cache, retry logging, and audit tracking.
"""
import hashlib
import time
from typing import Dict, List, Optional

from loguru import logger

from app.infrastructure.cache.semantic_cache import get_semantic_cache
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.embeddings.embedding_factory import EmbeddingProviderFactory
from app.infrastructure.providers.embeddings.iembedding_provider import IEmbeddingProvider
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import EmbeddingException


class EmbeddingService:
    """
    Manages embedding generation with:
    - Provider abstraction via EmbeddingProviderFactory
    - TTL-based embedding cache
    - Embedding normalization
    - Detailed audit logging
    """

    def __init__(self, provider: Optional[IEmbeddingProvider] = None):
        self._provider = provider or EmbeddingProviderFactory.create()
        self._cache = get_semantic_cache()
        self._config = get_app_config()
        logger.info(
            f"[EmbeddingService] Initialized | provider={self._provider.provider_name} | "
            f"model={self._provider.model_name}",
            category=LogCategories.EMBEDDING,
        )

    async def embed_text(
        self,
        text: str,
        job_id: str = "",
        request_id: str = "",
    ) -> List[float]:
        """
        Generate embedding for a single text with caching.

        Args:
            text: Text to embed.
            job_id: Job ID for audit logging.
            request_id: Request ID for audit logging.

        Returns:
            List of floats (embedding vector).
        """
        results = await self.embed_texts([text], job_id=job_id, request_id=request_id)
        return results[0] if results else []

    async def embed_texts(
        self,
        texts: List[str],
        job_id: str = "",
        request_id: str = "",
    ) -> List[List[float]]:
        """
        Generate embeddings for multiple texts with per-text caching.

        Args:
            texts: List of texts to embed.
            job_id: Job ID for audit logging.
            request_id: Request ID for audit logging.

        Returns:
            List of embedding vectors.
        """
        logger.debug(
            f"[EmbeddingService] embed_texts called | count={len(texts)} | "
            f"job_id={job_id} | request_id={request_id}",
            category=LogCategories.EMBEDDING,
        )

        cached_results: Dict[int, List[float]] = {}
        uncached_indices: List[int] = []
        uncached_texts: List[str] = []

        for i, text in enumerate(texts):
            cache_key = f"embedding:{hashlib.sha256(text.encode()).hexdigest()[:20]}"
            cached = self._cache.get(cache_key, request_id=request_id, job_id=job_id)
            if cached is not None:
                cached_results[i] = cached
            else:
                uncached_indices.append(i)
                uncached_texts.append(text)

        if uncached_texts:
            try:
                start_time = time.monotonic()
                result = await self._provider.embed(uncached_texts)
                latency_ms = (time.monotonic() - start_time) * 1000

                logger.info(
                    f"[EmbeddingService] Embeddings generated via provider | "
                    f"count={len(uncached_texts)} | latency_ms={latency_ms:.1f} | "
                    f"provider={self._provider.provider_name} | model={self._provider.model_name} | "
                    f"job_id={job_id} | request_id={request_id}",
                    category=LogCategories.EMBEDDING,
                )

                for j, idx in enumerate(uncached_indices):
                    embedding = result.embeddings[j]
                    normalized = self._normalize(embedding)
                    cached_results[idx] = normalized
                    cache_key = f"embedding:{hashlib.sha256(uncached_texts[j].encode()).hexdigest()[:20]}"
                    self._cache.set(
                        cache_key, normalized, confidence=1.0,
                        request_id=request_id, job_id=job_id,
                    )

            except EmbeddingException as exc:
                logger.error(
                    f"[EmbeddingService] Embedding generation FAILED | error={str(exc)[:200]} | "
                    f"job_id={job_id} | request_id={request_id} | "
                    f"workflow_step={WorkflowStepConstants.EMBEDDING_GENERATION}",
                    category=LogCategories.EMBEDDING,
                )
                # Graceful fallback: return zero vectors
                for idx in uncached_indices:
                    cached_results[idx] = []

        return [cached_results.get(i, []) for i in range(len(texts))]

    @staticmethod
    def _normalize(embedding: List[float]) -> List[float]:
        """L2-normalize an embedding vector."""
        import math
        magnitude = math.sqrt(sum(v * v for v in embedding))
        if magnitude == 0:
            return embedding
        return [v / magnitude for v in embedding]
