"""
Security sanitization utilities for PII masking and prompt sanitization.
"""
import re
from typing import Any, Dict, List, Optional


# PII masking patterns
_PII_PATTERNS = {
    "ssn": re.compile(r"\b\d{3}-\d{2}-\d{4}\b"),
    "email": re.compile(r"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}"),
    "account_number_long": re.compile(r"\b\d{10,17}\b"),
    "credit_card": re.compile(r"\b(?:\d{4}[\s\-]?){3}\d{4}\b"),
    "phone_us": re.compile(r"\b(?:\+1[\s\-]?)?\(?\d{3}\)?[\s\-]?\d{3}[\s\-]?\d{4}\b"),
}

_SENSITIVE_DICT_KEYS = frozenset(
    [
        "api_key", "apikey", "password", "secret", "token",
        "account_number", "ssn", "credit_card", "phone",
        "customer_id", "client_id",
    ]
)


def mask_pii(text: str) -> str:
    """Mask PII patterns in a text string."""
    masked = text
    masked = _PII_PATTERNS["ssn"].sub("[SSN-MASKED]", masked)
    masked = _PII_PATTERNS["email"].sub("[EMAIL-MASKED]", masked)
    masked = _PII_PATTERNS["credit_card"].sub("[CARD-MASKED]", masked)
    masked = _PII_PATTERNS["phone_us"].sub("[PHONE-MASKED]", masked)
    return masked


def sanitize_prompt(prompt: str) -> str:
    """
    Sanitize a prompt string for safe LLM submission.
    Removes potential injection patterns and masks PII.
    """
    # Remove potential prompt injection markers
    sanitized = re.sub(r"(?i)ignore\s+previous\s+instructions?", "[SANITIZED]", prompt)
    sanitized = re.sub(r"(?i)system\s*:", "[SANITIZED-SYSTEM]:", sanitized)
    # Mask PII
    sanitized = mask_pii(sanitized)
    return sanitized.strip()


def mask_sensitive_dict(data: Dict[str, Any], depth: int = 0) -> Dict[str, Any]:
    """Recursively mask sensitive values in a dictionary."""
    if depth > 5:
        return data
    result = {}
    for key, value in data.items():
        if isinstance(key, str) and key.lower() in _SENSITIVE_DICT_KEYS:
            result[key] = "***MASKED***"
        elif isinstance(value, dict):
            result[key] = mask_sensitive_dict(value, depth + 1)
        elif isinstance(value, list):
            result[key] = [
                mask_sensitive_dict(v, depth + 1) if isinstance(v, dict) else v
                for v in value
            ]
        else:
            result[key] = value
    return result


def truncate_for_log(value: str, max_length: int = 200) -> str:
    """Truncate a string for safe log output."""
    if len(value) > max_length:
        return value[:max_length] + f"...[TRUNCATED, total={len(value)} chars]"
    return value


def safe_exc(exc: Exception, limit: int = 200) -> str:
    """
    Return an exception string safe for loguru f-string log messages.
    Escapes { and } so loguru's internal .format() call does not treat
    JSON-like error payloads (e.g. {"type": "invalid_request_error"})
    as format placeholders, which would raise KeyError.
    """
    return str(exc)[:limit].replace("{", "{{").replace("}", "}}")
