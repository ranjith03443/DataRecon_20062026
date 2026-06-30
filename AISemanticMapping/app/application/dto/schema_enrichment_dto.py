"""
Pydantic DTOs for Schema Enrichment API.
"""
from typing import Dict, List, Optional
from pydantic import BaseModel, Field


class SemanticEnrichmentRequestDTO(BaseModel):
    """Request DTO for POST /api/semantic-enrichment."""
    fieldName: str = Field(..., min_length=1, description="Field name to enrich (e.g., ACC_OPN_DT)")
    additionalContext: Optional[str] = Field(None, description="Optional additional context")
    jobId: Optional[str] = Field(None, description="Job ID for audit tracking")
    requestId: Optional[str] = Field(None, description="Request correlation ID")


class SemanticEnrichmentResponseDTO(BaseModel):
    """Response DTO for POST /api/semantic-enrichment."""
    fieldName: str
    possibleMeaning: str = Field(..., description="Expanded full meaning (e.g., Account Open Date)")
    businessCategory: str = Field(..., description="Business domain category")
    description: Optional[str] = Field(None, description="Detailed business description")
    dataTypeHint: Optional[str] = Field(None, description="Inferred data type")
    abbreviationsExpanded: Optional[Dict[str, str]] = Field(None, description="Abbreviation expansions")
    confidence: float = Field(..., ge=0.0, le=1.0)
    confidenceLevel: str
    promptVersion: Optional[str] = None
    cached: bool = False
    requestId: Optional[str] = None
