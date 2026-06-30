# Data Reconciliation Platform

An enterprise-grade ASP.NET Core MVC (.NET 8) application for **metadata-driven intelligent reconciliation, transformation, and target file generation** for enterprise/mainframe onboarding workflows.

## Mainframe Development Centre

The Mainframe Development Centre is a job-centric workspace for generating deterministic artifacts, building mainframe starter assets, and running AI-assisted code guidance from a single screen.

### Purpose

- Work from one shared workflow job ID across all tabs.
- Generate mainframe artifacts directly from target schema data.
- Produce COBOL and JCL starter assets for developer review.
- Run AI guidance for explanation, review, enhancement, validation, documentation, and optimization.

### User Interface Areas

#### 1. Mainframe Artifacts

This tab generates deterministic outputs from the target schema only.

**What it does**

- Creates a copybook file (`.cpy`) from the extracted target schema.
- Produces a technical specification summary (`.txt`).
- Writes a JSON summary manifest (`.json`) for downstream use.

**Input fields**

- `Job ID` — identifies the workflow job whose artifacts will be used.
- `Record name` — logical business record name used in the generated outputs.
- `Application name` — application label included in the artifact headers.
- `Include comments in artifacts` — toggles whether explanatory comments are written.

**Outputs**

- Copybook download.
- Specification download.
- Summary manifest download.
- Generated field table showing field name, data type, length, picture clause, source field, and justification.

**Behavior**

- Reads the target schema only.
- Does not call AI.
- Fails early if the required job context or target schema is missing.

#### 2. Mainframe Assets

This tab generates starter implementation assets that are useful for a developer who wants to begin COBOL or JCL work.

**What it does**

- Creates a copybook file (`.cpy`).
- Creates a COBOL skeleton (`.cbl`).
- Creates a JCL skeleton (`.jcl`).
- Creates a technical specification document (`.txt`).
- Creates sample data records (`.dat`).

**Input fields**

- `Job ID` — selects the workflow job used to build the assets.
- `Record Name` — business record name for the generated assets.
- `Application Name` — application name embedded in the generated files.
- `Program Name` — COBOL program name, limited to 8 characters.
- `Job Name` — JCL job name, limited to 8 characters.
- `Sample Record Count` — number of sample records to generate.
- Output checkboxes — let the user choose which asset types to generate.

**Outputs**

- Copybook download.
- COBOL skeleton download.
- JCL skeleton download.
- Technical specification download.
- Sample records download.
- Generated asset table showing field order, type, length, start position, picture clause, source field, and required flag.

**Behavior**

- Uses schema and mapping data as the basis for generation.
- Produces starter code meant for developer review.
- Displays a warning that COBOL and JCL are not production-ready.

#### 3. AI Development Agent

This tab provides AI-assisted guidance for mainframe development tasks.

**Available actions**

- `Explain COBOL` — describes the COBOL skeleton in plain English.
- `Developer Docs` — generates developer-facing documentation.
- `Review COBOL` — highlights design, validation, and code quality concerns.
- `JCL Improvements` — suggests improvements for JCL structure and execution.
- `Optimisation` — suggests performance and maintainability improvements.
- `Enhance COBOL` — proposes code improvements and starter snippets.
- `Validation Logic` — generates validation patterns and checks.
- `Error Handling` — suggests error handling and recoverability patterns.

**Input fields**

- `Job ID` — the job whose COBOL, JCL, mappings, and schema data should be analyzed.

**Outputs**

- Natural-language summary.
- Optional generated code snippets.
- Structured recommendations.
- Warnings returned by the AI service.
- Confidence and response-time metadata.

**Behavior**

- Loads job-specific COBOL, JCL, mappings, value mappings, and schema data.
- Calls the Python AI service through the `MainframeController`.
- Returns a structured JSON response rendered in the UI.
- Shows an explicit error if the AI service is unavailable or returns no response.

### Shared Job Context

The page uses one shared job ID across the three tabs.

- Entering the job ID in one tab updates the other tabs.
- The active job banner shows the current job context.
- The same job ID is used for artifact generation, asset generation, and AI analysis.

### Typical Usage Flow

1. Enter or select a workflow job ID.
2. Generate the mainframe artifacts from the target schema.
3. Generate starter assets if COBOL or JCL output is needed.
4. Run an AI action such as explain, review, or documentation.
5. Download the generated files and review the AI guidance.

