# API Testing Guide — Semantic Intelligence Platform

## Base URL
```
http://localhost:8000
```

> **Tip:** Visit **http://localhost:8000/docs** for interactive Swagger UI to test all endpoints directly from the browser.

---

## Health Status Legend

| Provider | Status | Requires |
|---|---|---|
| `vectorstore/chromadb` | ✅ Works out of the box | Local ChromaDB |
| `llm/azure_openai` | ⚠️ Needs credentials | Azure OpenAI API Key + Endpoint |
| `embeddings/azure_openai` | ⚠️ Needs credentials | Azure OpenAI API Key + Endpoint |

> To fix Azure OpenAI `healthy: false`, configure your `.env` file with the API key, endpoint, and deployment names.

---

## Endpoints

### 1. Health Check ✅
**No Azure OpenAI required**

```
GET http://localhost:8000/api/health
```

**Sample Response:**
```json
{
  "status": "DEGRADED",
  "version": "v1",
  "environment": "dev",
  "providers": [
    { "name": "llm/azure_openai", "healthy": false, "details": "model=gpt-4o-mini" },
    { "name": "embeddings/azure_openai", "healthy": false, "details": "model=text-embedding-3-small" },
    { "name": "vectorstore/chromadb", "healthy": true, "details": null }
  ],
  "cacheStatus": "HEALTHY",
  "ragEnabled": true,
  "semanticCacheEnabled": true,
  "uptimeSeconds": 148.17
}
```

---

### 2. Index Documents into Vector Store ✅
**No Azure OpenAI required**

```
POST http://localhost:8000/api/vector-store/index
Content-Type: application/json
```

**Request Body:**
```json
{
  "collection": "test_mappings",
  "documents": [
    "CustomerID maps to client_id — unique customer identifier",
    "OrderDate maps to transaction_date — date when order was placed",
    "TotalAmount maps to invoice_total — total monetary value of the order"
  ],
  "metadatas": [
    { "source": "CustomerID", "target": "client_id" },
    { "source": "OrderDate", "target": "transaction_date" },
    { "source": "TotalAmount", "target": "invoice_total" }
  ],
  "ids": ["doc-1", "doc-2", "doc-3"],
  "jobId": "job-001"
}
```

**Sample Response:**
```json
{
  "collection": "test_mappings",
  "documentsIndexed": 3,
  "success": true,
  "requestId": null
}
```

---

### 3. Search Vector Store ✅
**No Azure OpenAI required**

```
POST http://localhost:8000/api/vector-store/search
Content-Type: application/json
```

**Request Body:**
```json
{
  "collection": "test_mappings",
  "query": "customer identifier field",
  "topK": 3,
  "jobId": "job-001"
}
```

**Sample Response:**
```json
{
  "collection": "test_mappings",
  "query": "customer identifier field",
  "results": [
    {
      "documentId": "doc-1",
      "document": "CustomerID maps to client_id — unique customer identifier",
      "metadata": { "source": "CustomerID", "target": "client_id" },
      "score": 0.95
    }
  ],
  "totalResults": 1,
  "requestId": null
}
```

---

### 4. Semantic Mapping ⚠️
**Requires Azure OpenAI**

```
POST http://localhost:8000/api/semantic-mapping
Content-Type: application/json
```

**Request Body:**
```json
{
  "jobId": "job-001",
  "sourceField": "CustomerID",
  "targetFieldCandidates": ["client_id", "user_id", "account_number", "member_code"],
  "sourceMetadata": {
    "datatype": "string",
    "description": "Unique identifier for a customer",
    "nullable": false,
    "sample_values": ["CUST001", "CUST002", "CUST003"]
  },
  "workflowStep": "field_mapping"
}
```

**Sample Response:**
```json
{
  "jobId": "job-001",
  "mapping": "client_id",
  "confidence": 0.95,
  "confidenceLevel": "HIGH",
  "reasoning": "CustomerID and client_id both represent a unique customer identifier.",
  "alternatives": [
    { "field": "user_id", "confidence": 0.72 },
    { "field": "account_number", "confidence": 0.45 }
  ],
  "promptVersion": "v1",
  "cached": false,
  "requestId": null
}
```

---

### 5. Rule Inference ⚠️
**Requires Azure OpenAI**

```
POST http://localhost:8000/api/rule-inference
Content-Type: application/json
```

**Request Body:**
```json
{
  "rule": "Convert the date from MM/DD/YYYY format to YYYYMMDD",
  "fieldContext": "OrderDate",
  "jobId": "job-001",
  "workflowStep": "transformation"
}
```

**Sample Response:**
```json
{
  "operation": "DATE_FORMAT_CONVERSION",
  "format": "YYYYMMDD",
  "parameters": {
    "input_format": "MM/DD/YYYY",
    "output_format": "YYYYMMDD"
  },
  "description": "Convert date string from MM/DD/YYYY to YYYYMMDD format",
  "validationRules": ["Input must match MM/DD/YYYY pattern", "Output must be 8 digits"],
  "confidence": 0.97,
  "confidenceLevel": "HIGH",
  "reasoning": "The rule explicitly states source and target date formats.",
  "promptVersion": "v1",
  "cached": false,
  "requestId": null
}
```

---

## Route Prefix Summary

| Endpoint | Method | Path |
|---|---|---|
| Health Check | GET | `/api/health` |
| Index Documents | POST | `/api/vector-store/index` |
| Search Documents | POST | `/api/vector-store/search` |
| Semantic Mapping | POST | `/api/semantic-mapping` |
| Rule Inference | POST | `/api/rule-inference` |
| Schema Enrichment | POST | `/api/semantic-enrichment` |
| Models Info | GET | `/api/models` |

---

## Quick Start (Test without Azure OpenAI)

1. Start the server: `python main.py`
2. Index some documents: `POST /api/vector-store/index`
3. Search indexed documents: `POST /api/vector-store/search`
4. Open Swagger UI: [http://localhost:8000/docs](http://localhost:8000/docs)
