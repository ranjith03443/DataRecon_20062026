namespace DataReconciliation.Domain.Transformations
{
    public class TransformationValidationReport
    {
        public string JobId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public List<int> RejectedRecords { get; set; } = new();
        public List<ValidationIssue> DatatypeFailures { get; set; } = new();
        public List<ValidationIssue> MaxLengthFailures { get; set; } = new();
        public List<ValidationIssue> InvalidOperations { get; set; } = new();
        public List<ValidationIssue> FormattingIssues { get; set; } = new();
        public List<ValidationIssue> ParameterFailures { get; set; } = new();
    }

    public class ValidationIssue
    {
        public int? RecordId { get; set; }
        public string SourceField { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
