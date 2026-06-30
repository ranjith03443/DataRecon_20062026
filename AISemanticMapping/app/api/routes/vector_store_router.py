"""
Router for POST /api/vector-store/index and POST /api/vector-store/search
"""
from fastapi import APIRouter, Depends, Request
from loguru import logger

from app.application.dto.vector_store_dto import (
    VectorIndexRequestDTO,
    VectorIndexResponseDTO,
    VectorSearchRequestDTO,
    VectorSearchResponseDTO,
)
from app.application.services.vector_store_service import VectorStoreService
from app.shared.constants.app_constants import LogCategories

router = APIRouter(prefix="/vector-store", tags=["Vector Store"])


def _get_service() -> VectorStoreService:
    return VectorStoreService()


@router.post(
    "/index",
    response_model=VectorIndexResponseDTO,
    summary="Index documents into vector store",
    description=(
        "Generates embeddings for the provided documents and indexes them in the "
        "specified ChromaDB collection. Only accepts semantic metadata documents — "
        "NOT raw CSV rows or transactional data."
    ),
)
async def index_documents(
    request: Request,
    body: VectorIndexRequestDTO,
    service: VectorStoreService = Depends(_get_service),
) -> VectorIndexResponseDTO:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[VectorStoreRouter] POST /vector-store/index | "
        f"collection={body.collection} | docs={len(body.documents)} | request_id={request_id}",
        category=LogCategories.VECTOR_STORE,
    )
    return await service.index_documents(body, request_id=request_id)


@router.post(
    "/search",
    response_model=VectorSearchResponseDTO,
    summary="Semantic vector search",
    description="Performs a semantic similarity search on a vector store collection.",
)
async def search_documents(
    request: Request,
    body: VectorSearchRequestDTO,
    service: VectorStoreService = Depends(_get_service),
) -> VectorSearchResponseDTO:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[VectorStoreRouter] POST /vector-store/search | "
        f"collection={body.collection} | query={body.query[:60]} | request_id={request_id}",
        category=LogCategories.VECTOR_STORE,
    )
    return await service.search(body, request_id=request_id)
