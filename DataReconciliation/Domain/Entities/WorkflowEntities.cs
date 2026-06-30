using DataReconciliation.Domain.Enums;

namespace DataReconciliation.Domain.Entities
{
    public class WorkflowJob
    {
        public int Id { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;
        public WorkflowStep CurrentStep { get; set; } = WorkflowStep.DatasetRegistration;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorDetails { get; set; }
        public string? Description { get; set; }
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
        public bool IsReplay { get; set; } = false;
        public string? ReplayFromJobId { get; set; }

        public ICollection<WorkflowStepExecution> StepExecutions { get; set; } = new List<WorkflowStepExecution>();
        public ICollection<WorkflowArtifact> Artifacts { get; set; } = new List<WorkflowArtifact>();
    }

    public class WorkflowStepExecution
    {
        public int Id { get; set; }
        public int WorkflowJobId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public WorkflowStep Step { get; set; }
        public StepStatus Status { get; set; } = StepStatus.Pending;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public double? DurationMs { get; set; }
        public int RetryCount { get; set; } = 0;
        public string? InputArtifacts { get; set; }
        public string? OutputArtifacts { get; set; }
        public string? ErrorDetails { get; set; }
        public string? StepMetadata { get; set; }

        public WorkflowJob? WorkflowJob { get; set; }
    }

    public class WorkflowArtifact
    {
        public int Id { get; set; }
        public int WorkflowJobId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public ArtifactType ArtifactType { get; set; }
        public string ArtifactName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string? ContentHash { get; set; }
        public string? Version { get; set; } = "1.0";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsValid { get; set; } = true;
        public WorkflowStep GeneratedByStep { get; set; }

        public WorkflowJob? WorkflowJob { get; set; }
    }

    public class AIInferenceAudit
    {
        public int Id { get; set; }
        public string JobId { get; set; } = string.Empty;
        public WorkflowStep Step { get; set; }
        public string PromptText { get; set; } = string.Empty;
        public string? ResponseText { get; set; }
        public double? ConfidenceScore { get; set; }
        public bool WasCached { get; set; } = false;
        public bool WasAccepted { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }
        public double? DurationMs { get; set; }
        public string? ErrorDetails { get; set; }
        public string CacheKey { get; set; } = string.Empty;

        // Token usage (from Python AI service response)
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public double? EstimatedCostUsd { get; set; }
    }

    public class ErrorAuditLog
    {
        public int Id { get; set; }
        public string JobId { get; set; } = string.Empty;
        public WorkflowStep? Step { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
        public string? Context { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
        public string CorrelationId { get; set; } = string.Empty;
        public bool IsResolved { get; set; } = false;
    }
}
