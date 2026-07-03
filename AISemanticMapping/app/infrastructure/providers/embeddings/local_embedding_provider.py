"""
LocalEmbeddingProvider — Uses sentence-transformers for local, offline embeddings.
No API keys required. Model is downloaded once and cached locally.
Default model: all-MiniLM-L6-v2 (22MB, 384 dimensions, fast and accurate for semantic tasks).
"""
import asyncio
import time
from typing import List, Optional

from loguru import logger

from app.infrastructure.providers.embeddings.iembedding_provider import (
    EmbeddingResult,
    IEmbeddingProvider,
)
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import EmbeddingException
from app.shared.utilities.sanitizer import safe_exc

_DEFAULT_MODEL = "all-MiniLM-L6-v2"


class LocalEmbeddingProvider(IEmbeddingProvider):
    """
    Local sentence-transformers embedding provider.
    Downloads the model on first use; subsequent calls use the cached model.
    Runs CPU inference — no GPU required.
    """

    def __init__(self, model_name: str = _DEFAULT_MODEL):
        self._model_name = model_name
        self._model: Optional[object] = None  # lazy load
        logger.info(
            f"[LocalEmbeddingProvider] Initialized | model={model_name} (lazy load on first call)",
            category=LogCategories.EMBEDDING,
        )

    @property
    def provider_name(self) -> str:
        return "local"

    @property
    def model_name(self) -> str:
        return self._model_name

    def _get_model(self):
        if self._model is None:
            try:
                from sentence_transformers import SentenceTransformer
                logger.info(
                    f"[LocalEmbeddingProvider] Loading model '{self._model_name}' (first call)...",
                    category=LogCategories.EMBEDDING,
                )
                self._model = SentenceTransformer(self._model_name)
                logger.info(
                    f"[LocalEmbeddingProvider] Model loaded | model={self._model_name}",
                    category=LogCategories.EMBEDDING,
                )
            except ImportError:
                raise EmbeddingException(
                    "sentence-transformers is not installed. Run: pip install sentence-transformers",
                    provider_name=self.provider_name,
                )
        return self._model

    async def embed(self, texts: List[str]) -> EmbeddingResult:
        if not texts:
            return EmbeddingResult(
                embeddings=[],
                model_name=self._model_name,
                provider_name=self.provider_name,
            )
        try:
            start = time.monotonic()
            model = self._get_model()
            # Run CPU-bound inference in a thread pool to not block the event loop
            loop = asyncio.get_event_loop()
            embeddings_np = await loop.run_in_executor(
                None, lambda: model.encode(texts, convert_to_numpy=True)
            )
            latency_ms = (time.monotonic() - start) * 1000
            embeddings = [e.tolist() for e in embeddings_np]
            logger.info(
                f"[LocalEmbeddingProvider] Embedded {len(texts)} texts | "
                f"model={self._model_name} | latency_ms={latency_ms:.1f}",
                category=LogCategories.EMBEDDING,
            )
            return EmbeddingResult(
                embeddings=embeddings,
                model_name=self._model_name,
                provider_name=self.provider_name,
                latency_ms=latency_ms,
            )
        except EmbeddingException:
            raise
        except Exception as exc:
            raise EmbeddingException(
                f"Local embedding failed: {safe_exc(exc, 200)}",
                provider_name=self.provider_name,
            )

    async def health_check(self) -> bool:
        try:
            result = await self.embed(["health check ping"])
            ok = len(result.embeddings) == 1 and len(result.embeddings[0]) > 0
            logger.info(
                f"[LocalEmbeddingProvider] Health check: {'HEALTHY' if ok else 'DEGRADED'}",
                category=LogCategories.HEALTH,
            )
            return ok
        except Exception as exc:
            logger.error(
                f"[LocalEmbeddingProvider] Health check FAILED: {safe_exc(exc)}",
                category=LogCategories.HEALTH,
            )
            return False
