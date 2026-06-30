"""
Application-wide constants for the Semantic Intelligence Platform.
"""


class AppConstants:
    """Core application constants."""
    APP_NAME = "semantic-intelligence-platform"
    API_VERSION = "v1"
    DEFAULT_REQUEST_ID_HEADER = "X-Request-ID"
    DEFAULT_CORRELATION_ID_HEADER = "X-Correlation-ID"
    DEFAULT_JOB_ID_HEADER = "X-Job-ID"
    DEFAULT_API_KEY_HEADER = "X-API-Key"


class ProviderConstants:
    """AI provider name constants."""
    AZURE_OPENAI = "azure_openai"
    OPENAI = "openai"
    OLLAMA = "ollama"
    HUGGINGFACE = "huggingface"
    MISTRAL = "mistral"
    CLAUDE = "claude"


class WorkflowStepConstants:
    """Workflow step name constants for audit logging."""
    SEMANTIC_MAPPING = "semantic_mapping"
    SCHEMA_ENRICHMENT = "schema_enrichment"
    RULE_INFERENCE = "rule_inference"
    EMBEDDING_GENERATION = "embedding_generation"
    RAG_RETRIEVAL = "rag_retrieval"
    VECTOR_INDEXING = "vector_indexing"
    VECTOR_SEARCH = "vector_search"
    CONTEXT_BUILDING = "context_building"
    PROMPT_RENDERING = "prompt_rendering"
    LLM_INFERENCE = "llm_inference"
    RESPONSE_VALIDATION = "response_validation"
    CACHE_LOOKUP = "cache_lookup"
    CACHE_STORE = "cache_store"
    MAINFRAME_AI_AGENT = "mainframe_ai_agent"


class CollectionNames:
    """ChromaDB collection name constants."""
    HISTORICAL_MAPPINGS = "historical_mappings"
    BUSINESS_RULES = "business_rules"
    SEMANTIC_GLOSSARY = "semantic_glossary"
    TARGET_SCHEMA = "target_schema"
    FIELD_DESCRIPTIONS = "field_descriptions"
    ONBOARDING_METADATA = "onboarding_metadata"
    MAINFRAME_PATTERNS = "mainframe_patterns"


class ConfidenceThresholds:
    """AI confidence level threshold constants."""
    HIGH = 0.90
    MEDIUM = 0.70
    LOW = 0.50


class ConfidenceLevels:
    """AI confidence level label constants."""
    HIGH = "HIGH"
    MEDIUM = "MEDIUM"
    LOW = "LOW"
    INSUFFICIENT = "INSUFFICIENT"


class LogCategories:
    """Log category constants for structured logging."""
    API_REQUEST = "api_request"
    API_RESPONSE = "api_response"
    LLM_CALL = "llm_call"
    EMBEDDING = "embedding"
    RAG = "rag"
    CACHE = "cache"
    VECTOR_STORE = "vector_store"
    AUDIT = "audit"
    SECURITY = "security"
    HEALTH = "health"
    TOKEN_USAGE = "token_usage"
    MAINFRAME_AI_AGENT = "mainframe_ai_agent"


class EnvironmentVars:
    """Environment variable name constants."""
    AZURE_OPENAI_ENDPOINT = "AZURE_OPENAI_ENDPOINT"
    AZURE_OPENAI_API_KEY = "AZURE_OPENAI_API_KEY"
    AZURE_OPENAI_API_VERSION = "AZURE_OPENAI_API_VERSION"
    AZURE_OPENAI_DEPLOYMENT_NAME = "AZURE_OPENAI_DEPLOYMENT_NAME"
    AZURE_OPENAI_EMBEDDING_DEPLOYMENT = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"
    OPENAI_API_KEY = "OPENAI_API_KEY"
    HUGGINGFACE_TOKEN = "HUGGINGFACE_TOKEN"
    SEMANTIC_API_KEYS = "SEMANTIC_API_KEYS"
    ENVIRONMENT = "ENVIRONMENT"
    LOG_LEVEL = "LOG_LEVEL"
    CONFIG_PATH = "CONFIG_PATH"
