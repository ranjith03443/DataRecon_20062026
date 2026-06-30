using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Enums;

namespace DataReconciliation.Application.Interfaces
{
    public interface IWorkflowJobRepository
    {
        Task<WorkflowJob> CreateAsync(WorkflowJob job);
        Task<WorkflowJob?> GetByJobIdAsync(string jobId);
        Task<IEnumerable<WorkflowJob>> GetAllAsync();
        Task<WorkflowJob> UpdateAsync(WorkflowJob job);
        Task<WorkflowJob?> GetWithStepsAsync(string jobId);
    }

    public interface IWorkflowStepExecutionRepository
    {
        Task<WorkflowStepExecution> CreateAsync(WorkflowStepExecution step);
        Task<WorkflowStepExecution?> GetByJobAndStepAsync(string jobId, WorkflowStep step);
        Task<IEnumerable<WorkflowStepExecution>> GetByJobIdAsync(string jobId);
        Task<WorkflowStepExecution> UpdateAsync(WorkflowStepExecution step);
    }

    public interface IWorkflowArtifactRepository
    {
        Task<WorkflowArtifact> CreateAsync(WorkflowArtifact artifact);
        Task<IEnumerable<WorkflowArtifact>> GetByJobIdAsync(string jobId);
        Task<WorkflowArtifact?> GetByJobAndTypeAsync(string jobId, ArtifactType type);
        Task<WorkflowArtifact> UpdateAsync(WorkflowArtifact artifact);
    }

    public interface IAIInferenceAuditRepository
    {
        Task<AIInferenceAudit> CreateAsync(AIInferenceAudit audit);
        Task<IEnumerable<AIInferenceAudit>> GetByJobIdAsync(string jobId);
    }

    public interface IErrorAuditLogRepository
    {
        Task<ErrorAuditLog> CreateAsync(ErrorAuditLog log);
        Task<IEnumerable<ErrorAuditLog>> GetByJobIdAsync(string jobId);
    }
}
