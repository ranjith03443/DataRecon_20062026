"""
Domain models for AI token usage audit tracking.
"""
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional
import uuid


@dataclass
class AITokenUsageAudit:
    """
    Tracks token usage and cost for every AI provider call.
    Used for governance, cost management, and audit logging.
    """
    job_id: str
    request_id: str
    workflow_step: str
    model_name: str
    provider_name: str
    prompt_tokens: int
    completion_tokens: int
    total_tokens: int
    estimated_cost_usd: float
    latency_ms: float
    correlation_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    timestamp: datetime = field(default_factory=datetime.utcnow)
    prompt_version: Optional[str] = None
    success: bool = True
    error_message: Optional[str] = None

    def to_log_dict(self) -> dict:
        return {
            "job_id": self.job_id,
            "request_id": self.request_id,
            "workflow_step": self.workflow_step,
            "model_name": self.model_name,
            "provider_name": self.provider_name,
            "prompt_tokens": self.prompt_tokens,
            "completion_tokens": self.completion_tokens,
            "total_tokens": self.total_tokens,
            "estimated_cost_usd": round(self.estimated_cost_usd, 6),
            "latency_ms": round(self.latency_ms, 2),
            "correlation_id": self.correlation_id,
            "timestamp": self.timestamp.isoformat(),
            "prompt_version": self.prompt_version,
            "success": self.success,
            "error_message": self.error_message,
        }


def estimate_cost(
    prompt_tokens: int,
    completion_tokens: int,
    model_name: str,
) -> float:
    """
    Estimate the cost of an LLM call in USD.
    Prices are approximate and must be kept updated.
    """
    # Cost per 1000 tokens in USD (input, output)
    pricing = {
        "gpt-4o": (0.005, 0.015),
        "gpt-4o-mini": (0.00015, 0.0006),
        "gpt-4-turbo": (0.01, 0.03),
        "gpt-35-turbo": (0.0015, 0.002),
        "text-embedding-3-small": (0.00002, 0.0),
        "text-embedding-3-large": (0.00013, 0.0),
    }
    # Normalize model name
    key = model_name.lower().replace("gpt-4o-mini", "gpt-4o-mini")
    input_price, output_price = pricing.get(key, (0.01, 0.03))
    cost = (prompt_tokens / 1000) * input_price + (completion_tokens / 1000) * output_price
    return cost
