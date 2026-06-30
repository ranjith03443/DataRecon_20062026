"""
AIResponseValidator — Validates, sanitizes, and rejects malformed AI responses.
Enforces structural integrity, confidence ranges, and required field presence.
"""
import json
import re
from typing import Any, Dict, List, Optional, Tuple

from loguru import logger

from app.domain.enums.confidence_enums import ConfidenceLevel, classify_confidence
from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import AIResponseValidationException


class AIResponseValidator:
    """
    Validates AI provider responses before they are returned to callers.

    Responsibilities:
    - Parse and validate JSON structure
    - Validate required fields
    - Validate confidence score ranges
    - Reject malformed or incomplete responses
    - Sanitize output strings
    - Log validation outcomes
    """

    def __init__(self):
        logger.info("[AIResponseValidator] Initialized", category=LogCategories.AUDIT)

    def parse_json_response(
        self,
        raw_response: str,
        job_id: str = "",
        request_id: str = "",
    ) -> Dict[str, Any]:
        """
        Parse and extract JSON from a raw LLM response string.
        Handles responses wrapped in markdown code blocks.

        Args:
            raw_response: Raw string from LLM.
            job_id: Job ID for logging.
            request_id: Request ID for logging.

        Returns:
            Parsed dictionary.

        Raises:
            AIResponseValidationException: If JSON cannot be parsed.
        """
        content = raw_response.strip()

        # Strip markdown code fences if present
        json_fence = re.search(r"```(?:json)?\s*([\s\S]+?)\s*```", content)
        if json_fence:
            content = json_fence.group(1).strip()

        # Extract JSON object
        json_match = re.search(r"\{[\s\S]+\}", content)
        if json_match:
            content = json_match.group(0)

        try:
            parsed = json.loads(content)
            logger.debug(
                f"[AIResponseValidator] JSON parsed successfully | "
                f"job_id={job_id} | request_id={request_id} | "
                f"workflow_step={WorkflowStepConstants.RESPONSE_VALIDATION}",
                category=LogCategories.AUDIT,
            )
            return parsed
        except json.JSONDecodeError as exc:
            logger.error(
                f"[AIResponseValidator] JSON parse FAILED | error={str(exc)} | "
                f"raw_length={len(raw_response)} | job_id={job_id} | request_id={request_id}",
                category=LogCategories.AUDIT,
            )
            raise AIResponseValidationException(
                f"AI response is not valid JSON: {str(exc)[:200]}",
                validation_errors=[f"JSONDecodeError: {str(exc)}"],
            )

    def validate_required_fields(
        self,
        data: Dict[str, Any],
        required_fields: List[str],
        job_id: str = "",
        request_id: str = "",
    ) -> None:
        """
        Validate that required fields are present in the parsed response.

        Raises:
            AIResponseValidationException: If any required fields are missing.
        """
        missing = [f for f in required_fields if f not in data]
        if missing:
            logger.error(
                f"[AIResponseValidator] Missing required fields | missing={missing} | "
                f"job_id={job_id} | request_id={request_id}",
                category=LogCategories.AUDIT,
            )
            raise AIResponseValidationException(
                f"AI response missing required fields: {missing}",
                validation_errors=[f"Missing field: {f}" for f in missing],
            )

    def validate_confidence(
        self,
        data: Dict[str, Any],
        job_id: str = "",
        request_id: str = "",
    ) -> float:
        """
        Validate and return the confidence score from the response.
        Clamps confidence to [0.0, 1.0] range.

        Returns:
            Validated confidence float.
        """
        confidence_raw = data.get("confidence")
        if confidence_raw is None:
            logger.warning(
                f"[AIResponseValidator] No confidence in response — defaulting to 0.5 | "
                f"job_id={job_id}",
                category=LogCategories.AUDIT,
            )
            return 0.5

        try:
            confidence = float(confidence_raw)
        except (TypeError, ValueError):
            raise AIResponseValidationException(
                f"Confidence value '{confidence_raw}' is not a valid float.",
                validation_errors=[f"Invalid confidence: {confidence_raw}"],
            )

        # Clamp to valid range
        confidence = max(0.0, min(1.0, confidence))
        level = classify_confidence(confidence)

        logger.info(
            f"[AIResponseValidator] Confidence validated | "
            f"confidence={confidence:.3f} | level={level} | "
            f"job_id={job_id} | request_id={request_id}",
            category=LogCategories.AUDIT,
        )
        return confidence

    def sanitize_string(self, value: str, max_length: int = 2000) -> str:
        """Sanitize a string value from AI response."""
        if not isinstance(value, str):
            value = str(value)
        # Remove null bytes
        value = value.replace("\x00", "")
        # Truncate
        if len(value) > max_length:
            value = value[:max_length] + "...[TRUNCATED]"
        return value.strip()

    def validate_mapping_response(
        self,
        raw_response: str,
        job_id: str = "",
        request_id: str = "",
    ) -> Tuple[Dict[str, Any], float]:
        """
        Full validation pipeline for semantic mapping AI response.

        Returns:
            Tuple of (validated_data_dict, confidence_float).
        """
        data = self.parse_json_response(raw_response, job_id=job_id, request_id=request_id)
        self.validate_required_fields(
            data, ["mapping", "confidence", "reasoning"],
            job_id=job_id, request_id=request_id,
        )
        confidence = self.validate_confidence(data, job_id=job_id, request_id=request_id)
        data["mapping"] = self.sanitize_string(str(data["mapping"]))
        data["reasoning"] = self.sanitize_string(str(data.get("reasoning", "")))
        data["confidence"] = confidence
        return data, confidence

    def validate_enrichment_response(
        self,
        raw_response: str,
        job_id: str = "",
        request_id: str = "",
    ) -> Tuple[Dict[str, Any], float]:
        """Full validation pipeline for schema enrichment AI response."""
        data = self.parse_json_response(raw_response, job_id=job_id, request_id=request_id)
        self.validate_required_fields(
            data, ["fieldName", "possibleMeaning", "businessCategory"],
            job_id=job_id, request_id=request_id,
        )
        confidence = self.validate_confidence(data, job_id=job_id, request_id=request_id)
        data["possibleMeaning"] = self.sanitize_string(str(data.get("possibleMeaning", "")))
        data["businessCategory"] = self.sanitize_string(str(data.get("businessCategory", "")))
        return data, confidence

    def validate_rule_inference_response(
        self,
        raw_response: str,
        job_id: str = "",
        request_id: str = "",
    ) -> Tuple[Dict[str, Any], float]:
        """Full validation pipeline for rule inference AI response."""
        data = self.parse_json_response(raw_response, job_id=job_id, request_id=request_id)
        self.validate_required_fields(
            data, ["operation", "confidence"],
            job_id=job_id, request_id=request_id,
        )
        confidence = self.validate_confidence(data, job_id=job_id, request_id=request_id)
        data["operation"] = self.sanitize_string(str(data.get("operation", "")))
        return data, confidence
