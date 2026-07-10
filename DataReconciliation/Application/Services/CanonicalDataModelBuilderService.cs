using CsvHelper;
using CsvHelper.Configuration;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;

namespace DataReconciliation.Application.Services
{
    public class CanonicalDataModelBuilderService : ICanonicalDataModelBuilderService
    {
        private readonly ILogger<CanonicalDataModelBuilderService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        public CanonicalDataModelBuilderService(
            ILogger<CanonicalDataModelBuilderService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
        }

        public async Task<IEnumerable<CanonicalRecord>> BuildCanonicalModelAsync(
            string jobId,
            DatasetManifest manifest,
            CanonicalMappingConfig config)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Building canonical data model. JobId={JobId}", jobId);

            // Load all source CSV files into memory-efficient dictionary structures
            var sourceData = new Dictionary<string, List<Dictionary<string, string>>>();

            foreach (var dataset in manifest.Datasets.Where(d => d.DatasetRole == DatasetRole.SOURCE))
            {
                if (!File.Exists(dataset.LocalPath))
                {
                    _logger.LogWarning("Source file not found. JobId={JobId} DatasetId={DatasetId}", jobId, dataset.DatasetId);
                    continue;
                }

                var records = await LoadCsvAsync(dataset.LocalPath);
                sourceData[dataset.DatasetId] = records;
                _logger.LogInformation("Loaded source dataset. JobId={JobId} DatasetId={DatasetId} RecordCount={Count}",
                    jobId, dataset.DatasetId, records.Count);
            }

            if (!sourceData.Any())
            {
                _logger.LogWarning("No source data loaded. JobId={JobId}", jobId);
                return Enumerable.Empty<CanonicalRecord>();
            }

            List<CanonicalRecord> canonicalRecords;

            if (sourceData.Count == 1)
            {
                // Single source - direct mapping
                var (datasetId, records) = sourceData.First();
                canonicalRecords = records.Select((r, i) => new CanonicalRecord
                {
                    RowIndex = i,
                    Fields = r.ToDictionary(kv => $"{datasetId}.{kv.Key}", kv => (object?)kv.Value),
                    SourceDatasets = new List<string> { datasetId }
                }).ToList();
            }
            else
            {
                // Multi-source - join based on relationships
                canonicalRecords = BuildJoinedRecords(sourceData, config);
            }

            sw.Stop();
            _logger.LogInformation("Canonical model built. JobId={JobId} RecordCount={Count} Duration={Duration}ms",
                jobId, canonicalRecords.Count, sw.ElapsedMilliseconds);

            await PersistCanonicalModelAsync(jobId, canonicalRecords);
            return canonicalRecords;
        }

        public async Task PersistCanonicalModelAsync(string jobId, IEnumerable<CanonicalRecord> records)
        {
            await _artifactService.PersistArtifactAsync(jobId, records.ToList(), ArtifactType.TransformationOutput, "canonical_records.json");
        }

        private static async Task<List<Dictionary<string, string>>> LoadCsvAsync(string filePath)
        {
            var records = new List<Dictionary<string, string>>();
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                BadDataFound = null
            };

            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, config);

            await csv.ReadAsync();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();

            while (await csv.ReadAsync())
            {
                var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                    record[headers[i]] = csv.GetField(i) ?? string.Empty;
                records.Add(record);
            }

            return records;
        }

        private static List<CanonicalRecord> BuildJoinedRecords(
            Dictionary<string, List<Dictionary<string, string>>> sourceData,
            CanonicalMappingConfig config)
        {
            var canonicalRecords = new List<CanonicalRecord>();
            // Choose the primary dataset as the one with the most records (robust for typical ingestion ordering)
            var primaryDatasetId = sourceData.OrderByDescending(kv => kv.Value?.Count ?? 0).First().Key;
            var primaryRecords = sourceData[primaryDatasetId];
            int rowIndex = 0;

            foreach (var primaryRecord in primaryRecords)
            {
                var canonical = new CanonicalRecord
                {
                    RowIndex = rowIndex++,
                    SourceDatasets = new List<string> { primaryDatasetId }
                };

                // Add primary dataset fields
                foreach (var kv in primaryRecord)
                    canonical.Fields[$"{primaryDatasetId}.{kv.Key}"] = kv.Value;

                // Join with other datasets
                foreach (var rel in config.Relationships.Where(r =>
                    r.LeftDataset == primaryDatasetId || r.RightDataset == primaryDatasetId))
                {
                    var otherDatasetId = rel.LeftDataset == primaryDatasetId ? rel.RightDataset : rel.LeftDataset;
                    var primaryJoinField = rel.LeftDataset == primaryDatasetId ? rel.LeftField : rel.RightField;
                    var otherJoinField = rel.LeftDataset == primaryDatasetId ? rel.RightField : rel.LeftField;

                    if (!sourceData.TryGetValue(otherDatasetId, out var otherRecords)) continue;

                    primaryRecord.TryGetValue(primaryJoinField, out var joinValue);
                    var matchedRecord = otherRecords.FirstOrDefault(r =>
                        r.TryGetValue(otherJoinField, out var v) && v == joinValue);

                    if (matchedRecord != null)
                    {
                        foreach (var kv in matchedRecord)
                            canonical.Fields[$"{otherDatasetId}.{kv.Key}"] = kv.Value;
                        canonical.SourceDatasets.Add(otherDatasetId);
                    }
                    else if (rel.RelationshipType == RelationshipType.INNER_JOIN)
                    {
                        // INNER_JOIN - skip record if no match
                        goto nextRecord;
                    }
                }

                canonicalRecords.Add(canonical);
                nextRecord:;
            }

            return canonicalRecords;
        }
    }
}
