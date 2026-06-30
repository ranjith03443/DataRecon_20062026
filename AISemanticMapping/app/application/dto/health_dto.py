"""
Pydantic DTOs for Health and Models endpoints.
"""
from typing import Any, Dict, List, Optional
from pydantic import BaseModel, Field


class ProviderStatusDTO(BaseModel):
    name: str
    healthy: bool
    details: Optional[str] = None


class HealthResponseDTO(BaseModel):
    """Response DTO for GET /api/health."""
    status: str = Field(..., description="HEALTHY | DEGRADED | UNHEALTHY")
    version: str
    environment: str
    providers: List[ProviderStatusDTO]
    cacheStatus: str
    ragEnabled: bool
    semanticCacheEnabled: bool
    uptimeSeconds: Optional[float] = None


class ActiveModelDTO(BaseModel):
    task: str
    modelName: str
    providerName: str
    temperature: Optional[float] = None
    maxTokens: Optional[int] = None


class ModelsResponseDTO(BaseModel):
    """Response DTO for GET /api/models."""
    llmProvider: str
    embeddingProvider: str
    vectorStoreProvider: str
    activeModels: List[ActiveModelDTO]
    embeddingModel: str
    availableProviders: List[str]
