# Mainframe Development Centre

## 1. Overview

The **Mainframe Development Centre** is a job-based workspace in the DataReconciliation application. It combines deterministic generation and AI-assisted guidance into one page.

It provides four functional areas:

1. **Mainframe Artifacts** — copybook, specification, summary manifest
2. **Mainframe Assets** — COBOL skeleton, JCL skeleton, sample records
3. **Recon Program** — COBOL reconciliation verification program and JCL
4. **AI Development Agent** — explanation, review, enhancement, documentation

---

## 2. Purpose and Scope

The Mainframe Development Centre is designed to:

- Generate mainframe-ready artifacts from workflow job data.
- Produce starter COBOL/JCL assets for implementation.
- Generate a standalone COBOL reconciliation verification program that mirrors the workflow's reconciliation checks on the mainframe.
- Provide AI-powered developer guidance for explanation, review, enhancement, and documentation.
- Keep all operations aligned to a single **Job ID**.

---

## 3. Shared Job Context

All four tabs use a shared **Job ID**.

### 3.1 Behavior

- A job entered in one tab is reused in other tabs.
- The page shows an **Active Job** indicator.
- Generated output and downloads are tied to that same job.

### 3.2 Why this matters

This avoids cross-job confusion and ensures artifacts, assets, recon programs, and AI analysis all reference the same workflow state.

---

## 4. Tab 1 — Mainframe Artifacts

This tab is deterministic and does not call AI.

### 4.1 Inputs

- **Job ID** (required)
- **Record name**
- **Application name**
- **Include comments in artifacts** (toggle)

### 4.2 What it Generates

- **Copybook** (`.cpy`)
- **Specification** (`.txt`)
- **Summary manifest** (`.json`)

### 4.3 Output Panel

After generation, the page shows:

- Job metadata (job, record, field count, timestamp)
- Download buttons for each file
- Field-level table with:
  - Field name
  - Data type
  - Length
  - PIC clause
  - Source field
  - Justification

### 4.4 Processing Characteristics

- Driven by target schema and mapping context for the selected job.
- Deterministic output: same input context produces same artifacts.
- Suitable as baseline artifacts before writing production COBOL.

### 4.5 Output Folder

```
{job-folder}/reports/mainframe/
```

---

## 5. Tab 2 — Mainframe Assets

This tab generates implementation starter assets (still deterministic, but intended for developer refinement).

### 5.1 Inputs

- **Job ID** (required)
- **Record Name**
- **Application Name**
- **Program Name** (max 8)
- **Job Name** (max 8)
- **Sample Record Count** (e.g., 10/20/50/100)
- Output toggles:
  - Generate Copybook
  - Generate COBOL Skeleton
  - Generate JCL Skeleton
  - Generate Technical Spec
  - Generate Sample Records

### 5.2 What it Generates

- **Copybook** (`.cpy`)
- **COBOL skeleton** (`.cbl`) — load/transform program that reads source and writes target
- **JCL skeleton** (`.jcl`)
- **Technical specification** (`.txt`)
- **Sample records** (`.dat`)

### 5.3 Output Panel

After generation, the page shows:

- Job/record/program metadata
- Asset download buttons
- Field detail table with:
  - Source and COBOL field names
  - Data type and length
  - Start position
  - PIC clause
  - Required indicator

### 5.4 Important Note

COBOL/JCL outputs are **starter skeletons** and require developer review before production use.

### 5.5 Output Folder

```
{job-folder}/reports/mainframe-assets/
```

---

## 6. Tab 3 — Recon Program

This tab generates a **COBOL reconciliation verification program** and its JCL. It is distinct from the Assets tab COBOL skeleton: rather than transforming data, it reads the already-generated target `.dat` file and independently verifies that counts and sums match the expected values computed by the workflow.

### 6.1 Concept

The workflow's `ReconciliationService` (Step 13) computes expected counts and sums from the source data and stores them in `reconciliation_result.json`. The Recon Program embeds those expected values as COBOL literals, reads the target `.dat` file on the mainframe, and compares:

