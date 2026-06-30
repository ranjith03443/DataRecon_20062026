using DataReconciliation.Domain.Models;

namespace DataReconciliation.Domain.Transformations
{
    public class TransformationRulesArtifact
    {
        public string JobId { get; set; } = string.Empty;
        public string ArtifactVersion { get; set; } = "1.0";
        public List<TransformationRuleContract> Rules { get; set; } = new();
    }

    public class TransformationRuleContract
    {
        public string SourceField { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double Confidence { get; set; }
        public string GeneratedBy { get; set; } = "RULE_ENGINE";
    }

    public class SupportedOperationsRegistry
    {
        public List<string> Operations { get; set; } = new();
    }

    public class TransformationExecutionInput
    {
        public string? CurrentValue { get; set; }
        public CanonicalRecord Record { get; set; } = new();
        public FinalMapping Mapping { get; set; } = new();
        public TargetFieldMetadata TargetField { get; set; } = new();
    }

    public class TransformationExecutionResult
    {
        public string JobId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public List<Dictionary<string, string>> TransformedRecords { get; set; } = new();
        public TransformationValidationReport ValidationReport { get; set; } = new();
        public TransformationAuditDocument Audit { get; set; } = new();
    }
}
