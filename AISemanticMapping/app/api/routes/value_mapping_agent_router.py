"""
Value Mapping Agent Router — POST /api/value-mapping-agent
Infers source-value → target-value mappings for low-cardinality coded fields.
"""
from typing import Any, Dict, List, Optional
from fastapi import APIRouter, Request
from loguru import logger
from pydantic import BaseModel, Field
import json

from app.infrastructure.config.config_loader import get_app_config

router = APIRouter(prefix="/value-mapping-agent", tags=["Value Mapping Agent"])


# ── DTOs ──────────────────────────────────────────────────────────────────────

class ValueMappingAgentRequest(BaseModel):
    jobId: str = Field(..., description="Workflow job ID")
    sourceField: str = Field(..., description="Source field name")
    targetField: str = Field(..., description="Target field name")
    sourceValues: List[str] = Field(..., description="Distinct source values to map")
    targetDescription: Optional[str] = Field(None, description="Target field description from schema")
    sourceParameterValues: Optional[List[str]] = Field(None, description="Reference values from source parameter file")
    targetParameterValues: Optional[List[str]] = Field(None, description="Reference values from target parameter file")
    workflowStep: Optional[str] = Field("ValueMappingDiscovery", description="Workflow step name")


class ValueMappingRule(BaseModel):
    sourceValue: str
    targetValue: str
    confidence: float
    isAiSuggested: bool = True
    reasoning: Optional[str] = None


class ValueMappingAgentResponse(BaseModel):
    jobId: str
    sourceField: str
    targetField: str
    confidence: float
    mappings: List[ValueMappingRule]
    cached: bool = False
    reasoning: Optional[str] = None


# ── LLM helper ────────────────────────────────────────────────────────────────

async def _call_llm(prompt: str) -> str:
    """Call the configured LLM to infer value mappings."""
    config = get_app_config()
    provider = (config.llm_provider or "").lower()

    if provider == "azure_openai":
        try:
            from openai import AzureOpenAI
            import os
            client = AzureOpenAI(
                azure_endpoint=os.environ.get("AZURE_OPENAI_ENDPOINT", ""),
                api_key=os.environ.get("AZURE_OPENAI_API_KEY", ""),
                api_version=os.environ.get("AZURE_OPENAI_API_VERSION", "2024-02-01"),
            )
            deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT_NAME", "gpt-4o")
            response = client.chat.completions.create(
                model=deployment,
                messages=[
                    {"role": "system", "content": "You are a data mapping expert. Respond with structured JSON only."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=1024,
                temperature=0.1,
            )
            return response.choices[0].message.content or ""
        except Exception as e:
            logger.error(f"[ValueMappingAgent] LLM call failed: {e}")
            return ""

    elif provider == "openai":
        try:
            from openai import OpenAI
            import os
            client = OpenAI(api_key=os.environ.get("OPENAI_API_KEY", ""))
            model = os.environ.get("OPENAI_MODEL", "gpt-4o")
            response = client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": "You are a data mapping expert. Respond with structured JSON only."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=1024,
                temperature=0.1,
            )
            return response.choices[0].message.content or ""
        except Exception as e:
            logger.error(f"[ValueMappingAgent] LLM call failed: {e}")
            return ""
    else:
        logger.warning("[ValueMappingAgent] No LLM provider configured — returning empty mappings.")
        return ""


def _build_prompt(req: ValueMappingAgentRequest) -> str:
    param_context = ""
    if req.sourceParameterValues:
        param_context += f"\nSource parameter reference values: {req.sourceParameterValues}"
    if req.targetParameterValues:
        param_context += f"\nTarget parameter reference values: {req.targetParameterValues}"
    if req.targetDescription:
        param_context += f"\nTarget field description: {req.targetDescription}"

    return f"""You are a data transformation expert. Map source coded values to their business meaning.

Source field: {req.sourceField}
Target field: {req.targetField}
Source values to map: {req.sourceValues}{param_context}

Instructions:
- For each source value, infer the most likely target business value.
- Use contextual clues from field names, descriptions, and any reference values provided.
- Common patterns: Y/N → Active/Inactive, 1/0 → Yes/No, numeric codes → descriptive labels.
- Return ONLY valid JSON, no markdown, no explanation outside JSON.

Required JSON format:
{{
  "overall_confidence": 0.85,
  "reasoning": "Brief explanation of mapping logic",
  "mappings": [
    {{"source_value": "Y", "target_value": "Active", "confidence": 0.95, "reasoning": "Y typically means active/yes"}},
    {{"source_value": "N", "target_value": "Inactive", "confidence": 0.95, "reasoning": "N typically means inactive/no"}}
  ]
}}"""


# ── Route ─────────────────────────────────────────────────────────────────────

@router.post(
    "",
    response_model=ValueMappingAgentResponse,
    summary="Infer value mappings for coded source fields",
    description=(
        "Uses AI to infer source-value to target-value mappings for low-cardinality coded fields. "
        "Considers field names, target descriptions, and optional parameter file reference values."
    ),
)
async def infer_value_mappings(
    request: Request,
    body: ValueMappingAgentRequest,
) -> ValueMappingAgentResponse:
    request_id = getattr(request.state, "request_id", None)
    logger.info(
        f"[ValueMappingAgent] POST /value-mapping-agent | "
        f"jobId={body.jobId} | sourceField={body.sourceField} | "
        f"targetField={body.targetField} | valueCount={len(body.sourceValues)} | "
        f"request_id={request_id}"
    )

    if not body.sourceValues:
        return ValueMappingAgentResponse(
            jobId=body.jobId,
            sourceField=body.sourceField,
            targetField=body.targetField,
            confidence=0.0,
            mappings=[],
            reasoning="No source values provided.",
        )

    prompt = _build_prompt(body)
    raw_response = await _call_llm(prompt)

    mappings: List[ValueMappingRule] = []
    overall_confidence = 0.0
    reasoning = "AI inference not available."

    if raw_response:
        try:
            # Strip markdown code fences if present
            cleaned = raw_response.strip()
            if cleaned.startswith("```"):
                cleaned = cleaned.split("```")[1]
                if cleaned.startswith("json"):
                    cleaned = cleaned[4:]
            cleaned = cleaned.strip()

            parsed = json.loads(cleaned)
            overall_confidence = float(parsed.get("overall_confidence", 0.7))
            reasoning = parsed.get("reasoning", "")

            for m in parsed.get("mappings", []):
                mappings.append(ValueMappingRule(
                    sourceValue=str(m.get("source_value", "")),
                    targetValue=str(m.get("target_value", "")),
                    confidence=float(m.get("confidence", 0.7)),
                    isAiSuggested=True,
                    reasoning=m.get("reasoning"),
                ))

            logger.info(
                f"[ValueMappingAgent] AI inference succeeded. "
                f"jobId={body.jobId} field={body.sourceField} mappings={len(mappings)} "
                f"confidence={overall_confidence}"
            )
        except Exception as e:
            logger.error(f"[ValueMappingAgent] Failed to parse LLM response: {e}. Raw: {raw_response[:200]}")

    return ValueMappingAgentResponse(
        jobId=body.jobId,
        sourceField=body.sourceField,
        targetField=body.targetField,
        confidence=overall_confidence,
        mappings=mappings,
        cached=False,
        reasoning=reasoning,
    )
