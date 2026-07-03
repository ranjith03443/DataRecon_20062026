using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class AuditLoggingService : IAuditLoggingService
    {
        private readonly ILogger<AuditLoggingService> _logger;
        private readonly IArtifactPersistenceService _artifactPersistence;

        public AuditLoggingService(
            ILogger<AuditLoggingService> logger,
            IArtifactPersistenceService artifactPersistence)
        {
            _logger = logger;
            _artifactPersistence = artifactPersistence;
        }

        public async Task InitializeAuditLogAsync(string jobId, string reviewerName)
        {
            var log = new GovernanceAuditLog
            {
                JobId         = jobId,
                ReviewerName  = reviewerName,
                CreatedAt     = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                Entries = new List<AuditEntry>
                {
                    new AuditEntry
                    {
                        Timestamp    = DateTime.UtcNow,
                        Reviewer     = reviewerName,
                        ActionType   = AuditActionType.WorkflowStarted,
                        WorkflowStep = "DatasetRegistration",
                        TargetField  = string.Empty,
                        Notes        = "Workflow initiated"
                    }
                }
            };

            await _artifactPersistence.PersistArtifactAsync(jobId, log, ArtifactType.GovernanceAuditLog, "governance_audit_log.json");
            _logger.LogInformation("Audit log initialized. JobId={JobId} Reviewer={Reviewer}", jobId, reviewerName);
        }

        public async Task AppendAuditEntryAsync(string jobId, AuditEntry entry)
        {
            var log = await _artifactPersistence.LoadArtifactByNameAsync<GovernanceAuditLog>(jobId, "governance_audit_log.json")
                      ?? new GovernanceAuditLog { JobId = jobId };

            log.Entries.Add(entry);
            log.LastUpdatedAt = DateTime.UtcNow;

            await _artifactPersistence.PersistArtifactAsync(jobId, log, ArtifactType.GovernanceAuditLog, "governance_audit_log.json");
        }

        public async Task<GovernanceAuditLog?> LoadAuditLogAsync(string jobId) =>
            await _artifactPersistence.LoadArtifactByNameAsync<GovernanceAuditLog>(jobId, "governance_audit_log.json");
    }
}
