"""
Semantic cache implementation using cachetools TTLCache.
Provides deterministic keying and confidence-gated caching.
"""
import time
from typing import Any, Optional

from cachetools import TTLCache
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.shared.constants.app_constants import LogCategories


class SemanticCache:
    """
    Thread-safe, TTL-based semantic cache.

    Caches high-confidence AI responses with deterministic keys.
    Does NOT cache workflow states, orchestration data, or low-confidence responses.
    """

    def __init__(self):
        config = get_app_config()
        self._enabled = config.cache_enabled
        self._min_confidence = config.cache_min_confidence
        self._ttl = config.cache_ttl_seconds
        self._max_size = config.cache_max_size
        self._cache: TTLCache = TTLCache(maxsize=self._max_size, ttl=self._ttl)
        self._hits = 0
        self._misses = 0
        self._evictions = 0
        logger.info(
            f"[SemanticCache] Initialized | enabled={self._enabled} | "
            f"ttl={self._ttl}s | max_size={self._max_size} | "
            f"min_confidence={self._min_confidence}",
            category=LogCategories.CACHE,
        )

    def get(self, cache_key: str, request_id: str = "", job_id: str = "") -> Optional[Any]:
        """
        Retrieve a cached value by key.

        Args:
            cache_key: Deterministic cache key string.
            request_id: Request ID for audit logging.
            job_id: Job ID for audit logging.

        Returns:
            Cached value or None if not found / cache disabled.
        """
        if not self._enabled:
            logger.debug(
                f"[SemanticCache] Cache disabled — skipping lookup | key={cache_key[:20]}...",
                category=LogCategories.CACHE,
            )
            return None

        value = self._cache.get(cache_key)
        if value is not None:
            self._hits += 1
            logger.info(
                f"[SemanticCache] CACHE HIT | key={cache_key[:20]}... | "
                f"job_id={job_id} | request_id={request_id} | "
                f"total_hits={self._hits}",
                category=LogCategories.CACHE,
            )
            return value
        else:
            self._misses += 1
            logger.debug(
                f"[SemanticCache] CACHE MISS | key={cache_key[:20]}... | "
                f"job_id={job_id} | request_id={request_id} | "
                f"total_misses={self._misses}",
                category=LogCategories.CACHE,
            )
            return None

    def set(
        self,
        cache_key: str,
        value: Any,
        confidence: float = 1.0,
        request_id: str = "",
        job_id: str = "",
    ) -> bool:
        """
        Store a value in cache. Only stores if confidence >= min_confidence.

        Args:
            cache_key: Deterministic cache key string.
            value: Value to cache.
            confidence: Confidence score of the response.
            request_id: Request ID for audit logging.
            job_id: Job ID for audit logging.

        Returns:
            True if stored, False if skipped.
        """
        if not self._enabled:
            return False

        if confidence < self._min_confidence:
            logger.debug(
                f"[SemanticCache] CACHE SKIP (low confidence) | "
                f"confidence={confidence:.3f} | min={self._min_confidence} | "
                f"job_id={job_id} | request_id={request_id}",
                category=LogCategories.CACHE,
            )
            return False

        self._cache[cache_key] = value
        logger.info(
            f"[SemanticCache] CACHE STORE | key={cache_key[:20]}... | "
            f"confidence={confidence:.3f} | job_id={job_id} | request_id={request_id}",
            category=LogCategories.CACHE,
        )
        return True

    def invalidate(self, cache_key: str) -> bool:
        """Remove a specific key from cache."""
        if cache_key in self._cache:
            del self._cache[cache_key]
            logger.info(
                f"[SemanticCache] CACHE INVALIDATED | key={cache_key[:20]}...",
                category=LogCategories.CACHE,
            )
            return True
        return False

    def clear(self) -> None:
        """Clear all cached entries."""
        size = len(self._cache)
        self._cache.clear()
        logger.warning(
            f"[SemanticCache] CACHE CLEARED | evicted={size} entries",
            category=LogCategories.CACHE,
        )

    def stats(self) -> dict:
        """Return cache statistics."""
        return {
            "enabled": self._enabled,
            "current_size": len(self._cache),
            "max_size": self._max_size,
            "ttl_seconds": self._ttl,
            "hits": self._hits,
            "misses": self._misses,
            "hit_rate": round(self._hits / max(self._hits + self._misses, 1), 3),
        }


# Module-level singleton
_cache_instance: Optional[SemanticCache] = None


def get_semantic_cache() -> SemanticCache:
    """Return the singleton SemanticCache instance."""
    global _cache_instance
    if _cache_instance is None:
        _cache_instance = SemanticCache()
    return _cache_instance
