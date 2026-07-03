"""
RagBuilderService — reads files from knowledge_base/ and indexes them into ChromaDB.

Folder layout (relative to AISemanticMapping/):
  knowledge_base/
    glossary/           *.csv  — TERM, EXPANSION, DEFINITION
    field_descriptions/ *.csv  — FIELD_NAME, DESCRIPTION, DATA_TYPE, EXAMPLE
    business_rules/     *.txt  — one rule per line
    historical_mappings/ *.csv — SOURCE_FIELD, TARGET_FIELD, TRANSFORMATION, CONFIDENCE, NOTES
    mainframe_patterns/ *.txt  — one pattern per line
    onboarding/         *.txt  — free-text project context (paragraphs separated by blank lines)

Call POST /api/rag/build to index all files.
Call GET  /api/rag/status to see document counts per collection.
"""
import csv
from pathlib import Path
from typing import Any, Dict, List, Tuple

from loguru import logger

from app.application.dto.vector_store_dto import VectorIndexRequestDTO
from app.application.services.vector_store_service import VectorStoreService
from app.infrastructure.vectorstore.chromadb_store import ChromaDBStore
from app.shared.constants.app_constants import CollectionNames, LogCategories

# Root of knowledge_base/ — four parents up from this file gets to AISemanticMapping/
_KNOWLEDGE_DIR = Path(__file__).resolve().parent.parent.parent.parent / "knowledge_base"


