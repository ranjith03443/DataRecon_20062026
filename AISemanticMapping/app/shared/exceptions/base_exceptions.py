"""
Shared exceptions for the Semantic Intelligence Platform.
Provides a hierarchy of typed exceptions for structured error handling.
"""
from typing import Optional, Dict, Any


class SemanticPlatformException(Exception):
    """Base exception for all Semantic Intelligence Platform errors."""

    def __init__(
        self,
        message: str,
        error_code: str = "SEMANTIC_PLATFORM_ERROR",
        details: Optional[Dict[str, Any]] = None,
        job_id: Optional[str] = None,
        request_id: Optional[str] = None,
    ):
        super().__init__(message)
        self.message = message
        self.error_code = error_code
        self.details = details or {}
        self.job_id = job_id
        self.request_id = request_id

    def to_dict(self) -> Dict[str, Any]:
        return {
            "error_code": self.error_code,
            "message": self.message,
            "details": self.details,
            "job_id": self.job_id,
            "request_id": self.request_id,
        }


class ConfigurationException(SemanticPlatformException):
    """Raised when there is a configuration error."""

    def __init__(self, message: str, config_key: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="CONFIGURATION_ERROR",
            details={"config_key": config_key},
        )


class ProviderException(SemanticPlatformException):
    """Raised when an AI provider fails."""

    def __init__(
        self,
        message: str,
        provider_name: str,
        model_name: Optional[str] = None,
        retry_count: int = 0,
    ):
        super().__init__(
            message=message,
            error_code="PROVIDER_ERROR",
            details={
                "provider_name": provider_name,
                "model_name": model_name,
                "retry_count": retry_count,
            },
        )
        self.provider_name = provider_name


class EmbeddingException(SemanticPlatformException):
    """Raised when embedding generation fails."""

    def __init__(self, message: str, provider_name: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="EMBEDDING_ERROR",
            details={"provider_name": provider_name},
        )


class VectorStoreException(SemanticPlatformException):
    """Raised when vector store operations fail."""

    def __init__(self, message: str, collection: Optional[str] = None, operation: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="VECTOR_STORE_ERROR",
            details={"collection": collection, "operation": operation},
        )


class SemanticMappingException(SemanticPlatformException):
    """Raised when semantic mapping inference fails."""

    def __init__(self, message: str, source_field: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="SEMANTIC_MAPPING_ERROR",
            details={"source_field": source_field},
        )


class RuleInferenceException(SemanticPlatformException):
    """Raised when rule inference fails."""

    def __init__(self, message: str, rule: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="RULE_INFERENCE_ERROR",
            details={"rule": rule},
        )


class AIResponseValidationException(SemanticPlatformException):
    """Raised when AI response validation fails."""

    def __init__(self, message: str, validation_errors: Optional[list] = None):
        super().__init__(
            message=message,
            error_code="AI_RESPONSE_VALIDATION_ERROR",
            details={"validation_errors": validation_errors or []},
        )


class PromptException(SemanticPlatformException):
    """Raised when prompt loading or rendering fails."""

    def __init__(self, message: str, prompt_id: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="PROMPT_ERROR",
            details={"prompt_id": prompt_id},
        )


class RAGException(SemanticPlatformException):
    """Raised when RAG retrieval fails."""

    def __init__(self, message: str, collection: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="RAG_ERROR",
            details={"collection": collection},
        )


class AuthenticationException(SemanticPlatformException):
    """Raised when authentication fails."""

    def __init__(self, message: str = "Authentication failed"):
        super().__init__(message=message, error_code="AUTHENTICATION_ERROR")


class AuthorizationException(SemanticPlatformException):
    """Raised when authorization fails."""

    def __init__(self, message: str = "Authorization failed"):
        super().__init__(message=message, error_code="AUTHORIZATION_ERROR")


class ValidationException(SemanticPlatformException):
    """Raised when request validation fails."""

    def __init__(self, message: str, field: Optional[str] = None):
        super().__init__(
            message=message,
            error_code="VALIDATION_ERROR",
            details={"field": field},
        )