### Availability Notes

- The Mainframe Development Centre depends on the .NET application and the Python AI service.
- Artifact and asset generation are deterministic.
- AI guidance requires the Python service to be running and reachable.
- If the AI service is unavailable, the UI shows a clear error message instead of failing silently.

## Architecture

```
DataReconciliation/
├── Controllers/              # MVC + API Controllers
│   ├── WorkflowController.cs
│   ├── ReportsController.cs
│   ├── WorkflowApiController.cs
│   └── HomeController.cs
├── Application/
│   ├── Interfaces/           # Service + Repository contracts
│   ├── DTOs/                 # Data Transfer Objects
│   ├── Services/             # All 14 workflow step services
│   └── Workflow/             # WorkflowOrchestratorService
├── Domain/
│   ├── Entities/             # EF Core entities (5 tables)
│   ├── Models/               # Domain models & DTOs
│   └── Enums/                # All enumerations
├── Infrastructure/
│   ├── Persistence/          # AppDbContext (SQLite)
│   ├── Repositories/         # 5 repository implementations
│   ├── FileStorage/          # ArtifactPersistenceService
│   ├── Caching/              # AICacheService (IMemoryCache)
│   ├── Middleware/           # CorrelationId + GlobalException
│   └── TransformationStrategies/  # Strategy Pattern (10 strategies)
├── AI/
│   ├── Clients/              # OpenAI REST client (Polly retry)
│   └── Validators/           # AIResponseValidator
└── Views/
    ├── Workflow/             # Dashboard, Create, Details
    ├── Reports/              # Reconciliation, Mappings, AIAudit, ErrorLog
    └── Shared/               # _Layout.cshtml
```

## Workflow Pipeline (14 Steps)

The orchestrator executes all 14 steps sequentially. Every step reads input artifacts from disk, does its work, and persists an output artifact. The workflow can be replayed from any step.

```
source_data.csv ─┐
target_schema.csv ─┤  Steps 1–4  ──►  source_schema_profile
historical_mappings.csv ─┘              target_metadata_profile

source_schema_profile ──►  Step 5 (AI /semantic-enrichment) ──►  semantic_schema_profile
                                                                         │
              ┌──────────────────────────────────────────────────────────┘
              ▼
Step 6  (historical + deterministic)  ──►  mapping_candidates
Step 7  (AI /semantic-mapping)        ──►  ai_mapping_response
Step 8  (join key detection)          ──►  canonical_mapping_config
Step 9  (CSV → joined rows)           ──►  canonical_records
Step 10 (merge all mappings)          ──►  final_mapping_config  ◄── User edits on Mappings page

final_mapping_config ──►  Step 11 (AI /rule-inference) ──►  transformation_output
transformation_output ──►  Step 12  ──►  target_output.dat
transformation_output ──►  Step 13  ──►  reconciliation_result.json
```

---

### Phase 1 — Ingestion & Registration

#### Step 1 — DatasetRegistration

| Attribute | Detail |
|---|---|
| **Service** | `DatasetRegistrationService` |
| **Input** | User-uploaded files via UI + form metadata (aliases, roles, descriptions) |
| **Processing** | Builds a `DatasetManifest` registering each file with its role (`SOURCE`, `TARGET_SCHEMA`, `HISTORICAL_MAPPINGS`), type, and local disk path |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `dataset_manifest.json` — list of all datasets with paths and roles |

#### Step 2 — FileIngestion

| Attribute | Detail |
|---|---|
| **Service** | `FileIngestionService` |
| **Input** | Uploaded file streams from the HTTP multipart request |
| **Processing** | Streams each file to `workflow/{jobId}/input/{filename}` using an 80 KB buffer |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | Physical files on disk: `workflow/{jobId}/input/source_data.csv`, etc. |

#### Step 3 — SourceSchemaProfiling

| Attribute | Detail |
|---|---|
| **Service** | `SourceSchemaProfilingService` |
| **Input** | `dataset_manifest.json` → local paths of SOURCE files |
| **Processing** | Reads up to 1,000 rows per CSV; infers datatype (integer/decimal/datetime/boolean/string), nullability, max length, uniqueness, sample values, pattern (YYYYMMDD, Decimal etc.), and a plain-English possible meaning per column |
| **AI Call** | ❌ None (pure rule-based) |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `source_schema_profile_{datasetId}.json` per source — `SourceFieldProfile[]` with field stats and up to 1,000 sample values |

