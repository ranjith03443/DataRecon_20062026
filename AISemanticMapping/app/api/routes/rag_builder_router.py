"""
Router for Knowledge Base management.
  POST /api/rag/build   — read knowledge_base/ folder and index into ChromaDB
  GET  /api/rag/status  — show document count for each ChromaDB collection
"""
from fastapi import APIRouter, Request
from loguru import logger

from app.application.services.rag_builder_service import RagBuilderService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/rag", tags=["RAG Knowledge Base"])


@router.post(
    "/build",
    summary="Build Knowledge Base from knowledge_base/ folder",
    description=(
        "Reads all files from the knowledge_base/ subfolders, generates embeddings, "
        "and upserts them into the corresponding ChromaDB collections. "
        "Safe to call multiple times — documents are upserted (not duplicated). "
        "The Python service must be running with a valid embedding provider."
    ),
)
async def build_rag(request: Request):
    request_id = getattr(request.state, "request_id", "manual")
    logger.info(
        f"[RagBuilderRouter] POST /rag/build | request_id={request_id}",
        category=LogCategories.RAG,
    )
    svc = RagBuilderService()
    results = await svc.build_all(job_id=request_id)
    total = sum(v.get("indexed", 0) for v in results.values())
    return {
        "success": True,
        "total_indexed": total,
        "collections": results,
    }


@router.get(
    "/status",
    summary="Check document counts in each ChromaDB collection",
    description="Returns the number of indexed documents in each Knowledge Base collection and whether it is ready.",
)
async def rag_status(request: Request):
    request_id = getattr(request.state, "request_id", "manual")
    logger.info(
        f"[RagBuilderRouter] GET /rag/status | request_id={request_id}",
        category=LogCategories.RAG,
    )
    svc = RagBuilderService()
    status = await svc.get_status()
    ready_count = sum(1 for v in status.values() if v.get("ready"))
    return {
        "collections": status,
        "ready_count": ready_count,
        "total_collections": len(status),
    }
