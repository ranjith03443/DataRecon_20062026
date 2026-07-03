namespace DataReconciliation.Domain.Models
{
    // ─── Evaluation Summary (persisted as evaluation_summary.json) ─────────────
    public class EvaluationSummary
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string ReviewerName { get; set; } = string.Empty;

        // ── KPI Metrics ────────────────────────────────────────────────────────
        public double MappingCoveragePercent { get; set; }
        public double AiAcceptanceRatePercent { get; set; }
        public double HumanOverrideRatePercent { get; set; }
        public double UnresolvedFieldRatePercent { get; set; }
        public double TransformationAccuracyPercent { get; set; }
        public double ArtifactGenerationSuccessRatePercent { get; set; }

        /// <summary>Coverage × Acceptance Rate — headline readiness indicator (0–100).</summary>
        public double MappingReadinessScore { get; set; }

        // ── Confidence Calibration ─────────────────────────────────────────────
        public ConfidenceCalibrationMetrics ConfidenceCalibration { get; set; } = new();

        // ── Field Counts ───────────────────────────────────────────────────────
        public int TotalTargetFields { get; set; }
        public int MappedFields { get; set; }
        public int UnresolvedFields { get; set; }
        public List<string> UnresolvedFieldNames { get; set; } = new();

        // ── Human Validation Counts (populated from Phase 2 audit log) ─────────
        public int TotalAiSuggestions { get; set; }
        public int AcceptedWithoutChange { get; set; }
        public int HumanModified { get; set; }
        public int HumanRejected { get; set; }

        // ── Transformation Metrics ─────────────────────────────────────────────
        public int TransformationsTotalCount { get; set; }
        public int TransformationsReviewedCount { get; set; }
        public int TransformationsOverriddenCount { get; set; }

        // ── Mapping Source Breakdown ───────────────────────────────────────────
        public Dictionary<string, int> MappingSourceBreakdown { get; set; } = new();

        // ── Gap Analysis ───────────────────────────────────────────────────────
        public GapAnalysis GapAnalysis { get; set; } = new();

        // ── Processing ─────────────────────────────────────────────────────────
        public ProcessingMetrics Processing { get; set; } = new();
    }

    public class GapAnalysis
    {
        public int UnmappedTargetFields { get; set; }
        public List<string> UnmappedFieldNames { get; set; } = new();
        public int ManualReviewRequiredCount { get; set; }
        public List<string> ManualReviewFieldNames { get; set; } = new();
        public int UnreviewedTransformationsCount { get; set; }
        public List<string> UnreviewedTransformationFields { get; set; } = new();
    }

    public class ConfidenceCalibrationMetrics
    {
        public double HighConfidenceAcceptanceRate { get; set; }    // AI confidence >0.90
        public double MediumConfidenceAcceptanceRate { get; set; }  // 0.70–0.90
        public double LowConfidenceAcceptanceRate { get; set; }     // <0.70
        public int HighConfidenceCount { get; set; }
        public int MediumConfidenceCount { get; set; }
        public int LowConfidenceCount { get; set; }
    }

    public class ProcessingMetrics
    {
        public string? LlmProviderUsed { get; set; }
        public string? LlmModelUsed { get; set; }
        public bool FallbackTriggered { get; set; }
        public double TotalWorkflowDurationMs { get; set; }
        public int? TotalPromptTokens { get; set; }
        public int? TotalCompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public double? TotalEstimatedCostUsd { get; set; }
        public List<StepMetric> StepMetrics { get; set; } = new();
    }

    public class StepMetric
    {
        public string Step { get; set; } = string.Empty;
        public double DurationMs { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    // ─── Run History (persisted as evaluation_history.json) ────────────────────
    public class EvaluationRunHistory
    {
        public string JobId { get; set; } = string.Empty;
        public List<EvaluationRunSnapshot> Runs { get; set; } = new();
    }

    public class EvaluationRunSnapshot
    {
        public int RunIndex { get; set; }
        public DateTime ComputedAt { get; set; }
        public string ReviewerName { get; set; } = string.Empty;
        public string? LlmProvider { get; set; }
        public string? LlmModel { get; set; }

        // ── Key KPIs ───────────────────────────────────────────────────────────
        public double MappingReadinessScore { get; set; }
        public double MappingCoveragePercent { get; set; }
        public double AiAcceptanceRatePercent { get; set; }
        public double HumanOverrideRatePercent { get; set; }
        public double TransformationAccuracyPercent { get; set; }
        public double ArtifactGenerationSuccessRatePercent { get; set; }

        // ── Field counts ───────────────────────────────────────────────────────
        public int TotalTargetFields { get; set; }
        public int MappedFields { get; set; }
        public int UnresolvedFields { get; set; }

        // ── Human validation counts ────────────────────────────────────────────
        public int AcceptedWithoutChange { get; set; }
        public int HumanModified { get; set; }
        public int HumanRejected { get; set; }

        // ── Gap counts ─────────────────────────────────────────────────────────
        public int GapUnmappedCount { get; set; }
        public int GapManualReviewCount { get; set; }
        public int GapUnreviewedTransformCount { get; set; }
    }
}