---

### Phase 2 — Schema Analysis

#### Step 4 — TargetMetadataExtraction

| Attribute | Detail |
|---|---|
| **Service** | `TargetMetadataExtractionService` |
| **Input** | `dataset_manifest.json` → path of `TARGET_SCHEMA` file (CSV or Excel) |
| **Processing** | Parses each column; reads optional metadata columns (`FIELD_NAME`, `DATATYPE`, `FORMAT`, `RULE`, `DESCRIPTION`, `HARDCODED_VALUE`, `FIELD_LENGTH`); assigns column order |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `target_metadata_profile.json` — ordered `TargetFieldMetadata[]` with format rules, required flags, and hardcoded values |

#### Step 5 — SemanticSchemaEnrichment

| Attribute | Detail |
|---|---|
| **Service** | `SemanticSchemaEnrichmentService` |
| **Input** | `source_schema_profile_{id}.json` — fields with `FieldName`, `Datatype`, `SampleValues` |
| **Processing** | Identifies ambiguous fields (no meaning, or name ≤ 3 chars). Clear fields get a rule-based category (Customer/Financial/Date etc.). Ambiguous fields go to AI. |
| **AI Call** | ✅ `POST /api/semantic-enrichment` — dedicated endpoint with `schema_enrichment_v1` prompt, RAG retrieval, caching, and token audit |
| **AI Input** | `{ "fieldName": "ACC_OPN_DT", "jobId": "JOB_...", "additionalContext": "datatype=date, samples=20160101,20180315" }` |
| **AI Output** | `{ "possibleMeaning": "Account Open Date", "businessCategory": "Financial", "dataTypeHint": "date", "abbreviationsExpanded": {"ACC":"Account","OPN":"Open","DT":"Date"}, "confidence": 0.97 }` |
| **Output Artifact** | `semantic_schema_profile_{dataset}.json` — enriched field meanings and business categories; feeds Step 6 abbreviation matching |

---

### Phase 3 — Mapping & AI

#### Step 6 — DeterministicMapping

| Attribute | Detail |
|---|---|
| **Service** | `DeterministicMappingService` |
| **Input** | All `source_schema_profile_{id}.json` + `target_metadata_profile.json` + `dataset_manifest.json` (for historical CSV) + `semantic_schema_profile_{id}.json` (AI-expanded abbreviations) |
| **Processing** | **Priority 1 — Historical:** Reads `HISTORICAL_MAPPINGS` CSV files (`SOURCE_FIELD, TARGET_FIELD, TRANSFORMATION, CONFIDENCE, NOTES`) and applies them directly. **Priority 2 — Deterministic:** For remaining unmapped targets: exact match (1.0) → normalized match (0.98) → abbreviation expansion (0.92) → partial match (0.80) → Jaccard fuzzy (0.60+). Unmatched → `UNRESOLVED`. |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `mapping_candidates.json` — `MappingCandidate[]` with `SourceField`, `TargetField`, `MatchType`, `Confidence`, `Status` (`AUTO_MATCHED` / `MANUAL_REVIEW_REQUIRED` / `UNRESOLVED`) |

#### Step 7 — AISemanticMapping

| Attribute | Detail |
|---|---|
| **Service** | `AIMappingInferenceService` → `PythonAIInferenceService` |
| **Input** | `mapping_candidates.json` (only `MANUAL_REVIEW_REQUIRED`, `UNRESOLVED`, or confidence < 0.85) + `target_metadata_profile.json` + `semantic_schema_profile_{id}.json` + `source_schema_profile_{id}.json` |
| **Processing** | Checks in-memory cache first. For each low-confidence candidate, calls Python AI service with full source metadata and all target candidates. Caches results with confidence ≥ 0.90. |
| **AI Call** | ✅ `POST /api/semantic-mapping` |
| **AI Input** | `{ "jobId": "JOB_...", "sourceField": "LOAN_AMT", "targetFieldCandidates": ["LOAN_AMOUNT", "PRINCIPAL", ...], "sourceMetadata": { "datatype": "decimal", "description": "...", "nullable": true, "sample_values": ["5000.00","12500.00"] }, "requestId": "map_LOAN_AMT_DS1", "workflowStep": "AISemanticMapping" }` |
| **AI Output** | `{ "jobId": "JOB_...", "mapping": "LOAN_AMOUNT", "confidence": 0.93, "confidenceLevel": "HIGH", "reasoning": "LOAN_AMT is a common abbreviation of LOAN_AMOUNT", "alternatives": [{"field":"PRINCIPAL","confidence":0.55}], "promptVersion": "v1", "cached": false }` |
| **Output Artifact** | `ai_mapping_response.json` — `AIMappingEntry[]` with `SourceField`, `TargetField`, `TransformationRule`, `Confidence`, `ConfidenceLevel`, `Reasoning` |

