"""
Pydantic DTOs for Semantic Mapping API request and response.
Strongly typed, validated input/output contracts.
"""
from typing import Any, Dict, List, Optional
from pydantic import BaseModel, Field, field_validator


class SourceMetadataDTO(BaseModel):
    """Metadata about the source field."""
    datatype: Optional[str] = Field(None, description="Data type of the source field")
    description: Optional[str] = Field(None, description="Business description of the source field")
    nullable: Optional[bool] = Field(None, description="Whether the field is nullable")
    sample_values: Optional[List[str]] = Field(None, description="Sample values for additional context")
    max_length: Optional[int] = Field(None, description="Maximum field length")


class SemanticMappingRequestDTO(BaseModel):
    """Request DTO for POST /api/semantic-mapping."""
    jobId: str = Field(..., min_length=1, description="Unique job identifier from orchestrator")
    sourceField: str = Field(..., min_length=1, description="Source field name to map")
    targetFieldCandidates: List[str] = Field(..., min_items=1, description="List of candidate target fields")
    sourceMetadata: Optional[SourceMetadataDTO] = Field(None, description="Optional source field metadata")
    requestId: Optional[str] = Field(None, description="Request correlation ID")
    workflowStep: Optional[str] = Field(None, description="Workflow step identifier")

    @field_validator("targetFieldCandidates")
    @classmethod
    def validate_candidates(cls, v: List[str]) -> List[str]:
        if not v:
            raise ValueError("targetFieldCandidates must not be empty")
        return [c.strip() for c in v if c.strip()]


class MappingAlternativeDTO(BaseModel):
    """Alternative mapping candidate."""
    field: str
    confidence: float = Field(..., ge=0.0, le=1.0)


class SemanticMappingResponseDTO(BaseModel):
    """Response DTO for POST /api/semantic-mapping."""
    jobId: str
    mapping: str = Field(..., description="Best matching target field")
    confidence: float = Field(..., ge=0.0, le=1.0, description="Confidence score 0-1")
    confidenceLevel: str = Field(..., description="HIGH / MEDIUM / LOW / INSUFFICIENT")
    reasoning: str = Field(..., description="Explanation for the mapping decision")
    alternatives: Optional[List[MappingAlternativeDTO]] = Field(None, description="Alternative candidates")
    promptVersion: Optional[str] = Field(None, description="Prompt version used")
    cached: bool = Field(False, description="Whether result came from semantic cache")
    requestId: Optional[str] = None

    # Token usage — populated only on live (non-cached) LLM calls
    promptTokens: Optional[int] = Field(None, description="Number of prompt tokens consumed")
    completionTokens: Optional[int] = Field(None, description="Number of completion tokens consumed")
    totalTokens: Optional[int] = Field(None, description="Total tokens consumed")
    estimatedCostUsd: Optional[float] = Field(None, description="Estimated cost in USD")
    modelName: Optional[str] = Field(None, description="LLM model used for this inference")
