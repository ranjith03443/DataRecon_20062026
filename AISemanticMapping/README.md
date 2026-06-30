# Semantic Intelligence Platform

Enterprise-grade Python FastAPI microservice that acts as the **AI/semantic intelligence layer** for the .NET Data Reconciliation orchestrator. It exposes three advisory endpoints consumed during the 14-step workflow pipeline.

---

## Role in the Overall Architecture

```
.NET Orchestrator (DataReconciliation)
    │
    ├─ Step 5  ──POST /api/semantic-enrichment──► expand field abbreviations, infer business meaning
    ├─ Step 7  ──POST /api/semantic-mapping   ──► choose best source→target field match
    └─ Step 11 ──POST /api/rule-inference     ──► interpret free-text transformation rules
```

All responses are **advisory only** — the .NET service makes final decisions. This service owns no workflow state.

---

## API Endpoints

### POST `/api/semantic-enrichment` — Step 5

Expands abbreviated field names into their full business meaning.

**Request**
```json
{
  "fieldName":         "ACC_OPN_DT",
  "additionalContext": "Retail banking onboarding",
  "jobId":             "JOB_20260526_ABC123",
  "requestId":         "optional-correlation-id"
}
```

**Response**
```json
{
  "fieldName":             "ACC_OPN_DT",
  "possibleMeaning":       "Account Open Date",
  "businessCategory":      "ACCOUNT",
  "description":           "The date the account was opened by the customer",
  "dataTypeHint":          "DATE",
  "abbreviationsExpanded": { "ACC": "Account", "OPN": "Open", "DT": "Date" },
  "confidence":            0.95,
  "confidenceLevel":       "HIGH",
  "promptVersion":         "schema_enrichment_v1",
  "cached":                false,
  "requestId":             "optional-correlation-id"
}
```

The enriched `possibleMeaning` and `dataTypeHint` are stored by the .NET orchestrator as `SemanticFieldEnrichment` artifacts (`semantic_schema_profile_{datasetId}.json`) and passed to Steps 6 and 7 to improve both deterministic and AI matching.

---

### POST `/api/semantic-mapping` — Step 7

Chooses the best target field for a source field when deterministic matching scored below 0.85, or when the source field had **no** deterministic candidate at all.

**Request**
```json
{
  "jobId":                  "JOB_20260526_ABC123",
  "sourceField":            "HANDY_NUM",
  "targetFieldCandidates":  ["PHONE_NUM", "MOBILE_NO", "CONTACT_NO"],
  "sourceMetadata": {
    "datatype":      "VARCHAR",
    "description":   "Phone number of the employee",
    "nullable":      true,
    "sample_values": ["0049-891234567", "+44 7911 123456"],
    "max_length":    20
  },
  "requestId":    "map_HANDY_NUM_SRC_DATA",
  "workflowStep": "AISemanticMapping"
}
```

**Response**
```json
{
  "jobId":             "JOB_20260526_ABC123",
  "mapping":           "PHONE_NUM",
  "confidence":        0.93,
  "confidenceLevel":   "HIGH",
  "reasoning":         "HANDY_NUM is a German abbreviation for mobile/phone number, matching PHONE_NUM",
  "alternatives":      [{ "field": "MOBILE_NO", "confidence": 0.72 }],
  "promptVersion":     "semantic_mapping_v1",
  "cached":            false,
  "requestId":         "map_HANDY_NUM_SRC_DATA",
  "promptTokens":      312,
  "completionTokens":  88,
  "totalTokens":       400,
  "estimatedCostUsd":  0.000180,
  "modelName":         "gpt-4o-mini"
}
```

Token usage fields (`promptTokens`, `completionTokens`, `totalTokens`, `estimatedCostUsd`, `modelName`) are included on every **live** (non-cached) response and stored in the .NET `AIInferenceAudit` table, visible on the **AI Audit** report page.

---

### POST `/api/rule-inference` — Step 11

Interprets a free-text business rule from the target schema's Rule column and returns a structured transformation operation.

