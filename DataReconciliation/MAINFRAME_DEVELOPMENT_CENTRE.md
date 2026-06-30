# Mainframe Development Centre

## 1. Overview

The **Mainframe Development Centre** is a job-based workspace in the DataReconciliation application. It combines deterministic generation and AI-assisted guidance into one page.

It provides three functional areas:

1. **Mainframe Artifacts**
2. **Mainframe Assets**
3. **AI Development Agent**

---

## 2. Purpose and Scope

The Mainframe Development Centre is designed to:

- Generate mainframe-ready artifacts from workflow job data.
- Produce starter COBOL/JCL assets for implementation.
- Provide AI-powered developer guidance for explanation, review, enhancement, and documentation.
- Keep all operations aligned to a single **Job ID**.

---

## 3. Shared Job Context

All three tabs use a shared **Job ID**.

### 3.1 Behavior

- A job entered in one tab is reused in other tabs.
- The page shows an **Active Job** indicator.
- Generated output and downloads are tied to that same job.

### 3.2 Why this matters

This avoids cross-job confusion and ensures artifacts, assets, and AI analysis all reference the same workflow state.

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
- **COBOL skeleton** (`.cbl`)
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

---

## 6. Tab 3 — AI Development Agent

This tab provides AI-generated development guidance using job artifacts and metadata.

### 6.1 Input

- **Job ID** (required)

### 6.2 Available Actions

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

### 6.3 Output Structure

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

### 6.4 Export/Utility Actions

- Copy response
- Export as `.doc`
- Print / Save as PDF

### 6.5 Runtime Dependency

AI actions require the Python AI service to be available. If unavailable, the UI returns a clear error message.

---

## 7. End-to-End Usage Flow

1. Open Mainframe Development Centre.
2. Set a valid workflow **Job ID**.
3. Run **Mainframe Artifacts** generation.
4. Optionally run **Mainframe Assets** generation.
5. Use **AI Development Agent** for explanation/review/enhancement.
6. Download outputs and complete developer validation.

---

## 8. Error Handling and Validation

The module includes explicit validation and failure messaging.

### 8.1 Common Validations

- Missing `Job ID`
- Missing output files during download
- AI service unavailable
- AI response missing or null

### 8.2 Error Surface

- Inline UI alert banners
- JSON error responses for AI action calls
- Logged server-side exceptions with job/agent context

---

## 9. Governance and Production Readiness

- Deterministic outputs still require business validation.
- AI outputs are advisory and must be reviewed.
- Generated COBOL/JCL is not production-ready by default.

Recommended review checklist:

- Field lengths, PIC, and alignment
- Transformation and mapping assumptions
- Error handling and reject logic
- Batch scheduling and dataset controls in JCL

---

## 10. Related Implementation Points

- UI view: `Views/Mainframe/Index.cshtml`
- Controller endpoints: `Controllers/MainframeController.cs`
  - `Generate`
  - `Download`
  - `GenerateAssets`
  - `DownloadAsset`
  - `RunAgentAction`

---

## 11. Summary

The Mainframe Development Centre is the single operational hub for:

- deterministic artifact generation,
- starter mainframe asset generation, and
- AI-assisted mainframe development guidance,

all under one consistent job context for better traceability and faster implementation.