"""
Tests for SemanticMappingService.
Verifies mapping inference flow with mocked LLM provider.
"""
import asyncio
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

from app.application.dto.semantic_mapping_dto import (
    SemanticMappingRequestDTO,
    SourceMetadataDTO,
)
from app.application.services.semantic_mapping_service import SemanticMappingService
from app.infrastructure.providers.llm.illm_provider import LLMResponse


MOCK_LLM_RESPONSE = '{"mapping": "CLIENT_ID", "confidence": 0.93, "reasoning": "Customer semantic similarity detected", "alternatives": []}'


@pytest.fixture
def mock_llm_provider():
    provider = MagicMock()
    provider.provider_name = "azure_openai"
    provider.model_name = "gpt-4o-mini"
    provider.complete = AsyncMock(
        return_value=LLMResponse(
            content=MOCK_LLM_RESPONSE,
            model_name="gpt-4o-mini",
            provider_name="azure_openai",
            prompt_tokens=150,
            completion_tokens=50,
            total_tokens=200,
            latency_ms=350.0,
        )
    )
    return provider


@pytest.fixture
def mock_retriever():
    retriever = MagicMock()
    retriever.retrieve_historical_mappings = AsyncMock(return_value=[])
    retriever.retrieve_glossary_terms = AsyncMock(return_value=[])
    retriever.retrieve_field_descriptions = AsyncMock(return_value=[])
    return retriever


@pytest.mark.asyncio
async def test_semantic_mapping_returns_response(mock_llm_provider, mock_retriever):
    """Test that semantic mapping returns a valid response with correct fields."""
    service = SemanticMappingService(
        llm_provider=mock_llm_provider,
        retriever=mock_retriever,
    )
    request = SemanticMappingRequestDTO(
        jobId="JOB_001",
        sourceField="PRIMARY_CUSTOMER_ID",
        targetFieldCandidates=["CLIENT_ID", "ACCOUNT_ID"],
        sourceMetadata=SourceMetadataDTO(
            datatype="numeric",
            description="Primary customer identifier",
        ),
    )
    response = await service.infer_mapping(request, request_id="TEST-REQ-001")

    assert response.jobId == "JOB_001"
    assert response.mapping == "CLIENT_ID"
    assert response.confidence == pytest.approx(0.93, abs=0.01)
    assert response.confidenceLevel == "HIGH"
    assert "Customer" in response.reasoning
    assert response.cached is False


@pytest.mark.asyncio
async def test_semantic_mapping_cache_hit(mock_llm_provider, mock_retriever):
    """Test that a second identical request returns from cache."""
    service = SemanticMappingService(
        llm_provider=mock_llm_provider,
        retriever=mock_retriever,
    )
    request = SemanticMappingRequestDTO(
        jobId="JOB_002",
        sourceField="ACCT_NUM",
        targetFieldCandidates=["ACCOUNT_NUMBER", "ACCOUNT_ID"],
    )
    # First call
    response1 = await service.infer_mapping(request, request_id="REQ-001")
    # Second call — should use cache (same parameters)
    response2 = await service.infer_mapping(request, request_id="REQ-002")

    # LLM called only once
    assert mock_llm_provider.complete.call_count == 1
    assert response2.cached is True
