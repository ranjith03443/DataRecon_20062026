using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace DataReconciliation.Infrastructure.FileStorage
{
    public class ArtifactPersistenceService : IArtifactPersistenceService
    {
        private readonly ILogger<ArtifactPersistenceService> _logger;
        private readonly string _baseWorkflowPath;

        public ArtifactPersistenceService(ILogger<ArtifactPersistenceService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _baseWorkflowPath = configuration["WorkflowStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "workflow");
        }

        public async Task<string> PersistArtifactAsync<T>(string jobId, T data, ArtifactType type, string fileName) where T : class
        {
            var artifactPath = Path.Combine(_baseWorkflowPath, jobId, "artifacts");
            Directory.CreateDirectory(artifactPath);
            var filePath = Path.Combine(artifactPath, fileName);

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);

            _logger.LogInformation("Artifact persisted: JobId={JobId} Type={ArtifactType} Path={FilePath}", jobId, type, filePath);
            return filePath;
        }

        public async Task<T?> LoadArtifactAsync<T>(string jobId, ArtifactType type) where T : class
        {
            var artifactPath = Path.Combine(_baseWorkflowPath, jobId, "artifacts");
            var fileName = GetArtifactFileName(type);
            var filePath = Path.Combine(artifactPath, fileName);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Artifact not found: JobId={JobId} Type={ArtifactType} Path={FilePath}", jobId, type, filePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            return JsonConvert.DeserializeObject<T>(json);
        }

        public async Task<T?> LoadArtifactByNameAsync<T>(string jobId, string fileName) where T : class
        {
            var filePath = Path.Combine(_baseWorkflowPath, jobId, "artifacts", fileName);
            if (!File.Exists(filePath)) return null;
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            return JsonConvert.DeserializeObject<T>(json);
        }

        public Task<bool> ArtifactExistsAsync(string jobId, ArtifactType type)
        {
            var artifactPath = Path.Combine(_baseWorkflowPath, jobId, "artifacts");
            var fileName = GetArtifactFileName(type);
            var filePath = Path.Combine(artifactPath, fileName);
            return Task.FromResult(File.Exists(filePath));
        }

        public static string ComputeHash(string content)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private static string GetArtifactFileName(ArtifactType type) => type switch
        {
            ArtifactType.DatasetManifest => "dataset_manifest.json",
            ArtifactType.SourceSchemaProfile => "source_schema_profile.json",
            ArtifactType.TargetMetadataProfile => "target_metadata_profile.json",
            ArtifactType.SemanticSchemaProfile => "semantic_schema_profile.json",
            ArtifactType.MappingCandidates => "mapping_candidates.json",
            ArtifactType.AIMappingResponse => "ai_mapping_response.json",
            ArtifactType.CanonicalMappingConfig => "canonical_mapping_config.json",
            ArtifactType.FinalMappingConfig => "final_mapping_config.json",
            ArtifactType.ValueMappings => "value_mappings.json",
            ArtifactType.ValueMappingCandidates => "value_mapping_candidates.json",
            ArtifactType.TransformationOutput => "transformation_output.json",
            ArtifactType.ReconciliationResult => "reconciliation_result.json",
            ArtifactType.ReconciliationConfig => "reconciliation_config.json",
            ArtifactType.TransformationOverrides => "transformation_overrides.json",
            ArtifactType.AuditLog => "audit_log.json",
            ArtifactType.WorkflowLog => "workflow_log.json",
            ArtifactType.TargetFile => "target_output.dat",
            ArtifactType.EvaluationSummary => "evaluation_summary.json",
            ArtifactType.GovernanceAuditLog => "governance_audit_log.json",
            ArtifactType.EvaluationHistory => "evaluation_history.json",
            _ => $"{type.ToString().ToLowerInvariant()}.json"
        };
    }
}
