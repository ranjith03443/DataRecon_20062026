"""
Pydantic DTOs for Rule Inference API.
"""
from typing import Any, Dict, List, Optional
from pydantic import BaseModel, Field


class RuleInferenceRequestDTO(BaseModel):
    """Request DTO for POST /api/rule-inference."""
    rule: str = Field(..., min_length=1, description="Business rule description to interpret")
    fieldContext: Optional[str] = Field(None, description="Field context for the rule")
    jobId: Optional[str] = Field(None, description="Job ID for audit tracking")
    requestId: Optional[str] = Field(None, description="Request correlation ID")
    workflowStep: Optional[str] = Field(None, description="Workflow step")


class RuleInferenceResponseDTO(BaseModel):
    """Response DTO for POST /api/rule-inference."""
    operation: str = Field(..., description="Inferred transformation operation type")
    format: Optional[str] = Field(None, description="Format string if applicable (e.g., YYYYMMDD)")
    parameters: Optional[Dict[str, Any]] = Field(None, description="Operation parameters")
    description: Optional[str] = Field(None, description="Plain English transformation description")
    validationRules: Optional[List[str]] = Field(None, description="Validation rules for the operation")
    confidence: float = Field(..., ge=0.0, le=1.0)
    confidenceLevel: str
    reasoning: Optional[str] = None
    promptVersion: Optional[str] = None
    cached: bool = False
    requestId: Optional[str] = None