#### Step 8 — RelationshipResolution

| Attribute | Detail |
|---|---|
| **Service** | `RelationshipResolutionService` |
| **Input** | All `source_schema_profile_{id}.json` files |
| **Processing** | When multiple source datasets exist: finds common fields between them (e.g., `CUSTOMER_ID`, `LOAN_ID`) and classifies them as `ONE_TO_ONE`, `ONE_TO_MANY`, or `MANY_TO_MANY` join keys |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `canonical_mapping_config.json` — `DatasetRelationship[]` specifying which field joins which dataset |

#### Step 9 — CanonicalDataModelConstruction

| Attribute | Detail |
|---|---|
| **Service** | `CanonicalDataModelBuilderService` |
| **Input** | `dataset_manifest.json` + `canonical_mapping_config.json` + source CSV files on disk |
| **Processing** | Single source: loads CSV rows as canonical records keyed as `{datasetId}.{fieldName}`. Multiple sources: joins datasets on the relationships from Step 8 |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `canonical_records.json` — `CanonicalRecord[]`: `{ rowIndex, fields: { "DS1.EMP_ID": "E001", "DS1.SALARY": "50000", ... }, sourceDatasets: ["DS1"] }` |

#### Step 10 — MappingConsolidation

| Attribute | Detail |
|---|---|
| **Service** | `MappingConsolidationService` |
| **Input** | `mapping_candidates.json` + `ai_mapping_response.json` + `target_metadata_profile.json` |
| **Processing** | Merges per target field in priority order: **1.** Hardcoded value → **2.** Deterministic ≥ 0.90 → **3.** AI ≥ 0.70 → **4.** Deterministic < 0.90 (flagged `MANUAL_REVIEW`) → **5.** `UNRESOLVED`. Builds transformation pipeline from target field format/length rules. |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `final_mapping_config.json` — `FinalMapping[]` with `SourceField`, `TargetField`, `Status`, `Confidence`, `Transformations[]`. **This is the editable Mappings page in the UI.** |

---

### Phase 4 — Transformation & Output

#### Step 11 — TransformationExecution

| Attribute | Detail |
|---|---|
| **Service** | `TransformationEngineService` |
| **Input** | `canonical_records.json` + `final_mapping_config.json` + `target_metadata_profile.json` |
| **Processing** | For every row × every target field: resolves source value from canonical record, runs it through the `Transformations[]` pipeline (date format, fixed-width pad, decimal format, hardcoded, UNRESOLVED → empty string) |
| **AI Call** | ⚠️ `POST /api/rule-inference` — integration pending for ambiguous free-text transformation rules |
| **AI Input** | `{ "rule": "Date format from MMDDYYYY to ISO 8601", "fieldContext": "BIRTH_DATE", "jobId": "JOB_...", "workflowStep": "TransformationExecution" }` |
| **AI Output** | `{ "operation": "DATE_FORMAT_CONVERSION", "parameters": { "input_format": "MMDDYYYY", "output_format": "YYYY-MM-DD" }, "confidence": 0.95, "confidenceLevel": "HIGH" }` |
| **Output Artifact** | `transformation_output.json` — `List<Dictionary<string,string>>` — every row as `{ "TARGET_FIELD_1": "value", ... }` |

#### Step 12 — TargetFileGeneration

| Attribute | Detail |
|---|---|
| **Service** | `TargetFileGenerationService` |
| **Input** | `transformation_output.json` + `target_metadata_profile.json` |
| **Processing** | Writes each row as a fixed-width `.dat` file, ordering fields by `ColumnOrder`, padding strings to `FieldLength` |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `target_output.dat` — mainframe-ready fixed-width output file, downloadable from the Workflow Details page |

