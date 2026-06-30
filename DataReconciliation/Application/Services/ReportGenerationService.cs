using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DataReconciliation.Application.Services
{
    public class ReportGenerationService : IReportGenerationService
    {
        private readonly ILogger<ReportGenerationService> _logger;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IWorkflowJobRepository _jobRepo;
        private readonly IAIInferenceAuditRepository _aiAuditRepo;
        private readonly IErrorAuditLogRepository _errorLogRepo;
        private readonly string _baseWorkflowPath;

        public ReportGenerationService(
            ILogger<ReportGenerationService> logger,
            IArtifactPersistenceService artifactPersistence,
            IWorkflowJobRepository jobRepo,
            IAIInferenceAuditRepository aiAuditRepo,
            IErrorAuditLogRepository errorLogRepo,
            IConfiguration configuration)
        {
            _logger = logger;
            _artifactPersistence = artifactPersistence;
            _jobRepo = jobRepo;
            _aiAuditRepo = aiAuditRepo;
            _errorLogRepo = errorLogRepo;
            _baseWorkflowPath = configuration["WorkflowStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "workflow");
        }

        public async Task GenerateAuditReportAsync(string jobId)
        {
            _logger.LogInformation("Generating audit report. JobId={JobId}", jobId);

            var job = await _jobRepo.GetWithStepsAsync(jobId);
            var aiAudits = await _aiAuditRepo.GetByJobIdAsync(jobId);
            var errorLogs = await _errorLogRepo.GetByJobIdAsync(jobId);

            var auditReport = new
            {
                JobId = jobId,
                JobName = job?.JobName,
                Status = job?.Status.ToString(),
                GeneratedAt = DateTime.UtcNow,
                Steps = job?.StepExecutions?.Select(s => new
                {
                    Step = s.Step.ToString(),
                    Status = s.Status.ToString(),
                    s.StartedAt,
                    s.CompletedAt,
                    s.DurationMs,
                    s.RetryCount,
                    s.ErrorDetails
                }).ToList(),
                AIInferenceSummary = new
                {
                    TotalCalls = aiAudits.Count(),
                    CacheHits = aiAudits.Count(a => a.WasCached),
                    AcceptedResponses = aiAudits.Count(a => a.WasAccepted),
                    AverageConfidence = aiAudits.Any() ? aiAudits.Average(a => a.ConfidenceScore ?? 0) : 0
                },
                ErrorSummary = new
                {
                    TotalErrors = errorLogs.Count(),
                    UnresolvedErrors = errorLogs.Count(e => !e.IsResolved)
                }
            };

            var reportsPath = Path.Combine(_baseWorkflowPath, jobId, "reports");
            Directory.CreateDirectory(reportsPath);
            var reportPath = Path.Combine(reportsPath, "audit_report.json");
            await File.WriteAllTextAsync(reportPath, JsonConvert.SerializeObject(auditReport, Formatting.Indented));

            _logger.LogInformation("Audit report generated. JobId={JobId} Path={Path}", jobId, reportPath);
        }

        public async Task GenerateReconciliationReportAsync(string jobId, ReconciliationResult result)
        {
            _logger.LogInformation("Generating reconciliation report. JobId={JobId}", jobId);

            var reportsPath = Path.Combine(_baseWorkflowPath, jobId, "reports");
            Directory.CreateDirectory(reportsPath);
            var reportPath = Path.Combine(reportsPath, "reconciliation_report.json");
            await File.WriteAllTextAsync(reportPath, JsonConvert.SerializeObject(result, Formatting.Indented));

            _logger.LogInformation("Reconciliation report generated. JobId={JobId} Path={Path}", jobId, reportPath);
        }
    }
}
