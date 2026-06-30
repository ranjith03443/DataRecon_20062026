"""
Models info router — GET /api/models
Returns active providers, models, and configuration.
"""
from fastapi import APIRouter
from loguru import logger

from app.application.dto.health_dto import ActiveModelDTO, ModelsResponseDTO
from app.infrastructure.config.config_loader import get_app_config
from app.shared.constants.app_constants import LogCategories

router = APIRouter(tags=["Models"])


@router.get(
    "/models",
    response_model=ModelsResponseDTO,
    summary="Active models and provider configuration",
    description="Returns active LLM, embedding, and vector store provider configurations.",
)
async def get_models() -> ModelsResponseDTO:
    config = get_app_config()
    logger.info("[ModelsRouter] GET /models", category=LogCategories.API_REQUEST)

    active_models = []
    for task in ["semantic_mapping", "schema_enrichment", "rule_inference"]:
        task_cfg = config.get_model_config(task)
        if task_cfg:
            active_models.append(
                ActiveModelDTO(
                    task=task,
                    modelName=task_cfg.get("model_name", "unknown"),
                    providerName=config.llm_provider,
                    temperature=task_cfg.get("temperature"),
                    maxTokens=task_cfg.get("max_tokens"),
                )
            )

    emb_cfg = config.get_embedding_model_config("default")
    embedding_model = emb_cfg.get("model_name", "text-embedding-3-small")

    return ModelsResponseDTO(
        llmProvider=config.llm_provider,
        embeddingProvider=config.embedding_provider,
        vectorStoreProvider=config.vectorstore_provider,
        activeModels=active_models,
        embeddingModel=embedding_model,
        availableProviders=["azure_openai", "openai", "ollama", "huggingface"],
    )
