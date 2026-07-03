"""
Semantic Intelligence Platform — FastAPI Application Entrypoint.

Architecture:
- Clean / Layered Architecture
- SOLID principles
- Provider-agnostic, stateless microservice
- Serves as the AI/semantic intelligence layer for the .NET orchestrator
- Does NOT own workflow orchestration, job state, or reconciliation logic
"""
import sys
from contextlib import asynccontextmanager

from dotenv import load_dotenv
load_dotenv()  # Load .env into os.environ before any config is read

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from loguru import logger

from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.logging.logger_setup import setup_logging
from app.api.middleware.logging_middleware import RequestLoggingMiddleware
from app.api.middleware.auth_middleware import AuthMiddleware
from app.api.middleware.exception_handler import register_exception_handlers
from app.api.routes import (
    semantic_mapping_router,
    semantic_enrichment_router,
    rule_inference_router,
    vector_store_router,
    health_router,
    models_router,
    mainframe_agent_router,
    value_mapping_agent_router,
    budget_router,
    multi_source_schema_router,
    rag_builder_router,
)
from app.shared.constants.app_constants import AppConstants


# ── Bootstrap Logging ────────────────────────────────────────────────────────

setup_logging()
config = get_app_config()


# ── Application Lifespan ─────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Application startup and shutdown lifecycle management."""
    logger.info(
        f"[Lifespan] Starting {config.app_name} | "
        f"environment={config.environment} | "
        f"llm_provider={config.llm_provider} | "
        f"embedding_provider={config.embedding_provider} | "
        f"rag_enabled={config.enable_rag} | "
        f"semantic_cache_enabled={config.enable_semantic_cache}"
    )

    # Ensure log and data directories exist
    import os
    for d in ["python_logs", "data/chromadb", "prompts", "config"]:
        os.makedirs(d, exist_ok=True)

    logger.info("[Lifespan] Application startup complete — ready to serve requests.")
    yield
    logger.info("[Lifespan] Application shutting down gracefully.")


# ── FastAPI Application ───────────────────────────────────────────────────────

def create_app() -> FastAPI:
    """
    Application factory — creates and configures the FastAPI app.
    """
    app = FastAPI(
        title=config._app.get("api", {}).get("title", "Semantic Intelligence Platform API"),
        description=(
            "Enterprise-grade semantic intelligence microservice for metadata-driven "
            "reconciliation and transformation orchestration. "
            "Provides semantic mapping, schema enrichment, rule inference, RAG retrieval, "
            "and vector search capabilities."
        ),
        version=config._app.get("version", "1.0.0"),
        docs_url="/docs",
        redoc_url="/redoc",
        openapi_url="/openapi.json",
        lifespan=lifespan,
    )

    # ── CORS Middleware ───────────────────────────────────────────────────────
    cors_origins = config._app.get("api", {}).get("cors_origins", ["*"])
    app.add_middleware(
        CORSMiddleware,
        allow_origins=cors_origins,
        allow_credentials=False,
        allow_methods=["GET", "POST", "OPTIONS"],
        allow_headers=["*"],
    )

    # ── Request Logging Middleware ────────────────────────────────────────────
    app.add_middleware(RequestLoggingMiddleware)

    # ── Auth Middleware ───────────────────────────────────────────────────────
    app.add_middleware(AuthMiddleware)

    # ── Exception Handlers ────────────────────────────────────────────────────
    register_exception_handlers(app)

    # ── API Routers ───────────────────────────────────────────────────────────
    api_prefix = config.api_prefix  # "/api"

    app.include_router(health_router.router, prefix=api_prefix)
    app.include_router(models_router.router, prefix=api_prefix)
    app.include_router(semantic_mapping_router.router, prefix=api_prefix)
    app.include_router(semantic_enrichment_router.router, prefix=api_prefix)
    app.include_router(rule_inference_router.router, prefix=api_prefix)
    app.include_router(vector_store_router.router, prefix=api_prefix)
    app.include_router(mainframe_agent_router.router, prefix=api_prefix)
    app.include_router(value_mapping_agent_router.router, prefix=api_prefix)
    app.include_router(budget_router.router, prefix=api_prefix)
    app.include_router(multi_source_schema_router.router, prefix=api_prefix)
    app.include_router(rag_builder_router.router, prefix=api_prefix)

    logger.info(
        f"[AppFactory] FastAPI application configured | "
        f"routes={len(app.routes)} | prefix={api_prefix}"
    )
    return app


app = create_app()


# ── Entrypoint ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn

    logger.info(
        f"[Main] Starting Uvicorn server | "
        f"host={config.server_host} | port={config.server_port}"
    )
    uvicorn.run(
        "main:app",
        host=config.server_host,
        port=config.server_port,
        reload=config._app.get("server", {}).get("reload", False),
        log_level="info",
    )