| Check | Type | Expected Source |
|---|---|---|
| Total record count | COUNT | `reconciliation_result.TotalTargetRecords` |
| SUM fields (e.g., TOTALAMOUNT) | SUM | `reconciliation_result.FieldTotals[field]` |
| COUNT fields (e.g., CUSTOMERNBR) | COUNT | `reconciliation_result.TargetFieldCounts[field]` |

The fields and their types (SUM or COUNT) are read from `reconciliation_config.json`, which the user configures in the Mapping Workbench before running Step 13.

### 6.2 Inputs Required

| Artifact | Purpose |
|---|---|
| `reconciliation_config.json` | Which fields to check and whether each is SUM or COUNT |
| `reconciliation_result.json` | Expected values (totals, counts, record count) to embed as COBOL literals |
| `target_metadata_profile.json` | Fixed-width field positions and lengths for REFERENCE MODIFICATION |
| `final_mapping_config.json` | Source field names shown in the spec and checks table |

Steps 10 (MappingConsolidation), 12 (TargetFileGeneration), and 13 (Reconciliation) must have completed before generating a Recon Program.

### 6.3 Form Inputs

- **Job ID** (required)
- **Program Name** (max 8 chars, default `RECONPGM`)
- **Job Name** (max 8 chars, default `RECONJOB`)
- Output toggles:
  - Generate COBOL Program
  - Generate JCL
  - Generate Specification

### 6.4 What it Generates

**COBOL program** (`.cbl`) — marked DEVELOPER REVIEW REQUIRED:
- FILE SECTION with `FD TARGET-FILE RECFM=FB LRECL={totalRecordLength}` and `FD REPT-FILE`
- WORKING-STORAGE with:
  - Expected value literals from `reconciliation_result.json`
  - Accumulators for SUM fields (`PIC 9(15)V99 COMP-3`)
  - Counters for COUNT fields (`PIC 9(9) COMP-3`)
  - Field extract buffers using REFERENCE MODIFICATION positions from the target profile
- PROCEDURE DIVISION:
  - `0000-MAIN` → open files → loop → terminate
  - `1000-INIT` → open and read first record
  - `2000-PROCESS` → extract fields via reference modification, accumulate SUM fields with `FUNCTION NUMVAL`, count non-blank for COUNT fields
  - `9100-CHK-RECS` through `91xx-CHK-{field}` → one PASS/FAIL paragraph per check
  - `9900-WRITE-SUMMARY` → overall PASS/FAIL with totals

**JCL** (`.jcl`) — skeleton:
- Runs the COBOL program
- `TARGET` DD pointing to the target `.dat` file (`RECFM=FB LRECL={totalRecordLength}`)
- `RPTFILE` DD for the output reconciliation report
- Comments instructing developer to fill in dataset names

**Specification** (`.txt`):
- Purpose, target file layout, and LRECL
- Full checks table: check number, field, type, source field, expected value
- Field extraction table: COBOL variable, start position, length, PIC clause
- Step-by-step developer instructions (compile, update JCL, submit, read report)

### 6.5 Output Panel

After generation:
- Summary cards: number of checks, expected record count, LRECL
- Download buttons for COBOL, JCL, and spec files
- Checks table showing each reconciliation check embedded in the COBOL program

### 6.6 Output Folder

```
{job-folder}/reports/mainframe-recon/
├── {ProgramName}_recon_{timestamp}.cbl
├── {JobName}_recon_{timestamp}.jcl
└── recon_program_spec_{timestamp}.txt
```

### 6.7 Processing Characteristics

- 100% deterministic — no AI involved.
- Uses exactly the same field-width calculation as `TargetFileGenerationService` to ensure correct REFERENCE MODIFICATION positions.
- COBOL paragraph names generated as `9100-CHK-RECS`, `9101-CHK-{field1}`, `9102-CHK-{field2}`, etc.

---

## 7. Tab 4 — AI Development Agent

This tab provides AI-generated development guidance using job artifacts and metadata.

### 7.1 Input

- **Job ID** (required)

### 7.2 Available Actions

