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

    public class HistoricalMappingRepository : IHistoricalMappingRepository
    {
        private readonly AppDbContext _context;

        public HistoricalMappingRepository(AppDbContext context) => _context = context;

        public async Task<HistoricalMappingEntry?> GetByTargetFieldAsync(string targetField) =>
            await _context.HistoricalMappings
                .FirstOrDefaultAsync(e => e.TargetField == targetField);

        public async Task<IEnumerable<HistoricalMappingEntry>> GetAllAsync(bool activeOnly = false)
        {
            var q = _context.HistoricalMappings.AsQueryable();
            if (activeOnly) q = q.Where(e => e.IsActive);
            return await q.OrderBy(e => e.TargetField).ToListAsync();
        }

        public async Task<HistoricalMappingEntry?> GetByIdAsync(int id) =>
            await _context.HistoricalMappings.FindAsync(id);

        public async Task<HistoricalMappingEntry> UpsertAsync(HistoricalMappingEntry entry)
        {
            var existing = await GetByTargetFieldAsync(entry.TargetField);
            if (existing == null)
            {
                _context.HistoricalMappings.Add(entry);
            }
            else
            {
                // Upsert: update fields but preserve manual edits
                existing.SourceField           = entry.SourceField;
                existing.SourceDataset         = entry.SourceDataset;
                existing.MatchSource           = entry.MatchSource;
                existing.TransformationSummary = entry.TransformationSummary;
                existing.LastJobId             = entry.LastJobId;
                existing.LastReviewerName      = entry.LastReviewerName;
                existing.LastUsedAt            = entry.LastUsedAt;
                existing.UsageCount           += 1;
                if (entry.Confidence > existing.Confidence)
                    existing.Confidence = entry.Confidence;
                _context.HistoricalMappings.Update(existing);
            }
            await _context.SaveChangesAsync();
            return existing ?? entry;
        }

        public async Task<HistoricalMappingEntry> UpdateAsync(HistoricalMappingEntry entry)
        {
            _context.HistoricalMappings.Update(entry);
            await _context.SaveChangesAsync();
            return entry;
        }

        public async Task DeleteAsync(int id)
        {
            var entry = await _context.HistoricalMappings.FindAsync(id);
            if (entry != null)
            {
                _context.HistoricalMappings.Remove(entry);
                await _context.SaveChangesAsync();
            }
        }
    }
}
