"""
Confidence level enumeration and classification utilities.
"""
from enum import Enum


class ConfidenceLevel(str, Enum):
    """AI response confidence level classification."""
    HIGH = "HIGH"
    MEDIUM = "MEDIUM"
    LOW = "LOW"
    INSUFFICIENT = "INSUFFICIENT"


def classify_confidence(score: float) -> ConfidenceLevel:
    """
    Classify a confidence score into a ConfidenceLevel enum.

    Args:
        score: Float confidence score between 0.0 and 1.0.

    Returns:
        ConfidenceLevel classification.
    """
    if score >= 0.90:
        return ConfidenceLevel.HIGH
    elif score >= 0.70:
        return ConfidenceLevel.MEDIUM
    elif score >= 0.50:
        return ConfidenceLevel.LOW
    else:
        return ConfidenceLevel.INSUFFICIENT


def is_cacheable_confidence(score: float) -> bool:
    """Return True if confidence is high enough to cache (>= 0.90)."""
    return score >= 0.90
