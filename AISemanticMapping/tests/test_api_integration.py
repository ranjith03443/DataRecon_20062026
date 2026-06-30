"""
Integration test for FastAPI endpoints using httpx TestClient.
"""
import pytest
from fastapi.testclient import TestClient
from unittest.mock import AsyncMock, MagicMock, patch


@pytest.fixture
def client():
    """Create a test client with mocked AI services."""
    from main import app
    with TestClient(app) as c:
        yield c


def test_health_endpoint_returns_200(client):
    """Health endpoint must always return 200 regardless of provider state."""
    with patch("app.api.routes.health_router.LLMProviderFactory.create") as mock_llm, \
         patch("app.api.routes.health_router.EmbeddingProviderFactory.create") as mock_emb, \
         patch("app.api.routes.health_router.VectorStoreFactory.create") as mock_vs:
        mock_llm.return_value.health_check = AsyncMock(return_value=True)
        mock_emb.return_value.health_check = AsyncMock(return_value=True)
        mock_vs.return_value.health_check = AsyncMock(return_value=True)
        mock_llm.return_value.model_name = "gpt-4o-mini"
        mock_emb.return_value.model_name = "text-embedding-3-small"

        response = client.get("/api/health")
        assert response.status_code == 200
        data = response.json()
        assert "status" in data
        assert "providers" in data


def test_models_endpoint(client):
    response = client.get("/api/models")
    assert response.status_code == 200
    data = response.json()
    assert "llmProvider" in data
    assert "embeddingProvider" in data
    assert "activeModels" in data