**Request**
```json
{
  "rule":          "Convert date from MM/DD/YYYY to YYYYMMDD",
  "fieldContext":  "BIRTH_DATE",
  "jobId":         "JOB_20260526_ABC123",
  "workflowStep":  "TransformationExecution"
}
```

**Response**
```json
{
  "operation":       "DATE_FORMAT_CONVERSION",
  "format":          "YYYYMMDD",
  "parameters":      { "input_format": "MM/DD/YYYY", "output_format": "YYYYMMDD" },
  "description":     "Convert date string from MM/DD/YYYY to YYYYMMDD",
  "validationRules": ["Must be 8 digits", "Must be a valid calendar date"],
  "confidence":      0.97,
  "confidenceLevel": "HIGH",
  "reasoning":       "Standard ISO date reformat",
  "promptVersion":   "rule_inference_v1",
  "cached":          false
}
```

---

### Other Endpoints

| Method | Path | Description |
|---|---|---|
| GET  | `/health` | Liveness probe — returns `{"status":"healthy"}` |
| GET  | `/api/models` | Active LLM/embedding models and provider details |
| POST | `/api/vector-store/index` | Index documents into ChromaDB |
| POST | `/api/vector-store/search` | Vector similarity search |

Interactive API docs: **http://localhost:8000/docs**

---

## Project Structure

```
AISemanticMapping/
├── main.py                        # Application entry point
├── requirements.txt
├── pyproject.toml
├── config/
│   ├── app_config.yaml            # Feature flags, server, AI governance thresholds
│   ├── models.yaml                # LLM + embedding model settings per endpoint
│   ├── cache.yaml                 # Semantic cache TTL and size
│   ├── logging.yaml               # Log levels, sinks, PII masking
│   ├── security.yaml              # API key auth, bypass flag
│   └── vectorstore.yaml           # ChromaDB path and collection names
├── prompts/
│   ├── semantic_mapping_v1.yaml   # Prompt template for /api/semantic-mapping
│   ├── schema_enrichment_v1.yaml  # Prompt template for /api/semantic-enrichment
│   └── rule_inference_v1.yaml     # Prompt template for /api/rule-inference
├── app/
│   ├── api/
│   │   ├── routes/                # FastAPI routers (one per endpoint)
│   │   └── middleware/            # Correlation ID, request logging
│   ├── application/
│   │   ├── dto/                   # Pydantic request/response DTOs
│   │   └── services/              # SemanticMappingService, RuleInferenceService, …
│   ├── domain/
│   │   ├── models/audit_models.py # AITokenUsageAudit dataclass + estimate_cost()
│   │   └── enums/                 # ConfidenceLevel enum
│   ├── infrastructure/
│   │   ├── providers/llm/         # AzureOpenAIProvider, OpenAIProvider, OllamaProvider
│   │   ├── providers/embeddings/  # Embedding providers
│   │   ├── cache/                 # In-memory semantic cache
│   │   ├── vectorstore/           # ChromaDB client wrapper
│   │   └── config/                # YAML config loader
│   ├── ai/
│   │   ├── embeddings/            # Embedding pipeline
│   │   ├── rag/                   # ContextBuilderService, SemanticRetrieverService
│   │   ├── prompts/               # PromptManager, PromptVersioningService
│   │   └── validators/            # AIResponseValidator (structure + schema)
│   └── shared/
│       ├── constants/             # LogCategories, WorkflowStepConstants
│       └── exceptions/            # Domain-specific exception hierarchy
├── python_logs/                   # Runtime log files (TOKEN_USAGE, LLM_CALL, …)
└── tests/
    ├── conftest.py
    ├── test_semantic_mapping_service.py
    ├── test_ai_response_validator.py
    └── test_api_integration.py
```

---

## Quick Start

### 1. Prerequisites

- Python 3.11+
- Azure OpenAI account **or** an OpenAI API key (see provider config below)

### 2. Create virtual environment

```bash
cd AISemanticMapping
python -m venv venv
# Windows
venv\Scripts\activate
# macOS/Linux
source venv/bin/activate
pip install -r requirements.txt
```

### 3. Configure environment variables

Create a `.env` file in `AISemanticMapping/`:

```env
# Azure OpenAI (primary provider)
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_API_KEY=<your-key>
AZURE_OPENAI_DEPLOYMENT_NAME=gpt-4o-mini
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-3-small

# Optional: standard OpenAI fallback
# OPENAI_API_KEY=sk-...

# Security
SEMANTIC_API_KEYS=dev-key-1,dev-key-2
ENVIRONMENT=dev
LOG_LEVEL=INFO
```

### 4. Run

```bash
python main.py
```

The service starts on **http://localhost:8000**. The .NET orchestrator expects it here by default (configurable via `appsettings.json → AI:PythonService:BaseUrl`).

---

## Configuration Reference

### `config/app_config.yaml` — Feature Flags

| Flag | Default | Effect when `false` |
|---|---|---|
| `enable_rag` | `false` | RAG context skipped; LLM uses prompt only |
| `enable_semantic_cache` | `false` | Every request hits the LLM |
| `enable_ai_mapping` | `true` | `/api/semantic-mapping` returns errors |
| `enable_token_governance` | `true` | Token usage not logged |
| `enable_prompt_versioning` | `true` | Prompt version not tracked |

> **Testing tip:** set `enable_rag: false` to avoid Azure embedding errors when the embedding deployment is unavailable. The mapping quality is unaffected — RAG only adds historical context.

### `config/models.yaml` — LLM Models

| Endpoint | Model | Temperature | Max Tokens |
|---|---|---|---|
| `/api/semantic-mapping` | `gpt-4o-mini` | 0.1 | 2000 |
| `/api/semantic-enrichment` | `gpt-4o-mini` | 0.1 | 1000 |
| `/api/rule-inference` | `gpt-4o-mini` | 0.0 | 1000 |
| Embeddings | `text-embedding-3-small` | — | — |

### `prompts/` — Externalized Prompts

All LLM prompts are version-controlled YAML files. Prompt version tags are returned in every API response and stored in the .NET audit log, making it possible to correlate mapping decisions with specific prompt versions.

---

## AI Governance

| Confidence Score | Level | Cached for reuse |
|---|---|---|
| ≥ 0.90 | HIGH | ✅ Yes |
| 0.70 – 0.89 | MEDIUM | ❌ No |
| 0.50 – 0.69 | LOW | ❌ No |
| < 0.50 | INSUFFICIENT | ❌ No |

- All responses are **advisory** — the .NET orchestrator decides whether to accept them.
- Token usage is logged to `python_logs/` under the `TOKEN_USAGE` category and returned in the response payload so the .NET **AI Audit** page can display prompt tokens, completion tokens, and estimated cost per call.

---

## Token Cost Visibility

Every live (non-cached) `/api/semantic-mapping` call includes cost data in the response:

```json
"promptTokens":     312,
"completionTokens": 88,
"totalTokens":      400,
"estimatedCostUsd": 0.000180,
"modelName":        "gpt-4o-mini"
```

These are stored in the `AIInferenceAudits` table in the .NET SQLite database and surfaced on the **Reports → AI Audit** page as:
- Per-call prompt / completion / total token counts
- Per-call estimated USD cost
- Job-level totals in the summary banner

---

## Security

| Mode | Setting | Behaviour |
|---|---|---|
| Development | `bypass_authentication: true` (`config/security.yaml`) | All requests trusted, bypass logged |
| Production | `bypass_authentication: false` | `X-API-Key` header validated against `SEMANTIC_API_KEYS` |

PII masking is applied to all log outputs. Prompts are sanitized before LLM submission.

---

## Running Tests

```bash
pytest tests/ -v
```

---

## Integration with .NET Orchestrator

The .NET `appsettings.json` controls how the orchestrator connects to this service:

```json
"AI": {
  "Enabled": true,
  "Provider": "PythonService",
  "PythonService": {
    "BaseUrl":           "http://localhost:8000",
    "MappingEndpoint":   "/api/semantic-mapping",
    "TimeoutSeconds":    30
  }
}
```

Set `"Enabled": false` to skip all AI steps and run purely deterministic mapping.

