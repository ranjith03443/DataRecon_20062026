using DataReconciliation.Application.Interfaces;
using DataReconciliation.Application.Transformations;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class TransformationEngineService : ITransformationEngineService
    {
        private readonly ILogger<TransformationEngineService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly ITransformationExecutionService _executionService;

        public TransformationEngineService(
            ILogger<TransformationEngineService> logger,
            IArtifactPersistenceService artifactService,
            ITransformationExecutionService executionService)
        {
            _logger = logger;
            _artifactService = artifactService;
            _executionService = executionService;
        }

        public async Task<IEnumerable<Dictionary<string, string>>> ExecuteTransformationsAsync(
            string jobId,
            IEnumerable<CanonicalRecord> canonicalRecords,
            FinalMappingConfig mappingConfig,
            TargetMetadataProfile targetProfile)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting transformation execution. JobId={JobId}", jobId);

            var executionResult = await _executionService.ExecuteAsync(
                jobId,
                canonicalRecords,
                mappingConfig,
                targetProfile);

            var transformedRecords = executionResult.TransformedRecords;

            sw.Stop();
            _logger.LogInformation("Transformation execution completed. JobId={JobId} TotalRecords={Total} Rejected={Rejected} Duration={Duration}ms",
                jobId,
                transformedRecords.Count,
                executionResult.ValidationReport.RejectedRecords.Count,
                sw.ElapsedMilliseconds);

            await PersistTransformationOutputAsync(jobId, transformedRecords);
            return transformedRecords;
        }

        public async Task PersistTransformationOutputAsync(string jobId, IEnumerable<Dictionary<string, string>> output)
        {
            await _artifactService.PersistArtifactAsync(jobId, output.ToList(), ArtifactType.TransformationOutput, "transformation_output.json");
        }
    }
}
