namespace DataReconciliation.Domain.Models
{
    // ─── Governance Audit Log (persisted as governance_audit_log.json) ──────────
    public class GovernanceAuditLog
    {
        public string JobId { get; set; } = string.Empty;
        public string ReviewerName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
        public List<AuditEntry> Entries { get; set; } = new();
    }

    public class AuditEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString()[..8];
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Reviewer { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string WorkflowStep { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string? SourceField { get; set; }
        public string? AiSuggested { get; set; }
        public string? HumanChoice { get; set; }
        public double? AiConfidence { get; set; }
        public string? Notes { get; set; }
    }

    public static class AuditActionType
    {
        public const string WorkflowStarted          = "WORKFLOW_STARTED";
        public const string WorkflowCompleted        = "WORKFLOW_COMPLETED";
        public const string MappingAccepted          = "MAPPING_ACCEPTED";
        public const string MappingRejected          = "MAPPING_REJECTED";
        public const string MappingModified          = "MAPPING_MODIFIED";
        public const string ValueMappingAccepted     = "VALUE_MAPPING_ACCEPTED";
        public const string ValueMappingModified     = "VALUE_MAPPING_MODIFIED";
        public const string TransformationAccepted   = "TRANSFORMATION_ACCEPTED";
        public const string TransformationOverridden = "TRANSFORMATION_OVERRIDDEN";
        public const string TransformationRejected   = "TRANSFORMATION_REJECTED";
    }
}
