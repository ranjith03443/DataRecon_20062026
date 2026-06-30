"""
Multi-source schema service.
Uses the LLM to merge N source file schemas into one unified target schema with
source-field-to-target-field mappings and merge rules (PRIMARY / FALLBACK / CONCAT).
"""
import json
import re
from typing import Any

from loguru import logger

from app.application.dto.multi_source_schema_dto import (
    MultiSourceSchemaRequestDto,
    MultiSourceSchemaResponseDto,
    MultiSourceTargetFieldDto,
    SourceMappingDto,
)
from app.infrastructure.providers.llm.llm_factory import LLMProviderFactory
from app.shared.constants.app_constants import LogCategories

_SYSTEM_PROMPT = (
    "You are a data schema architect specialising in data integration and normalisation. "
    "Analyse multiple source file schemas and create a single, unified target schema. "
    "Respond with valid JSON only — no markdown fences, no commentary."
)


def _build_user_prompt(request: MultiSourceSchemaRequestDto) -> str:
    lines: list[str] = [
        f"Create a unified target schema named '{request.schema_name}' from the source files below.",
        "",
        "RULES:",
        "1. Merge columns that represent the same business concept across files "
        "   (e.g. ACCT_NO and ACCOUNT_NUMBER → one ACCOUNT_ID target column).",
        "2. Keep columns that are unique to one file.",
        "3. For merged columns assign a merge rule:",
        "   PRIMARY  – the canonical source (use this value).",
        "   FALLBACK – use only when PRIMARY is null or blank.",
        "   CONCAT   – concatenate values with a space (typically for name fragments).",
        "4. Use UPPERCASE_WITH_UNDERSCORES for all target field names.",
        "5. Infer datatypes: STRING, INTEGER, DECIMAL, DATETIME, BOOLEAN.",
        "6. Write concise English descriptions.",
        "",
        "SOURCE FILES:",
    ]
    for sf in request.source_files:
        lines.append(f"\n[File {sf.file_index}: {sf.file_name}]")
        for f in sf.fields:
            samples = ", ".join(f.sample_values[:3]) if f.sample_values else "N/A"
            lines.append(
                f"  - {f.field_name}  type={f.inferred_type}  maxlen={f.max_length}"
                f"  nullable={f.nullable}  samples=[{samples}]"
            )

    lines += [
        "",
        "Return ONLY a JSON object matching this exact structure (no extra keys):",
        json.dumps({
            "fields": [{
                "target_field": "EXAMPLE_ID",
                "source_mappings": [
                    {"source_file_name": "file_a.csv", "source_file_index": 0,
                     "source_field": "ID", "merge_rule": "PRIMARY"},
                    {"source_file_name": "file_b.csv", "source_file_index": 1,
                     "source_field": "IDENTIFIER", "merge_rule": "FALLBACK"},
                ],
                "datatype": "STRING",
                "field_length": 20,
                "nullable": False,
                "description": "Unique record identifier.",
                "business_category": "IDENTIFIER",
                "confidence": 0.95,
            }]
        }),
    ]
    return "\n".join(lines)


def _parse_response(schema_name: str, content: str) -> MultiSourceSchemaResponseDto:
    cleaned = re.sub(r"```(?:json)?\s*", "", content).strip().rstrip("`")
    try:
        data: dict[str, Any] = json.loads(cleaned)
    except json.JSONDecodeError:
        match = re.search(r"\{[\s\S]*\}", cleaned)
        if not match:
            raise ValueError(f"LLM returned non-JSON content: {cleaned[:300]}")
        data = json.loads(match.group())

    fields = []
    for raw in data.get("fields", []):
        mappings = [
            SourceMappingDto(
                source_file_name=m.get("source_file_name", ""),
                source_file_index=int(m.get("source_file_index", 0)),
                source_field=m.get("source_field", ""),
                merge_rule=m.get("merge_rule", "PRIMARY"),
            )
            for m in raw.get("source_mappings", [])
        ]
        fields.append(MultiSourceTargetFieldDto(
            target_field=raw.get("target_field", ""),
            source_mappings=mappings,
            datatype=raw.get("datatype", "STRING"),
            field_length=raw.get("field_length"),
            nullable=raw.get("nullable", True),
            description=raw.get("description"),
            business_category=raw.get("business_category"),
            confidence=float(raw.get("confidence", 0.80)),
            generation_method="AI MULTI-SOURCE",
        ))

    return MultiSourceSchemaResponseDto(schema_name=schema_name, fields=fields)


class MultiSourceSchemaService:
    async def generate(self, request: MultiSourceSchemaRequestDto) -> MultiSourceSchemaResponseDto:
        logger.info(
            f"[MultiSourceSchemaService] Generating unified schema | "
            f"schemaName={request.schema_name} | files={len(request.source_files)}",
            category=LogCategories.LLM_CALL,
        )
        provider = LLMProviderFactory.create(task="schema_enrichment")
        user_prompt = _build_user_prompt(request)
        response = await provider.complete(
            system_prompt=_SYSTEM_PROMPT,
            user_prompt=user_prompt,
            temperature=0.1,
            max_tokens=4000,
        )
        return _parse_response(request.schema_name, response.content)
