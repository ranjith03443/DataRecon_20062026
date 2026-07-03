using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataReconciliation.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<WorkflowJob> WorkflowJobs => Set<WorkflowJob>();
        public DbSet<WorkflowStepExecution> WorkflowStepExecutions => Set<WorkflowStepExecution>();
        public DbSet<WorkflowArtifact> WorkflowArtifacts => Set<WorkflowArtifact>();
        public DbSet<AIInferenceAudit> AIInferenceAudits => Set<AIInferenceAudit>();
        public DbSet<ErrorAuditLog> ErrorAuditLogs => Set<ErrorAuditLog>();
        public DbSet<HistoricalMappingEntry> HistoricalMappings => Set<HistoricalMappingEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // WorkflowJob
            modelBuilder.Entity<WorkflowJob>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.JobId).IsUnique();
                e.Property(x => x.Status).HasConversion<string>();
                e.Property(x => x.CurrentStep).HasConversion<string>();
                e.HasMany(x => x.StepExecutions).WithOne(x => x.WorkflowJob).HasForeignKey(x => x.WorkflowJobId);
                e.HasMany(x => x.Artifacts).WithOne(x => x.WorkflowJob).HasForeignKey(x => x.WorkflowJobId);
            });

            // WorkflowStepExecution
            modelBuilder.Entity<WorkflowStepExecution>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.JobId, x.Step });
                e.Property(x => x.Status).HasConversion<string>();
                e.Property(x => x.Step).HasConversion<string>();
            });

            // WorkflowArtifact
            modelBuilder.Entity<WorkflowArtifact>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.JobId, x.ArtifactType });
                e.Property(x => x.ArtifactType).HasConversion<string>();
                e.Property(x => x.GeneratedByStep).HasConversion<string>();
            });

            // AIInferenceAudit
            modelBuilder.Entity<AIInferenceAudit>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.JobId);
                e.Property(x => x.Step).HasConversion<string>();
            });

            // ErrorAuditLog
            modelBuilder.Entity<ErrorAuditLog>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.JobId);
                e.Property(x => x.Step).HasConversion<string>();
            });

            // HistoricalMappingEntry
            modelBuilder.Entity<HistoricalMappingEntry>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.TargetField).IsUnique();
                e.HasIndex(x => x.IsActive);
            });
        }
    }
}
