"""
ContextBuilderService — Assembles enriched context from RAG results for LLM prompts.
Builds structured context blocks from retrieval results.
"""
from typing import List, Optional

from loguru import logger

from app.infrastructure.vectorstore.ivectorstore import VectorSearchResult
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants


class ContextBuilderService:
    """
    Assembles semantically retrieved context into structured prompt-ready blocks.
    Used by all AI services to enrich prompts before LLM inference.
    """

    def __init__(self, min_relevance_score: float = 0.70):
        self._min_relevance_score = min_relevance_score
        logger.info(
            f"[ContextBuilderService] Initialized | min_relevance_score={min_relevance_score}",
            category=LogCategories.RAG,
        )

    def build_mapping_context(
        self,
        historical_mappings: List[VectorSearchResult],
        glossary_terms: List[VectorSearchResult],
        field_descriptions: List[VectorSearchResult],
        job_id: str = "",
        request_id: str = "",
    ) -> str:
        """
        Build context string for semantic mapping enrichment.

        Args:
            historical_mappings: Retrieved historical mapping documents.
            glossary_terms: Retrieved glossary entries.
            field_descriptions: Retrieved field description documents.
            job_id: Job ID for logging.
            request_id: Request ID for logging.

        Returns:
            Formatted context string for prompt injection.
        """
        logger.debug(
            f"[ContextBuilderService] Building mapping context | "
            f"historical={len(historical_mappings)} | glossary={len(glossary_terms)} | "
            f"field_desc={len(field_descriptions)} | job_id={job_id} | "
            f"workflow_step={WorkflowStepConstants.CONTEXT_BUILDING}",
            category=LogCategories.RAG,
        )

        context_parts = []

        filtered_mappings = self._filter_by_score(historical_mappings)
        if filtered_mappings:
            context_parts.append("=== HISTORICAL FIELD MAPPINGS ===")
            for r in filtered_mappings:
                context_parts.append(f"- [{r.score:.2f}] {r.document}")

        filtered_glossary = self._filter_by_score(glossary_terms)
        if filtered_glossary:
            context_parts.append("=== SEMANTIC GLOSSARY ===")
            for r in filtered_glossary:
                context_parts.append(f"- [{r.score:.2f}] {r.document}")

        filtered_fields = self._filter_by_score(field_descriptions)
        if filtered_fields:
            context_parts.append("=== TARGET FIELD DESCRIPTIONS ===")
            for r in filtered_fields:
                context_parts.append(f"- [{r.score:.2f}] {r.document}")

        if not context_parts:
            context = "No relevant historical context found. Use general semantic reasoning."
        else:
            context = "\n".join(context_parts)

        logger.info(
            f"[ContextBuilderService] Context assembled | "
            f"sections={len(context_parts)} | chars={len(context)} | "
            f"job_id={job_id} | request_id={request_id}",
            category=LogCategories.RAG,
        )
        return context

    def build_rule_context(
        self,
        business_rules: List[VectorSearchResult],
        glossary_terms: List[VectorSearchResult],
        job_id: str = "",
        request_id: str = "",
    ) -> str:
        """Build context for rule inference."""
        context_parts = []
        for label, results in [
            ("=== RELEVANT BUSINESS RULES ===", business_rules),
            ("=== SEMANTIC GLOSSARY ===", glossary_terms),
        ]:
            filtered = self._filter_by_score(results)
            if filtered:
                context_parts.append(label)
                for r in filtered:
                    context_parts.append(f"- [{r.score:.2f}] {r.document}")

        return "\n".join(context_parts) if context_parts else "No relevant rule context found."

    def build_enrichment_context(
        self,
        glossary_terms: List[VectorSearchResult],
        field_descriptions: List[VectorSearchResult],
        job_id: str = "",
        request_id: str = "",
    ) -> str:
        """Build context for schema enrichment."""
        context_parts = []
        for label, results in [
            ("=== SEMANTIC GLOSSARY ===", glossary_terms),
            ("=== SIMILAR FIELD DESCRIPTIONS ===", field_descriptions),
        ]:
            filtered = self._filter_by_score(results)
            if filtered:
                context_parts.append(label)
                for r in filtered:
                    context_parts.append(f"- [{r.score:.2f}] {r.document}")

        return "\n".join(context_parts) if context_parts else "No relevant enrichment context found."

    def _filter_by_score(self, results: List[VectorSearchResult]) -> List[VectorSearchResult]:
        """Filter results below the minimum relevance score threshold."""
        return [r for r in results if r.score >= self._min_relevance_score]
