"""\nAPI routes package — exports all router modules.\n"""
from app.api.routes import (
    health_router,
    models_router,
    semantic_mapping_router,
    semantic_enrichment_router,
    rule_inference_router,
    vector_store_router,
    mainframe_agent_router,
    value_mapping_agent_router,
    budget_router,
    multi_source_schema_router,
)

__all__ = [
    "health_router",
    "models_router",
    "semantic_mapping_router",
    "semantic_enrichment_router",
    "rule_inference_router",
    "vector_store_router",
    "mainframe_agent_router",
    "value_mapping_agent_router",
    "budget_router",
    "multi_source_schema_router",
]
