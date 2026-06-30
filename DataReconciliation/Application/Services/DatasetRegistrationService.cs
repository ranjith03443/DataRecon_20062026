using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.Application.Services
{
    public class DatasetRegistrationService : IDatasetRegistrationService
    {
        private readonly ILogger<DatasetRegistrationService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly string _baseWorkflowPath;

        public DatasetRegistrationService(
            ILogger<DatasetRegistrationService> logger,
            IArtifactPersistenceService artifactService,
            IConfiguration configuration)
        {
            _logger = logger;
            _artifactService = artifactService;
            _baseWorkflowPath = configuration["WorkflowStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "workflow");
        }

        public async Task<DatasetManifest> RegisterDatasetsAsync(string jobId, IEnumerable<DatasetRegistrationRequest> requests)
        {
            _logger.LogInformation("Starting dataset registration. JobId={JobId}", jobId);

            var manifest = new DatasetManifest
            {
                JobId = jobId,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var req in requests)
            {
                _logger.LogInformation("Registering dataset. JobId={JobId} DatasetId={DatasetId} Role={Role}", jobId, req.DatasetId, req.DatasetRole);

                var registration = new DatasetRegistration
                {
                    DatasetId = req.DatasetId,
                    FileName = req.FileName,
                    DatasetRole = req.DatasetRole,
                    DatasetType = req.DatasetType,
                    DatasetAlias = req.DatasetAlias,
                    DatasetDomain = req.DatasetDomain,
                    Description = req.Description,
                    LocalPath = Path.Combine(_baseWorkflowPath, jobId, "input", req.FileName)
                };

                manifest.Datasets.Add(registration);
                _logger.LogInformation("Dataset registered successfully. JobId={JobId} DatasetId={DatasetId}", jobId, req.DatasetId);
            }

            await PersistManifestAsync(jobId, manifest);
            _logger.LogInformation("Dataset manifest persisted. JobId={JobId} DatasetCount={Count}", jobId, manifest.Datasets.Count);
            return manifest;
        }

        public async Task<DatasetManifest?> LoadManifestAsync(string jobId)
        {
            _logger.LogInformation("Loading dataset manifest. JobId={JobId}", jobId);
            return await _artifactService.LoadArtifactAsync<DatasetManifest>(jobId, ArtifactType.DatasetManifest);
        }

        public async Task PersistManifestAsync(string jobId, DatasetManifest manifest)
        {
            await _artifactService.PersistArtifactAsync(jobId, manifest, ArtifactType.DatasetManifest, "dataset_manifest.json");
        }
    }
}