class RagBuilderService:
    """
    Reads knowledge files from knowledge_base/ and indexes them into ChromaDB.
    Each subfolder maps to one ChromaDB collection.
    """

    def __init__(self) -> None:
        self._svc = VectorStoreService()
        self._store = ChromaDBStore()

    async def build_all(self, job_id: str = "rag_build") -> Dict[str, Any]:
        """Index all knowledge folders. Returns per-collection document counts."""
        logger.info(
            f"[RagBuilderService] Starting full RAG build | knowledge_dir={_KNOWLEDGE_DIR} | job_id={job_id}",
            category=LogCategories.RAG,
        )
        results: Dict[str, Any] = {}

        indexers = [
            ("glossary",            self._load_glossary,           CollectionNames.SEMANTIC_GLOSSARY),
            ("field_descriptions",  self._load_field_descriptions, CollectionNames.FIELD_DESCRIPTIONS),
            ("business_rules",      self._load_business_rules,     CollectionNames.BUSINESS_RULES),
            ("historical_mappings", self._load_historical_mappings,CollectionNames.HISTORICAL_MAPPINGS),
            ("mainframe_patterns",  self._load_mainframe_patterns, CollectionNames.MAINFRAME_PATTERNS),
            ("onboarding",          self._load_onboarding,         CollectionNames.ONBOARDING_METADATA),
        ]

        for folder_name, loader, collection in indexers:
            folder = _KNOWLEDGE_DIR / folder_name
            if not folder.exists():
                logger.warning(
                    f"[RagBuilderService] Folder not found — skipping | folder={folder}",
                    category=LogCategories.RAG,
                )
                results[collection] = {"indexed": 0, "skipped": True, "reason": "folder_not_found"}
                continue

            docs, ids = loader(folder)
            if not docs:
                logger.info(
                    f"[RagBuilderService] No documents found | folder={folder_name} | collection={collection}",
                    category=LogCategories.RAG,
                )
                results[collection] = {"indexed": 0, "skipped": True, "reason": "no_files"}
                continue

            count = await self._index(collection, docs, ids, job_id)
            results[collection] = {"indexed": count, "skipped": False}

        total = sum(v["indexed"] for v in results.values())
        logger.info(
            f"[RagBuilderService] Build complete | total_indexed={total} | job_id={job_id}",
            category=LogCategories.RAG,
        )
        return results

    async def get_status(self) -> Dict[str, Any]:
        """Return document count for each ChromaDB collection."""
        all_collections = [
            CollectionNames.SEMANTIC_GLOSSARY,
            CollectionNames.FIELD_DESCRIPTIONS,
            CollectionNames.BUSINESS_RULES,
            CollectionNames.HISTORICAL_MAPPINGS,
            CollectionNames.MAINFRAME_PATTERNS,
            CollectionNames.ONBOARDING_METADATA,
            CollectionNames.TARGET_SCHEMA,
        ]
        status: Dict[str, Any] = {}
        for col in all_collections:
            try:
                count = await self._store.get_collection_count(col)
                status[col] = {"count": count, "ready": count > 0}
            except Exception as exc:
                status[col] = {"count": 0, "ready": False, "error": str(exc)[:100]}
        return status

    # ── Loaders ──────────────────────────────────────────────────────────────────

    def _load_glossary(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for csv_file in sorted(folder.glob("*.csv")):
            with open(csv_file, encoding="utf-8", newline="") as f:
                for row in csv.DictReader(f):
                    term = (row.get("TERM") or "").strip()
                    expansion = (row.get("EXPANSION") or "").strip()
                    definition = (row.get("DEFINITION") or "").strip()
                    if not term:
                        continue
                    parts = [f"TERM: {term}"]
                    if expansion:
                        parts.append(f"stands for {expansion}")
                    if definition:
                        parts.append(f"— {definition}")
                    docs.append(". ".join(parts) + ".")
                    ids.append(f"glossary_{term.lower().replace(' ', '_')[:80]}")
        return docs, ids

    def _load_field_descriptions(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for csv_file in sorted(folder.glob("*.csv")):
            with open(csv_file, encoding="utf-8", newline="") as f:
                for row in csv.DictReader(f):
                    name = (row.get("FIELD_NAME") or "").strip()
                    desc = (row.get("DESCRIPTION") or "").strip()
                    dtype = (row.get("DATA_TYPE") or "").strip()
                    example = (row.get("EXAMPLE") or "").strip()
                    if not name:
                        continue
                    parts = [f"FIELD: {name}"]
                    if desc:
                        parts.append(f"Description: {desc}")
                    if dtype:
                        parts.append(f"Type: {dtype}")
                    if example:
                        parts.append(f"Example value: {example}")
                    docs.append(". ".join(parts) + ".")
                    ids.append(f"field_{name.lower().replace(' ', '_')[:80]}")
        return docs, ids

    def _load_business_rules(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for txt_file in sorted(folder.glob("*.txt")):
            lines = txt_file.read_text(encoding="utf-8").splitlines()
            for i, line in enumerate(lines):
                line = line.strip()
                if line:
                    docs.append(f"BUSINESS RULE: {line}")
                    ids.append(f"rule_{txt_file.stem}_{i}")
        return docs, ids

    def _load_historical_mappings(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for csv_file in sorted(folder.glob("*.csv")):
            with open(csv_file, encoding="utf-8", newline="") as f:
                for row in csv.DictReader(f):
                    src = (row.get("SOURCE_FIELD") or "").strip()
                    tgt = (row.get("TARGET_FIELD") or "").strip()
                    transform = (row.get("TRANSFORMATION") or "").strip()
                    confidence = (row.get("CONFIDENCE") or "").strip()
                    notes = (row.get("NOTES") or "").strip()
                    if not src or not tgt:
                        continue
                    parts = [f"SOURCE: {src} maps to TARGET: {tgt}."]
                    if transform and transform.upper() != "DIRECT":
                        parts.append(f"Transformation: {transform}.")
                    if confidence:
                        parts.append(f"Confidence: {confidence}.")
                    if notes:
                        parts.append(f"Notes: {notes}.")
                    docs.append(" ".join(parts))
                    ids.append(f"hist_{tgt.lower().replace(' ', '_')[:80]}")
        return docs, ids

    def _load_mainframe_patterns(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for txt_file in sorted(folder.glob("*.txt")):
            lines = txt_file.read_text(encoding="utf-8").splitlines()
            for i, line in enumerate(lines):
                line = line.strip()
                if line:
                    docs.append(f"MAINFRAME PATTERN: {line}")
                    ids.append(f"pattern_{txt_file.stem}_{i}")
        return docs, ids

    def _load_onboarding(self, folder: Path) -> Tuple[List[str], List[str]]:
        docs, ids = [], []
        for txt_file in sorted(folder.glob("*.txt")):
            text = txt_file.read_text(encoding="utf-8")
            paragraphs = [p.strip() for p in text.split("\n\n") if p.strip()]
            for i, para in enumerate(paragraphs):
                docs.append(f"PROJECT CONTEXT: {para}")
                ids.append(f"onboard_{txt_file.stem}_{i}")
        return docs, ids

    # ── Internal indexing ────────────────────────────────────────────────────────

    async def _index(self, collection: str, docs: List[str], ids: List[str], job_id: str) -> int:
        try:
            req = VectorIndexRequestDTO(
                collection=collection,
                documents=docs,
                ids=ids,
                jobId=job_id,
            )
            resp = await self._svc.index_documents(req)
            logger.info(
                f"[RagBuilderService] Indexed | collection={collection} | count={resp.documentsIndexed}",
                category=LogCategories.RAG,
            )
            return resp.documentsIndexed
        except Exception as exc:
            logger.error(
                f"[RagBuilderService] Indexing failed | collection={collection} | error={exc}",
                category=LogCategories.RAG,
            )
            return 0