#### Understanding

- **Explain COBOL**
- **Developer Docs**

#### Quality & Review

- **Review COBOL**
- **JCL Improvements**
- **Optimisation**

#### Code Generation

- **Enhance COBOL**
- **Validation Logic**
- **Error Handling**

### 7.3 Output Structure

The response section is tabbed and includes:

1. **Summary** (business explanation)
2. **Generated Code** (if provided)
3. **Recommendations** (severity-based)
4. **Warnings**

Additional metadata shown:

- Agent name
- Confidence and confidence level
- Response time
- Governance warning

### 7.4 Export/Utility Actions

- Copy response
- Export as `.doc`
- Print / Save as PDF

### 7.5 Runtime Dependency

AI actions require the Python AI service to be available. If unavailable, the UI returns a clear error message.

---

## 8. End-to-End Usage Flow

1. Open Mainframe Development Centre.
2. Set a valid workflow **Job ID** (one with Steps 10–13 completed for full functionality).
3. Run **Mainframe Artifacts** generation (Tab 1).
4. Optionally run **Mainframe Assets** generation (Tab 2) for COBOL/JCL load skeletons.
5. Run **Recon Program** generation (Tab 3) to produce the mainframe-side verification program.
6. Use **AI Development Agent** (Tab 4) for explanation/review/enhancement of any generated code.
7. Download outputs and complete developer validation.

---

## 9. Error Handling and Validation

The module includes explicit validation and failure messaging.

### 9.1 Common Validations

- Missing `Job ID`
- Missing output files during download
- Required artifacts missing (reconciliation_config or reconciliation_result not yet generated)
- AI service unavailable
- AI response missing or null

### 9.2 Error Surface

- Inline UI alert banners (separate per tab: `Success`, `AssetSuccess`, `ReconSuccess`, `Error`, `AssetError`, `ReconError`)
- JSON error responses for AI action calls
- Logged server-side exceptions with job/agent context

---

## 10. Governance and Production Readiness

- Deterministic outputs still require business validation.
- AI outputs are advisory and must be reviewed.
- Generated COBOL/JCL is not production-ready by default.

Recommended review checklist:

- Field lengths, PIC, and alignment
- Transformation and mapping assumptions
- REFERENCE MODIFICATION positions (verify against actual target record layout)
- Expected values in the Recon Program (verify against source system reconciliation)
- Error handling and reject logic
- Batch scheduling and dataset controls in JCL
- Dataset names in all JCL DD statements

---

## 11. Related Implementation Points

- UI view: `Views/Mainframe/Index.cshtml`
- Controller endpoints: `Controllers/MainframeController.cs`
  - `Generate` — Tab 1 artifacts
  - `Download` — Tab 1 downloads
  - `GenerateAssets` — Tab 2 assets
  - `DownloadAsset` — Tab 2 downloads
  - `GenerateReconProgram` — Tab 3 recon program
  - `DownloadReconProgram` — Tab 3 downloads
  - `RunAgentAction` — Tab 4 AI agent (AJAX)
- Services:
  - `MainframeArtifactGenerationService` — Tab 1
  - `MainframeAssetGenerationService` — Tab 2
  - `ReconProgramGenerationService` — Tab 3
  - `PythonMainframeAiAgentService` — Tab 4
- Interfaces: `IMainframeArtifactGenerationService`, `IMainframeAssetGenerationService`, `IReconProgramGenerationService`, `IMainframeAiAgentService`
- DTOs: `MainframeArtifactPageDto`, `MainframeAssetPageDto`, `ReconProgramPageDto`, `ReconProgramRequest`, `ReconProgramResultDto`, `ReconCheckDto`

---

## 12. Summary

The Mainframe Development Centre is the single operational hub for:

- deterministic artifact generation (Tab 1),
- starter mainframe asset generation including COBOL load skeleton (Tab 2),
- COBOL reconciliation verification program generation for mainframe-side recon (Tab 3), and
- AI-assisted mainframe development guidance (Tab 4),

all under one consistent job context for better traceability and faster implementation.
