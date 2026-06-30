namespace DataReconciliation.Domain.Transformations
{
    public class TransformationAuditDocument
    {
        public string JobId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public List<TransformationAuditEntry> Entries { get; set; } = new();
    }

    public class TransformationAuditEntry
    {
        public int RecordId { get; set; }
        public string SourceField { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string? OriginalValue { get; set; }
        public string? TransformedValue { get; set; }
        public string Operation { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string CorrelationId { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;
        public double ExecutionDurationMs { get; set; }
    }
}
