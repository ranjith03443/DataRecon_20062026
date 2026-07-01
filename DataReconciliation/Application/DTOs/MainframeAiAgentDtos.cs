namespace DataReconciliation.Application.DTOs
{
    // ── Request ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Request sent to the Mainframe Development AI Agent endpoints
    /// (POST /api/mainframe/{agent-type}).
    /// </summary>
    public class MainframeAiAgentRequest
    {
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Agent type: explain | review | enhance | validation |
        /// error_handling | documentation | jcl_improvements | optimization
        /// </summary>
        public string PromptType { get; set; } = string.Empty;

        // Artifact content — loaded from job folder by the service
        public string? CobolContent { get; set; }
        public string? JclContent { get; set; }
        public string? CopybookContent { get; set; }
        public List<Dictionary<string, object?>>? TransformationRules { get; set; }
        public Dictionary<string, object?>? ValueMappings { get; set; }
        public List<Dictionary<string, object?>>? FieldMappings { get; set; }
        public Dictionary<string, object?>? TargetSchema { get; set; }

        // Generation metadata — populated by MainframeAssetGenerationService when UseAiMode=true
        public string? ProgramName { get; set; }
        public string? RecordName { get; set; }
        public string? JobName { get; set; }
        public int? TotalRecordLength { get; set; }
        public List<Dictionary<string, object?>>? FieldDetails { get; set; }

        /// <summary>
        /// Distinct source datasets feeding this job.
        /// Each entry: { datasetId, fields: [sourceFieldName, ...], fieldCount }.
        /// Used by AI to generate one SELECT/FD per source file.
        /// </summary>
        public List<Dictionary<string, object?>>? SourceDatasets { get; set; }

        // Recon-program metadata — populated by ReconProgramGenerationService when UseAiMode=true
        public int? ExpectedRecordCount { get; set; }
        public List<Dictionary<string, object?>>? ReconChecks { get; set; }

        public string? RequestId { get; set; }
        public string? UserId { get; set; }
    }

    // ── Sub-models ────────────────────────────────────────────────────────────

    public class MainframeAiRecommendationItem
    {
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = "INFO";
        public string Message { get; set; } = string.Empty;
        public string? Suggestion { get; set; }
        public string? CodeExample { get; set; }
    }

    // ── Response ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Response from any Mainframe Development AI Agent endpoint.
    /// </summary>
    public class MainframeAiAgentResponse
    {
        public string JobId { get; set; } = string.Empty;
        public string PromptType { get; set; } = string.Empty;
        public string AgentName { get; set; } = string.Empty;

        public string? BusinessExplanation { get; set; }
        public string? GeneratedCode { get; set; }
        public List<MainframeAiRecommendationItem> Recommendations { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        public double Confidence { get; set; }
        public string ConfidenceLevel { get; set; } = string.Empty;
        public double ResponseTimeMs { get; set; }
        public Dictionary<string, object?>? TokenUsage { get; set; }

        public string GovernanceNotice { get; set; } =
            "AI Generated Developer Guidance | Review Required";

        public string? RequestId { get; set; }
        public string? PromptVersion { get; set; }
        public bool Cached { get; set; }

        // ── Derived helpers ───────────────────────────────────────────────────
        public bool HasGeneratedCode => !string.IsNullOrWhiteSpace(GeneratedCode);
        public bool HasExplanation => !string.IsNullOrWhiteSpace(BusinessExplanation);
        public int RecommendationCount => Recommendations.Count;
        public int HighSeverityCount =>
            Recommendations.Count(r => r.Severity.Equals("HIGH", StringComparison.OrdinalIgnoreCase));
    }

    // ── Page / View model ─────────────────────────────────────────────────────

    /// <summary>
    /// View model carrying the AI agent result back to the Index view via AJAX JSON.
    /// </summary>
    public class MainframeAiAgentPageDto
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public MainframeAiAgentResponse? Result { get; set; }
    }
}
