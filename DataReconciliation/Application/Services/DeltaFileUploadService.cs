using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    /// <summary>
    /// Enhancement 1: Processes incremental (delta) file uploads during review.
    /// Profiles new files, runs semantic enrichment, updates source catalog,
    /// and performs incremental mapping WITHOUT restarting the workflow job.
    /// </summary>
    public class DeltaFileUploadService : IDeltaFileUploadService
    {
        private readonly IFileIngestionService _fileIngestion;
        private readonly ISourceSchemaProfilingService _profiling;
        private readonly ITargetMetadataExtractionService _targetExtraction;
        private readonly ISemanticSchemaEnrichmentService _semanticEnrichment;
        private readonly IDeterministicMappingService _deterministicMapping;
        private readonly IAIMappingInferenceService _aiMapping;
        private readonly IMappingConsolidationService _consolidation;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IDatasetRegistrationService _datasetRegistration;
        private readonly ILogger<DeltaFileUploadService> _logger;

        public DeltaFileUploadService(
            IFileIngestionService fileIngestion,
            ISourceSchemaProfilingService profiling,
            ITargetMetadataExtractionService targetExtraction,
            ISemanticSchemaEnrichmentService semanticEnrichment,
            IDeterministicMappingService deterministicMapping,
            IAIMappingInferenceService aiMapping,
            IMappingConsolidationService consolidation,
            IArtifactPersistenceService artifactPersistence,
            IDatasetRegistrationService datasetRegistration,
            ILogger<DeltaFileUploadService> logger)
        {
            _fileIngestion = fileIngestion;
            _profiling = profiling;
            _targetExtraction = targetExtraction;
            _semanticEnrichment = semanticEnrichment;
            _deterministicMapping = deterministicMapping;
            _aiMapping = aiMapping;
            _consolidation = consolidation;
            _artifactPersistence = artifactPersistence;
            _datasetRegistration = datasetRegistration;
            _logger = logger;
        }

        public async Task<DeltaFileUploadResultDto> ProcessDeltaUploadAsync(
            string jobId,
            Stream fileStream,
            string fileName,
            string datasetId,
            DatasetRole role,
            DatasetType type,
            string? alias = null,
            string? domain = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation(
                "Delta file upload started. JobId={JobId} File={FileName} DatasetId={DatasetId} Role={Role}",
                jobId, fileName, datasetId, role);

            // 1. Ingest the new file
            var filePath = await _fileIngestion.IngestFileAsync(jobId, fileStream, fileName, role);
            _logger.LogInformation("Delta file ingested. JobId={JobId} Path={Path}", jobId, filePath);

            // 2. Update the manifest with the new dataset
            var manifest = await _artifactPersistence.LoadArtifactAsync<DatasetManifest>(
                jobId, ArtifactType.DatasetManifest);

            if (manifest == null)
            {
                _logger.LogWarning("No manifest found for delta upload. JobId={JobId}", jobId);
                return new DeltaFileUploadResultDto
                {
                    JobId = jobId,
                    FileName = fileName,
                    DatasetId = datasetId,
                    Status = "Failed - No manifest found",
                    TimeProcessedMs = sw.ElapsedMilliseconds
                };
            }

            // Add or replace dataset in manifest
            var existingDs = manifest.Datasets.FirstOrDefault(d =>
                d.DatasetId.Equals(datasetId, StringComparison.OrdinalIgnoreCase));

            if (existingDs != null)
            {
                existingDs.FileName = fileName;
                existingDs.LocalPath = filePath;
                existingDs.DatasetAlias = alias ?? existingDs.DatasetAlias;
                existingDs.DatasetDomain = domain ?? existingDs.DatasetDomain;
                _logger.LogInformation("Delta upload replaced existing dataset. JobId={JobId} DatasetId={DatasetId}", jobId, datasetId);
            }
            else
            {
                manifest.Datasets.Add(new DatasetRegistration
                {
                    DatasetId = datasetId,
                    FileName = fileName,
                    DatasetRole = role,
                    DatasetType = type,
                    DatasetAlias = alias,
                    DatasetDomain = domain,
                    LocalPath = filePath
                });
                _logger.LogInformation("Delta upload added new dataset. JobId={JobId} DatasetId={DatasetId}", jobId, datasetId);
            }

            await _artifactPersistence.PersistArtifactAsync(
                jobId, manifest, ArtifactType.DatasetManifest, "dataset_manifest.json");

            // 3. Profile the new file and persist it so step 5 can load it from disk
            SourceSchemaProfile? profile = null;
            int rowsAdded = 0;
            if (role == DatasetRole.SOURCE)
            {
                profile = await _profiling.ProfileSourceFileAsync(jobId, datasetId, filePath);
                rowsAdded = profile?.Fields.Sum(f => f.DistinctValueCount) ?? 0;
                if (profile != null)
                    await _profiling.PersistProfileAsync(jobId, profile);
                _logger.LogInformation("Delta file profiled and persisted. JobId={JobId} Fields={Fields}", jobId, profile?.Fields.Count ?? 0);
            }

            // 4. Run Semantic Enrichment on the new file
            SemanticSchemaProfile? semanticProfile = null;
            if (profile != null)
            {
                try
                {
                    semanticProfile = await _semanticEnrichment.EnrichSchemaAsync(jobId, profile);
                    _logger.LogInformation("Delta file semantically enriched. JobId={JobId} DatasetId={DatasetId}", jobId, datasetId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Semantic enrichment failed for delta file. JobId={JobId} DatasetId={DatasetId}. Continuing without enrichment.", jobId, datasetId);
                }
            }

            // 5. Perform incremental mapping
            int mappingsUpdated = 0;
            if (role == DatasetRole.SOURCE && profile != null)
            {
                try
                {
                    mappingsUpdated = await RunMappingPipelineAsync(jobId, manifest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Incremental mapping after delta upload encountered an error. JobId={JobId}. Existing mappings remain intact.",
                        jobId);
                }
            }
            else if (role == DatasetRole.TARGET_SCHEMA)
            {
                try
                {
                    // Re-extract target metadata from the updated schema file
                    var targetProfile = await _targetExtraction.ExtractTargetMetadataAsync(jobId, filePath);
                    await _targetExtraction.PersistMetadataProfileAsync(jobId, targetProfile);
                    _logger.LogInformation("Target schema re-extracted. JobId={JobId} Fields={Fields}",
                        jobId, targetProfile?.Fields?.Count ?? 0);

                    // Re-run full mapping pipeline with refreshed target
                    mappingsUpdated = await RunMappingPipelineAsync(jobId, manifest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Target metadata re-extraction failed after delta upload. JobId={JobId}", jobId);
                }
            }

            sw.Stop();
            var result = new DeltaFileUploadResultDto
            {
                JobId = jobId,
                FileName = fileName,
                DatasetId = datasetId,
                RowsAdded = rowsAdded,
                MappingsUpdated = mappingsUpdated,
                TimeProcessedMs = sw.ElapsedMilliseconds,
                UploadedAt = DateTime.UtcNow,
                Status = "Processed"
            };

            _logger.LogInformation(
                "Delta file upload completed. JobId={JobId} File={FileName} RowsAdded={Rows} MappingsUpdated={Mappings} Duration={Duration}ms",
                jobId, fileName, rowsAdded, mappingsUpdated, sw.ElapsedMilliseconds);

            return result;
        }

        /// <summary>
        /// Loads all persisted source profiles, re-runs deterministic + AI mapping + consolidation,
        /// then merges the result with any CONFIRMED mappings from the existing config to preserve
        /// manual user edits.
        /// </summary>
        private async Task<int> RunMappingPipelineAsync(string jobId, DatasetManifest manifest)
        {
            var allSourceProfiles = new List<SourceSchemaProfile>();
            foreach (var dataset in manifest.Datasets.Where(d => d.DatasetRole == DatasetRole.SOURCE))
            {
                var dsProfile = await _artifactPersistence.LoadArtifactByNameAsync<SourceSchemaProfile>(
                    jobId, $"source_schema_profile_{dataset.DatasetId}.json");
                if (dsProfile != null)
                    allSourceProfiles.Add(dsProfile);
            }

            if (!allSourceProfiles.Any())
            {
                _logger.LogWarning("No source profiles found on disk for mapping re-run. JobId={JobId}", jobId);
                return 0;
            }

            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, ArtifactType.TargetMetadataProfile);

            if (targetProfile == null)
            {
                _logger.LogWarning("No target profile found for mapping re-run. JobId={JobId}", jobId);
                return 0;
            }

            // Re-run deterministic mapping
            var candidates = await _deterministicMapping.GenerateMappingCandidatesAsync(
                jobId, allSourceProfiles, targetProfile, new List<SemanticSchemaProfile>());

            // Re-run AI mapping
            var aiResponse = await _aiMapping.InferMappingsAsync(
                jobId, candidates, targetProfile, new List<SemanticSchemaProfile>(), allSourceProfiles);

            // Re-consolidate
            var newFinalMapping = await _consolidation.ConsolidateMappingsAsync(
                jobId, candidates, aiResponse, targetProfile);

            // Preserve CONFIRMED mappings from the existing config so user edits are not overwritten
            var existingConfig = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            if (existingConfig?.Mappings?.Count > 0)
            {
                var confirmedByTarget = existingConfig.Mappings
                    .Where(m => m.Status == MappingStatus.CONFIRMED)
                    .ToDictionary(m => m.TargetField, StringComparer.OrdinalIgnoreCase);

                foreach (var newMap in newFinalMapping.Mappings)
                {
                    if (confirmedByTarget.TryGetValue(newMap.TargetField, out var confirmed))
                    {
                        newMap.SourceField = confirmed.SourceField;
                        newMap.SourceDataset = confirmed.SourceDataset;
                        newMap.Transformations = confirmed.Transformations;
                        newMap.AdditionalSources = confirmed.AdditionalSources;
                        newMap.MatchSource = confirmed.MatchSource;
                        newMap.Status = MappingStatus.CONFIRMED;
                        newMap.Confidence = confirmed.Confidence;
                    }
                }

                _logger.LogInformation(
                    "Preserved {Count} CONFIRMED mapping(s) from existing config. JobId={JobId}",
                    confirmedByTarget.Count, jobId);
            }

            _logger.LogInformation(
                "Delta incremental mapping completed. JobId={JobId} MappingsUpdated={Count}",
                jobId, newFinalMapping.Mappings.Count);

            return newFinalMapping.Mappings.Count;
        }
    }
}
