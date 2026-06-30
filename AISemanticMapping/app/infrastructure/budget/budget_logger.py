"""
Budget logger — records every LLM API call with token usage and estimated cost.
Writes to python_logs/budget_log.jsonl (one JSON object per line).
"""
import json
from datetime import datetime, timezone
from pathlib import Path

# Approximate pricing per 1M tokens: (input_usd, output_usd)
# Source: provider pricing pages as of June 2026
_PRICING: dict[str, tuple[float, float]] = {
    "claude-haiku-4-5":      (0.25,   1.25),
    "claude-haiku":          (0.25,   1.25),
    "claude-sonnet-4-6":     (3.00,  15.00),
    "claude-sonnet":         (3.00,  15.00),
    "claude-opus-4-8":      (15.00,  75.00),
    "claude-opus":          (15.00,  75.00),
    "gpt-4o-mini":           (0.15,   0.60),
    "gpt-4o":                (2.50,  10.00),
    "gpt-4-turbo":          (10.00,  30.00),
    "gpt-3.5-turbo":         (0.50,   1.50),
    "gemini-2.0-flash":      (0.10,   0.40),
    "gemini-1.5-flash":      (0.075,  0.30),
    "gemini-1.5-pro":        (1.25,   5.00),
    "mistral-small-latest":  (0.10,   0.30),
    "mistral-medium-latest": (0.40,   1.20),
    "mistral-large-latest":  (2.00,   6.00),
    "mistral-small":         (0.10,   0.30),
    "mistral-large":         (2.00,   6.00),
}

_LOG_DIR = Path("python_logs")
_LOG_FILE = _LOG_DIR / "budget_log.jsonl"


def _estimate_cost(model_name: str, prompt_tokens: int, completion_tokens: int, provider_name: str) -> float:
    if provider_name == "ollama":
        return 0.0
    model_lower = model_name.lower()
    for key, (in_cost, out_cost) in _PRICING.items():
        if key in model_lower:
            return (prompt_tokens * in_cost + completion_tokens * out_cost) / 1_000_000
    # Unknown model: conservative fallback at $1/1M tokens
    return (prompt_tokens + completion_tokens) / 1_000_000


def record(
    provider_name: str,
    model_name: str,
    task: str,
    prompt_tokens: int,
    completion_tokens: int,
    total_tokens: int,
    latency_ms: float,
) -> None:
    """Append one LLM call record to the JSONL budget log. Never raises."""
    try:
        _LOG_DIR.mkdir(parents=True, exist_ok=True)
        entry = {
            "ts": datetime.now(timezone.utc).isoformat(),
            "provider": provider_name,
            "model": model_name,
            "task": task,
            "prompt_tokens": prompt_tokens,
            "completion_tokens": completion_tokens,
            "total_tokens": total_tokens,
            "cost_usd": round(
                _estimate_cost(model_name, prompt_tokens, completion_tokens, provider_name), 8
            ),
            "latency_ms": round(latency_ms, 1),
        }
        with open(_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")
    except Exception:
        pass
