"""
Budget API router — LLM usage and cost analytics.
Reads python_logs/budget_log.jsonl and returns aggregated spend data.
"""
import json
from collections import defaultdict
from datetime import date, datetime
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, Query

router = APIRouter(prefix="/budget", tags=["Budget"])

_LOG_FILE = Path("python_logs") / "budget_log.jsonl"


def _load_records(from_date: Optional[date], to_date: Optional[date], provider: Optional[str]) -> list[dict]:
    if not _LOG_FILE.exists():
        return []
    records = []
    with open(_LOG_FILE, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
                ts_date = datetime.fromisoformat(rec["ts"]).date()
                if from_date and ts_date < from_date:
                    continue
                if to_date and ts_date > to_date:
                    continue
                if provider and rec.get("provider", "").lower() != provider.lower():
                    continue
                records.append(rec)
            except Exception:
                continue
    return records


@router.get("")
async def get_budget(
    from_date: Optional[str] = Query(None, alias="from", description="YYYY-MM-DD start date"),
    to_date: Optional[str] = Query(None, alias="to", description="YYYY-MM-DD end date"),
    provider: Optional[str] = Query(None, description="Filter by provider name"),
):
    from_d = date.fromisoformat(from_date) if from_date else None
    to_d   = date.fromisoformat(to_date)   if to_date   else None

    records = _load_records(from_d, to_d, provider)

    # Aggregate by provider + model
    agg: dict[tuple, dict] = defaultdict(lambda: {
        "calls": 0, "prompt_tokens": 0, "completion_tokens": 0,
        "total_tokens": 0, "cost_usd": 0.0,
    })
    for r in records:
        k = (r.get("provider", ""), r.get("model", ""))
        g = agg[k]
        g["calls"] += 1
        g["prompt_tokens"]     += r.get("prompt_tokens", 0)
        g["completion_tokens"] += r.get("completion_tokens", 0)
        g["total_tokens"]      += r.get("total_tokens", 0)
        g["cost_usd"]          += r.get("cost_usd", 0.0)

    by_provider = [
        {
            "provider": k[0],
            "model":    k[1],
            "calls":            v["calls"],
            "prompt_tokens":    v["prompt_tokens"],
            "completion_tokens": v["completion_tokens"],
            "total_tokens":     v["total_tokens"],
            "cost_usd":         round(v["cost_usd"], 6),
        }
        for k, v in sorted(agg.items(), key=lambda x: -x[1]["cost_usd"])
    ]

    total_calls  = sum(r["calls"]      for r in by_provider)
    total_cost   = round(sum(r["cost_usd"]  for r in by_provider), 6)
    total_tokens = sum(r["total_tokens"] for r in by_provider)

    # Return the 100 most recent individual records for the detail table
    recent = sorted(records, key=lambda r: r.get("ts", ""), reverse=True)[:100]

    return {
        "total_calls":   total_calls,
        "total_cost_usd": total_cost,
        "total_tokens":  total_tokens,
        "by_provider":   by_provider,
        "records":       recent,
    }
