"""
Pydantic DTOs for the Mainframe Development AI Agent endpoints.
"""
from typing import Any, Dict, List, Optional
from pydantic import BaseModel, Field


# ── Request ───────────────────────────────────────────────────────────────────

class MainframeAgentRequestDTO(BaseModel):
    """Request body for all /api/mainframe/* endpoints."""

    jobId: str = Field(..., min_length=1, description="Workflow job ID for audit tracking")
    promptType: str = Field(
        ...,
        description=(
            "Agent type: explain | review | enhance | validation | "
            "error_handling | documentation | jcl_improvements | optimization"
        ),
    )

    # Artifact content — caller provides whichever are available
    cobolContent: Optional[str] = Field(None, description="COBOL skeleton program content")
    jclContent: Optional[str] = Field(None, description="JCL skeleton content")
    copybookContent: Optional[str] = Field(None, description="Copybook (.cpy) content")
    transformationRules: Optional[List[Dict[str, Any]]] = Field(
        None, description="Parsed transformation_rules.json list"
    )
    valueMappings: Optional[Dict[str, Any]] = Field(
        None, description="Parsed value_mappings.json object"
    )
    fieldMappings: Optional[List[Dict[str, Any]]] = Field(
        None, description="Parsed final_mapping_config.json field list"
    )
    targetSchema: Optional[Dict[str, Any]] = Field(
        None, description="Parsed target_schema_metadata.json"
    )

    # Audit
    requestId: Optional[str] = Field(None, description="Correlation / request ID")
    userId: Optional[str] = Field(None, description="User triggering the request")


# ── Sub-models ────────────────────────────────────────────────────────────────

class RecommendationItem(BaseModel):
    """A single AI recommendation."""
    category: str = Field(..., description="Category: validation | error_handling | logic | performance | naming | structure")
    severity: str = Field(..., description="HIGH | MEDIUM | LOW | INFO")
    message: str = Field(..., description="Plain-English description of the issue or suggestion")
    suggestion: Optional[str] = Field(None, description="Corrective action or recommendation")
    codeExample: Optional[str] = Field(None, description="COBOL or JCL code example snippet")


# ── Response ──────────────────────────────────────────────────────────────────

class MainframeAgentResponseDTO(BaseModel):
    """Response body for all /api/mainframe/* endpoints."""

    jobId: str
    promptType: str
    agentName: str = Field(..., description="Human-readable agent name")

    # Content sections — populated by specific agents
    businessExplanation: Optional[str] = Field(
        None, description="Plain-English explanation of the COBOL program logic"
    )
    generatedCode: Optional[str] = Field(
        None, description="AI-generated COBOL / JCL code snippets"
    )
    recommendations: List[RecommendationItem] = Field(
        default_factory=list,
        description="Structured recommendations list",
    )
    warnings: List[str] = Field(
        default_factory=list,
        description="High-level risk / governance warnings",
    )

    # Metrics
    confidence: float = Field(..., ge=0.0, le=1.0)
    confidenceLevel: str = Field(..., description="HIGH | MEDIUM | LOW | INSUFFICIENT")
    responseTimeMs: float = Field(default=0.0)
    tokenUsage: Optional[Dict[str, Any]] = Field(None)

    # Governance
    governanceNotice: str = Field(
        default=(
            "AI Generated Developer Guidance | "
            "Developer Review Required | "
            "Not Production Ready"
        )
    )

    requestId: Optional[str] = None
    promptVersion: Optional[str] = None
    cached: bool = False
