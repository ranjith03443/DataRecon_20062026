"""
Enterprise-grade structured logging setup using loguru.
Configures multiple log sinks (console + per-domain files).
"""
import os
import sys
from pathlib import Path
from typing import Any, Dict

from loguru import logger

from app.infrastructure.config.config_loader import get_app_config


def setup_logging() -> None:
    """
    Configure loguru for enterprise structured logging.
    Creates per-domain log files and a console sink.
    """
    config = get_app_config()
    log_dir = Path(config.log_directory)
    log_dir.mkdir(parents=True, exist_ok=True)

    log_format = (
        "{time:YYYY-MM-DD HH:mm:ss.SSS} | {level: <8} | "
        "{name}:{function}:{line} | {message}"
    )

    # Remove default loguru sink
    logger.remove()

    # Console sink
    logger.add(
        sys.stdout,
        level=config.log_level,
        format=log_format,
        colorize=True,
        backtrace=True,
        diagnose=True,
    )

    # Error sink
    logger.add(
        str(log_dir / "errors.log"),
        level="ERROR",
        format=log_format,
        rotation="100 MB",
        retention="90 days",
        compression="gz",
        backtrace=True,
        diagnose=True,
    )

    # API sink
    logger.add(
        str(log_dir / "api.log"),
        level="INFO",
        format=log_format,
        rotation="100 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: "api" in record["name"].lower() or record["extra"].get("category") == "api",
    )

    # Semantic mapping sink
    logger.add(
        str(log_dir / "semantic_mapping.log"),
        level="DEBUG",
        format=log_format,
        rotation="100 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: (
            "semantic_mapping" in record["name"].lower()
            or record["extra"].get("category") == "semantic_mapping"
        ),
    )

    # RAG retrieval sink
    logger.add(
        str(log_dir / "rag_retrieval.log"),
        level="DEBUG",
        format=log_format,
        rotation="100 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: (
            "rag" in record["name"].lower()
            or record["extra"].get("category") == "rag"
        ),
    )

    # Embeddings sink
    logger.add(
        str(log_dir / "embeddings.log"),
        level="DEBUG",
        format=log_format,
        rotation="50 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: (
            "embedding" in record["name"].lower()
            or record["extra"].get("category") == "embedding"
        ),
    )

    # Cache sink
    logger.add(
        str(log_dir / "cache.log"),
        level="DEBUG",
        format=log_format,
        rotation="50 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: (
            "cache" in record["name"].lower()
            or record["extra"].get("category") == "cache"
        ),
    )

    # Mainframe AI Agent sink
    logger.add(
        str(log_dir / "mainframe_ai_agent.log"),
        level="DEBUG",
        format=log_format,
        rotation="100 MB",
        retention="30 days",
        compression="gz",
        filter=lambda record: (
            "mainframe" in record["name"].lower()
            or record["extra"].get("category") == "mainframe_ai_agent"
        ),
    )

    logger.info(
        f"[LoggerSetup] Logging configured | level={config.log_level} | log_dir={log_dir.resolve()}"
    )


def get_logger(name: str):
    """Return a loguru logger bound with a module name."""
    return logger.bind(module=name)


def log_with_context(
    level: str,
    message: str,
    job_id: str = "",
    request_id: str = "",
    workflow_step: str = "",
    model_name: str = "",
    provider_name: str = "",
    correlation_id: str = "",
    category: str = "",
    **extra: Any,
) -> None:
    """
    Emit a structured log entry with mandatory audit context fields.
    """
    ctx = dict(
        job_id=job_id,
        request_id=request_id,
        workflow_step=workflow_step,
        model_name=model_name,
        provider_name=provider_name,
        correlation_id=correlation_id,
        category=category,
        **extra,
    )
    logger.opt(depth=1).bind(**ctx).log(level.upper(), message)