---

### Phase 5 — Reconciliation & Reporting

#### Step 13 — ReconciliationValidation

| Attribute | Detail |
|---|---|
| **Service** | `ReconciliationService` |
| **Input** | `canonical_records.json` (source truth) + `transformation_output.json` + `target_metadata_profile.json` |
| **Processing** | Row count check; per-field validation against format/datatype/required/pattern rules; identifies mismatches with reason (null violation, pattern mismatch, length overflow) |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | `reconciliation_result.json` — `{ TotalSourceRecords, TotalTargetRecords, MatchedCount, MismatchCount, SuccessRate%, Mismatches[], ValidationErrors[] }` |

#### Step 14 — ReportingAuditGeneration

| Attribute | Detail |
|---|---|
| **Service** | `ReportGenerationService` |
| **Input** | All artifacts from Steps 1–13 |
| **Processing** | Assembles audit log, AI inference audit trail, reconciliation summary into UI report pages |
| **AI Call** | ❌ None |
| **AI Input** | — |
| **AI Output** | — |
| **Output Artifact** | Report pages: Reconciliation Report, Mapping Report, AI Audit log, Error Log |

---

### Python AI Service Integration Summary

| Step | Step Name | Python Endpoint | Status |
|---|---|---|---|
| 5 | SemanticSchemaEnrichment | `POST /api/semantic-enrichment` | ✅ Fully integrated — `schema_enrichment_v1` prompt, RAG, cache; enriched meanings flow into Steps 6 & 7 |
| 7 | AISemanticMapping | `POST /api/semantic-mapping` | ✅ Fully integrated |
| 11 | TransformationExecution | `POST /api/rule-inference` | ⚠️ Integration pending |

Python service runs at `http://localhost:8000`. Swagger UI: `http://localhost:8000/docs`.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 or VS Code with C# extension

## Setup & Run

```bash
cd DataReconciliation

# Restore packages
dotnet restore

# Run (database is auto-created on startup)
dotnet run
```

Open browser at `https://localhost:5001` or `http://localhost:5000`.

## Configuration

Edit `appsettings.json`:

```json
{
  "WorkflowStorage": {
    "BasePath": "workflow"          // Where artifacts are stored
  },
  "AI": {
    "ApiKey": "sk-...",             // Your OpenAI API key
    "Model": "gpt-3.5-turbo",
    "ConfidenceThreshold": {
      "AutoAccept": 0.90,
      "Warning": 0.70
    }
  }
}
```

> **Note:** AI is optional. If no API key is configured, the system operates deterministically.

## AI Governance

| Confidence | Action |
|-----------|--------|
| > 0.90 | Auto Accept |
| 0.70 – 0.90 | Warning (manual review recommended) |
| < 0.70 | Manual Review Required |

## Supported Transformations

- Date Formatting (YYYY/MM/DD, YYYYMMDD, etc.)
- Decimal Formatting
- Masking
- Value Mapping (lookup tables)
- Hardcoded Values
- Fixed-Width Formatting
- Datatype Conversion
- Default Value Assignment
- Uppercase / Trim

## Workflow Artifacts

All artifacts are persisted under `/workflow/{jobId}/artifacts/` and support:
- **Versioning** – version field on each artifact
- **Replayability** – workflows can replay from any step
- **Auditability** – full audit trail in database
- **Traceability** – CorrelationId on every log entry

## Database Tables

1. `WorkflowJobs` – Job metadata
2. `WorkflowStepExecutions` – Per-step execution tracking
3. `WorkflowArtifacts` – Artifact registry
4. `AIInferenceAudits` – AI call audit trail
5. `ErrorAuditLogs` – Error tracking

## Design Patterns Used

- **Orchestrator Pattern** – WorkflowOrchestratorService
- **Pipeline Pattern** – 14-step sequential workflow
- **Strategy Pattern** – TransformationStrategies (10 strategies)
- **Factory Pattern** – TransformationStrategyFactory
- **Repository Pattern** – 5 repositories with interfaces
- **Adapter Pattern** – IAIInferenceService abstraction
- **DTO Contract Pattern** – All API contracts via DTOs
