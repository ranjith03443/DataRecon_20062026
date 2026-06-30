"""
YAML-based configuration loader for the Semantic Intelligence Platform.
Loads configuration from YAML files and environment variables.
Configuration is immutable after load (singleton pattern).
"""
import os
import yaml
from functools import lru_cache
from pathlib import Path
from typing import Any, Dict, Optional

from loguru import logger


CONFIG_BASE_PATH = Path(os.getenv("CONFIG_PATH", "config"))


def _load_yaml(filename: str) -> Dict[str, Any]:
    """Load a YAML file from the config directory."""
    filepath = CONFIG_BASE_PATH / filename
    if not filepath.exists():
        logger.warning(f"[ConfigLoader] Config file not found: {filepath}. Using empty config.")
        return {}
    with open(filepath, "r", encoding="utf-8") as f:
        data = yaml.safe_load(f) or {}
    logger.debug(f"[ConfigLoader] Loaded config file: {filepath}")
    return data


class AppConfig:
    """Unified application configuration loaded from YAML + environment variables."""

    def __init__(self):
        logger.info("[ConfigLoader] Loading all configuration files...")
        self._app = _load_yaml("app_config.yaml")
        self._models = _load_yaml("models.yaml")
        self._cache = _load_yaml("cache.yaml")
        self._logging = _load_yaml("logging.yaml")
        self._security = _load_yaml("security.yaml")
        self._vectorstore = _load_yaml("vectorstore.yaml")
        logger.info("[ConfigLoader] All configuration files loaded successfully.")

    # ── App Config ──────────────────────────────────────────────────────────

    @property
    def app_name(self) -> str:
        return self._app.get("app_name", "semantic-intelligence-platform")

    @property
    def environment(self) -> str:
        return os.getenv("ENVIRONMENT", self._app.get("environment", "dev"))

    @property
    def enable_rag(self) -> bool:
        return self._app.get("features", {}).get("enable_rag", True)

    @property
    def enable_semantic_cache(self) -> bool:
        return self._app.get("features", {}).get("enable_semantic_cache", True)

    @property
    def enable_ai_mapping(self) -> bool:
        return self._app.get("features", {}).get("enable_ai_mapping", True)

    @property
    def server_host(self) -> str:
        return self._app.get("server", {}).get("host", "0.0.0.0")

    @property
    def server_port(self) -> int:
        return int(self._app.get("server", {}).get("port", 8000))

    @property
    def api_prefix(self) -> str:
        return self._app.get("api", {}).get("prefix", "/api")

    @property
    def high_confidence_threshold(self) -> float:
        return float(self._app.get("ai_governance", {}).get("high_confidence_threshold", 0.90))

    @property
    def medium_confidence_threshold(self) -> float:
        return float(self._app.get("ai_governance", {}).get("medium_confidence_threshold", 0.70))

    # ── Models Config ────────────────────────────────────────────────────────

    @property
    def llm_provider(self) -> str:
        return os.getenv("LLM_PROVIDER", self._models.get("llm_provider", "azure_openai"))

    @property
    def embedding_provider(self) -> str:
        return os.getenv("EMBEDDING_PROVIDER", self._models.get("embedding_provider", "azure_openai"))

    def get_model_config(self, task: str) -> Dict[str, Any]:
        """Get model configuration for a specific task."""
        return self._models.get("models", {}).get(task, {})

    def get_embedding_model_config(self, model_key: str = "default") -> Dict[str, Any]:
        return self._models.get("embedding_models", {}).get(model_key, {})

    def get_provider_config(self, provider: str) -> Dict[str, Any]:
        return self._models.get("providers", {}).get(provider, {})

    # ── Azure OpenAI Config ──────────────────────────────────────────────────

    @property
    def azure_openai_endpoint(self) -> Optional[str]:
        return os.getenv("AZURE_OPENAI_ENDPOINT")

    @property
    def azure_openai_api_key(self) -> Optional[str]:
        return os.getenv("AZURE_OPENAI_API_KEY")

    @property
    def azure_openai_api_version(self) -> str:
        return os.getenv(
            "AZURE_OPENAI_API_VERSION",
            self.get_provider_config("azure_openai").get("api_version", "2024-02-15-preview"),
        )

    @property
    def azure_openai_deployment_name(self) -> Optional[str]:
        return os.getenv("AZURE_OPENAI_DEPLOYMENT_NAME")

    @property
    def azure_openai_embedding_deployment(self) -> Optional[str]:
        return os.getenv(
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
            self.get_embedding_model_config().get("model_name", "text-embedding-3-small"),
        )

    # ── Security Config ──────────────────────────────────────────────────────

    @property
    def security_enabled(self) -> bool:
        return self._security.get("security", {}).get("enabled", True)

    @property
    def bypass_authentication(self) -> bool:
        return self._security.get("security", {}).get("bypass_authentication", True)

    @property
    def bypass_authorization(self) -> bool:
        return self._security.get("security", {}).get("bypass_authorization", True)

    @property
    def valid_api_keys(self) -> list:
        raw = os.getenv("SEMANTIC_API_KEYS", "")
        if not raw:
            return []
        return [k.strip() for k in raw.split(",") if k.strip()]

    # ── Cache Config ─────────────────────────────────────────────────────────

    @property
    def cache_enabled(self) -> bool:
        return self._cache.get("semantic_cache", {}).get("enabled", True)

    @property
    def cache_ttl_seconds(self) -> int:
        return int(self._cache.get("semantic_cache", {}).get("ttl_seconds", 3600))

    @property
    def cache_max_size(self) -> int:
        return int(self._cache.get("semantic_cache", {}).get("max_size", 1000))

    @property
    def cache_min_confidence(self) -> float:
        return float(self._cache.get("semantic_cache", {}).get("min_confidence_to_cache", 0.90))

    # ── Vector Store Config ───────────────────────────────────────────────────

    @property
    def vectorstore_provider(self) -> str:
        return self._vectorstore.get("vector_store", {}).get("provider", "chromadb")

    @property
    def chromadb_persist_dir(self) -> str:
        return self._vectorstore.get("vector_store", {}).get("chromadb", {}).get(
            "persist_directory", "./data/chromadb"
        )

    @property
    def vectorstore_top_k(self) -> int:
        return int(self._vectorstore.get("vector_store", {}).get("search", {}).get("default_top_k", 5))

    # ── Logging Config ────────────────────────────────────────────────────────

    @property
    def log_level(self) -> str:
        return os.getenv("LOG_LEVEL", self._logging.get("logging", {}).get("level", "DEBUG"))

    @property
    def log_directory(self) -> str:
        return self._logging.get("logging", {}).get("log_directory", "python_logs")


@lru_cache(maxsize=1)
def get_app_config() -> AppConfig:
    """Return the singleton AppConfig instance."""
    return AppConfig()
