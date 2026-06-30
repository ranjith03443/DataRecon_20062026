"""
Pydantic DTOs for Vector Store API (index + search).
"""
from typing import Any, Dict, List, Optional
from pydantic import BaseModel, Field


class VectorIndexRequestDTO(BaseModel):
    """Request DTO for POST /api/vector-store/index."""
    collection: str = Field(..., min_length=1, description="Target collection name")
    documents: List[str] = Field(..., min_items=1, description="Documents to index")
    metadatas: Optional[List[Dict[str, Any]]] = Field(None, description="Document metadata")
    ids: Optional[List[str]] = Field(None, description="Optional document IDs")
    jobId: Optional[str] = Field(None, description="Job ID for audit")
    requestId: Optional[str] = None


class VectorIndexResponseDTO(BaseModel):
    """Response DTO for POST /api/vector-store/index."""
    collection: str
    documentsIndexed: int
    success: bool
    requestId: Optional[str] = None


class VectorSearchRequestDTO(BaseModel):
    """Request DTO for POST /api/vector-store/search."""
    collection: str = Field(..., min_length=1, description="Collection to search")
    query: str = Field(..., min_length=1, description="Natural language search query")
    topK: int = Field(5, ge=1, le=50, description="Number of results to return")
    metadataFilter: Optional[Dict[str, Any]] = Field(None, description="Optional metadata filter")
    jobId: Optional[str] = None
    requestId: Optional[str] = None


class VectorSearchResultItemDTO(BaseModel):
    """Single vector search result item."""
    documentId: str
    document: str
    metadata: Dict[str, Any]
    score: float = Field(..., ge=0.0, le=1.0)


class VectorSearchResponseDTO(BaseModel):
    """Response DTO for POST /api/vector-store/search."""
    collection: str
    query: str
    results: List[VectorSearchResultItemDTO]
    totalResults: int
    requestId: Optional[str] = None
