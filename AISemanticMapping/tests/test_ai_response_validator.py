"""
Tests for AIResponseValidator.
"""
import pytest
from app.ai.validators.ai_response_validator import AIResponseValidator
from app.shared.exceptions.base_exceptions import AIResponseValidationException


@pytest.fixture
def validator():
    return AIResponseValidator()


def test_parse_valid_json(validator):
    raw = '{"mapping": "CLIENT_ID", "confidence": 0.93, "reasoning": "Matched."}'
    result = validator.parse_json_response(raw)
    assert result["mapping"] == "CLIENT_ID"


def test_parse_json_with_markdown_fence(validator):
    raw = '```json\n{"mapping": "CLIENT_ID", "confidence": 0.93, "reasoning": "OK"}\n```'
    result = validator.parse_json_response(raw)
    assert result["mapping"] == "CLIENT_ID"


def test_parse_invalid_json_raises(validator):
    with pytest.raises(AIResponseValidationException):
        validator.parse_json_response("this is not json")


def test_validate_confidence_clamps(validator):
    data = {"mapping": "X", "confidence": 1.5, "reasoning": "OK"}
    conf = validator.validate_confidence(data)
    assert conf == 1.0


def test_validate_confidence_missing_returns_default(validator):
    data = {"mapping": "X", "reasoning": "OK"}
    conf = validator.validate_confidence(data)
    assert conf == 0.5


def test_validate_required_fields_missing_raises(validator):
    with pytest.raises(AIResponseValidationException):
        validator.validate_required_fields(
            {"mapping": "X"},
            required_fields=["mapping", "confidence", "reasoning"],
        )


def test_full_mapping_validation(validator):
    raw = '{"mapping": "CLIENT_ID", "confidence": 0.93, "reasoning": "Good match.", "alternatives": []}'
    data, conf = validator.validate_mapping_response(raw)
    assert data["mapping"] == "CLIENT_ID"
    assert conf == pytest.approx(0.93, abs=0.01)
