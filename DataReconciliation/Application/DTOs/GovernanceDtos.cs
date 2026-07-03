using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Models;

namespace DataReconciliation.Application.DTOs
{
    public class GovernanceDashboardViewModel
    {
        public string JobId { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public EvaluationSummary? Evaluation { get; set; }
        public GovernanceAuditLog? AuditLog { get; set; }
        public List<AIInferenceAudit> AiAudits { get; set; } = new();
        public EvaluationRunHistory RunHistory { get; set; } = new();
    }
}
