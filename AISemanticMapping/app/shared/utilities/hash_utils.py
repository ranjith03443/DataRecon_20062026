"""
Hashing utilities for deterministic cache key generation.
"""
import hashlib
import json
from typing import Any, Dict, List, Optional


def hash_string(value: str) -> str:
    """Generate a SHA-256 hash of a string."""
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def hash_dict(data: Dict[str, Any]) -> str:
    """Generate a deterministic hash of a dictionary."""
    normalized = json.dumps(data, sort_keys=True, default=str)
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:16]


def hash_list(items: List[Any]) -> str:
    """Generate a deterministic hash of a list."""
    normalized = json.dumps(sorted(str(i) for i in items))
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:16]


def build_cache_key(
    operation: str,
    source_field: Optional[str] = None,
    target_candidates: Optional[List[str]] = None,
    source_metadata: Optional[Dict[str, Any]] = None,
    rule: Optional[str] = None,
    prompt_version: Optional[str] = None,
    extra: Optional[Dict[str, Any]] = None,
) -> str:
    """
    Build a deterministic cache key from semantic operation inputs.

    Args:
        operation: The semantic operation type (e.g., 'semantic_mapping').
        source_field: Source field name.
        target_candidates: List of target field candidates.
        source_metadata: Source field metadata dict.
        rule: Business rule string.
        prompt_version: Active prompt version string.
        extra: Any additional discriminating data.

    Returns:
        Deterministic cache key string.
    """
    parts = [operation]
    if source_field:
        parts.append(hash_string(source_field.upper().strip()))
    if target_candidates:
        parts.append(hash_list(target_candidates))
    if source_metadata:
        parts.append(hash_dict(source_metadata))
    if rule:
        parts.append(hash_string(rule.strip()))
    if prompt_version:
        parts.append(hash_string(prompt_version))
    if extra:
        parts.append(hash_dict(extra))
    return ":".join(parts)
