"""
Azure OpenAI Embedding Provider.
Uses the Azure OpenAI Embeddings REST API via httpx.
"""
import asyncio
import time
from typing import List, Optional

import httpx
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.embeddings.iembedding_provider import (
    IEmbeddingProvider,
    EmbeddingResult,
)
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import EmbeddingException

_MAX_RETRIES = 3
_RETRY_BACKOFF_SECONDS = [1.0, 2.0, 4.0]


class AzureOpenAIEmbeddingProvider(IEmbeddingProvider):
    """
    Azure OpenAI Embeddings API provider.
    Reads all credentials from environment variables via AppConfig.
    """

    def __init__(self):
        config = get_app_config()
        self._config = config
        embedding_cfg = config.get_embedding_model_config("default")
        self._model = embedding_cfg.get("model_name", "text-embedding-3-small")
        self._deployment = config.azure_openai_embedding_deployment or self._model
        self._api_version = config.azure_openai_api_version
        self._endpoint = config.azure_openai_endpoint
        self._timeout = float(embedding_cfg.get("request_timeout", 30))
        self._batch_size = int(embedding_cfg.get("batch_size", 100))

        logger.info(
            f"[AzureOpenAIEmbeddingProvider] Initialized | model={self._model} | "
            f"deployment={self._deployment}",
            category=LogCategories.EMBEDDING,
        )

    @property
    def provider_name(self) -> str:
        return "azure_openai"

    @property
    def model_name(self) -> str:
        return self._model

    async def embed(self, texts: List[str]) -> EmbeddingResult:
        """
        Generate embeddings for a list of texts.
        Batches requests to respect API limits.
        """
        api_key = self._config.azure_openai_api_key
        if not self._endpoint or not api_key:
            raise EmbeddingException(
                "Azure OpenAI endpoint or API key not configured for embeddings.",
                provider_name=self.provider_name,
            )

        if not texts:
            logger.warning("[AzureOpenAIEmbeddingProvider] Empty text list provided — returning empty embeddings.")
            return EmbeddingResult(embeddings=[], model_name=self._model, provider_name=self.provider_name)

        url = (
            f"{self._endpoint.rstrip('/')}/openai/deployments/"
            f"{self._deployment}/embeddings?api-version={self._api_version}"
        )
        headers = {"Content-Type": "application/json", "api-key": api_key}
        all_embeddings: List[List[float]] = []
        total_tokens = 0
        start_time = time.monotonic()

        # Process in batches
        for batch_start in range(0, len(texts), self._batch_size):
            batch = texts[batch_start: batch_start + self._batch_size]
            batch_embeddings = await self._embed_batch(url, headers, batch)
            all_embeddings.extend(batch_embeddings)

        latency_ms = (time.monotonic() - start_time) * 1000
        logger.info(
            f"[AzureOpenAIEmbeddingProvider] Embeddings generated | "
            f"count={len(all_embeddings)} | latency_ms={latency_ms:.1f}",
            category=LogCategories.EMBEDDING,
        )
        return EmbeddingResult(
            embeddings=all_embeddings,
            model_name=self._model,
            provider_name=self.provider_name,
            total_tokens=total_tokens,
            latency_ms=latency_ms,
        )

    async def _embed_batch(
        self,
        url: str,
        headers: dict,
        batch: List[str],
    ) -> List[List[float]]:
        """Send a single batch of texts to the embeddings API."""
        payload = {"input": batch, "model": self._model}
        last_exc: Optional[Exception] = None

        for attempt in range(_MAX_RETRIES):
            try:
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, headers=headers, json=payload)
                    response.raise_for_status()
                    data = response.json()

                embeddings = [item["embedding"] for item in sorted(data["data"], key=lambda x: x["index"])]
                logger.debug(
                    f"[AzureOpenAIEmbeddingProvider] Batch embedded | "
                    f"batch_size={len(batch)} | attempt={attempt + 1}",
                    category=LogCategories.EMBEDDING,
                )
                return embeddings

            except httpx.HTTPStatusError as exc:
                status = exc.response.status_code
                logger.warning(
                    f"[AzureOpenAIEmbeddingProvider] HTTP error | status={status} | attempt={attempt + 1}",
                    category=LogCategories.EMBEDDING,
                )
                last_exc = EmbeddingException(
                    f"Embedding HTTP {status}: {exc.response.text[:100]}",
                    provider_name=self.provider_name,
                )
                if status in (429, 500, 502, 503) and attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                    continue
                break
            except Exception as exc:
                last_exc = EmbeddingException(str(exc)[:200], provider_name=self.provider_name)
                if attempt < _MAX_RETRIES - 1:
                    await asyncio.sleep(_RETRY_BACKOFF_SECONDS[attempt])
                    continue
                break

        raise last_exc or EmbeddingException("Embedding batch failed.", provider_name=self.provider_name)

    async def health_check(self) -> bool:
        try:
            result = await self.embed(["health check"])
            healthy = len(result.embeddings) == 1
            logger.info(
                f"[AzureOpenAIEmbeddingProvider] Health check: {'HEALTHY' if healthy else 'DEGRADED'}",
                category=LogCategories.HEALTH,
            )
            return healthy
        except Exception as exc:
            logger.error(
                f"[AzureOpenAIEmbeddingProvider] Health check FAILED: {str(exc)[:200]}",
                category=LogCategories.HEALTH,
            )
            return False
