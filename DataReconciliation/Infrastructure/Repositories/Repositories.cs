using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataReconciliation.Infrastructure.Repositories
{
    public class WorkflowJobRepository : IWorkflowJobRepository
    {
        private readonly AppDbContext _context;

        public WorkflowJobRepository(AppDbContext context) => _context = context;

        public async Task<WorkflowJob> CreateAsync(WorkflowJob job)
        {
            _context.WorkflowJobs.Add(job);
            await _context.SaveChangesAsync();
            return job;
        }

        public async Task<WorkflowJob?> GetByJobIdAsync(string jobId) =>
            await _context.WorkflowJobs.FirstOrDefaultAsync(j => j.JobId == jobId);

        public async Task<IEnumerable<WorkflowJob>> GetAllAsync() =>
            await _context.WorkflowJobs.OrderByDescending(j => j.CreatedAt).ToListAsync();

        public async Task<WorkflowJob> UpdateAsync(WorkflowJob job)
        {
            _context.WorkflowJobs.Update(job);
            await _context.SaveChangesAsync();
            return job;
        }

        public async Task<WorkflowJob?> GetWithStepsAsync(string jobId) =>
            await _context.WorkflowJobs
                .Include(j => j.StepExecutions)
                .Include(j => j.Artifacts)
                .FirstOrDefaultAsync(j => j.JobId == jobId);
    }

    public class WorkflowStepExecutionRepository : IWorkflowStepExecutionRepository
    {
        private readonly AppDbContext _context;

        public WorkflowStepExecutionRepository(AppDbContext context) => _context = context;

        public async Task<WorkflowStepExecution> CreateAsync(WorkflowStepExecution step)
        {
            _context.WorkflowStepExecutions.Add(step);
            await _context.SaveChangesAsync();
            return step;
        }

        public async Task<WorkflowStepExecution?> GetByJobAndStepAsync(string jobId, WorkflowStep step) =>
            await _context.WorkflowStepExecutions
                .FirstOrDefaultAsync(s => s.JobId == jobId && s.Step == step);

        public async Task<IEnumerable<WorkflowStepExecution>> GetByJobIdAsync(string jobId) =>
            await _context.WorkflowStepExecutions
                .Where(s => s.JobId == jobId)
                .OrderBy(s => s.Step)
                .ToListAsync();

        public async Task<WorkflowStepExecution> UpdateAsync(WorkflowStepExecution step)
        {
            _context.WorkflowStepExecutions.Update(step);
            await _context.SaveChangesAsync();
            return step;
        }
    }

    public class WorkflowArtifactRepository : IWorkflowArtifactRepository
    {
        private readonly AppDbContext _context;

        public WorkflowArtifactRepository(AppDbContext context) => _context = context;

        public async Task<WorkflowArtifact> CreateAsync(WorkflowArtifact artifact)
        {
            _context.WorkflowArtifacts.Add(artifact);
            await _context.SaveChangesAsync();
            return artifact;
        }

        public async Task<IEnumerable<WorkflowArtifact>> GetByJobIdAsync(string jobId) =>
            await _context.WorkflowArtifacts
                .Where(a => a.JobId == jobId)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

        public async Task<WorkflowArtifact?> GetByJobAndTypeAsync(string jobId, ArtifactType type) =>
            await _context.WorkflowArtifacts
                .FirstOrDefaultAsync(a => a.JobId == jobId && a.ArtifactType == type);

        public async Task<WorkflowArtifact> UpdateAsync(WorkflowArtifact artifact)
        {
            _context.WorkflowArtifacts.Update(artifact);
            await _context.SaveChangesAsync();
            return artifact;
        }
    }

    public class AIInferenceAuditRepository : IAIInferenceAuditRepository
    {
        private readonly AppDbContext _context;

        public AIInferenceAuditRepository(AppDbContext context) => _context = context;

        public async Task<AIInferenceAudit> CreateAsync(AIInferenceAudit audit)
        {
            _context.AIInferenceAudits.Add(audit);
            await _context.SaveChangesAsync();
            return audit;
        }

        public async Task<IEnumerable<AIInferenceAudit>> GetByJobIdAsync(string jobId) =>
            await _context.AIInferenceAudits
                .Where(a => a.JobId == jobId)
                .OrderByDescending(a => a.RequestedAt)
                .ToListAsync();
    }

    public class ErrorAuditLogRepository : IErrorAuditLogRepository
    {
        private readonly AppDbContext _context;

        public ErrorAuditLogRepository(AppDbContext context) => _context = context;

        public async Task<ErrorAuditLog> CreateAsync(ErrorAuditLog log)
        {
            _context.ErrorAuditLogs.Add(log);
            await _context.SaveChangesAsync();
            return log;
        }

        public async Task<IEnumerable<ErrorAuditLog>> GetByJobIdAsync(string jobId) =>
            await _context.ErrorAuditLogs
                .Where(l => l.JobId == jobId)
                .OrderByDescending(l => l.OccurredAt)
                .ToListAsync();
    }
}
