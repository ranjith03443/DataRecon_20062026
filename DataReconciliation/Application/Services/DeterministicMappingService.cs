using CsvHelper;
using CsvHelper.Configuration;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class DeterministicMappingService : IDeterministicMappingService
    {
        private readonly ILogger<DeterministicMappingService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        // Abbreviation map — loaded from config/abbreviation_map.json at startup.
        // Edit that file to add/remove entries or blank it out ({}) to disable abbreviation matching.
        // Falls back to built-in defaults if the file is absent or unreadable.
        private readonly Dictionary<string, string[]> _abbreviationMap;

        private static readonly Dictionary<string, string[]> _builtInAbbreviationMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // NOTE: The built-in fallback is intentionally empty.
        // All abbreviations are controlled exclusively via config/abbreviation_map.json.
        // An empty or missing file means abbreviation matching is fully disabled.

        public DeterministicMappingService(
            ILogger<DeterministicMappingService> logger,
            IArtifactPersistenceService artifactService,
            IHostEnvironment hostEnvironment)
        {
            _logger = logger;
            _artifactService = artifactService;
            _abbreviationMap = LoadAbbreviationMapFromFile(logger, hostEnvironment.ContentRootPath);
        }

        /// <summary>
        /// Loads abbreviation_map.json from config/ under ContentRootPath.
        /// ContentRootPath is the project directory in development and the publish directory in production,
        /// so edits take effect on next restart without needing a rebuild.
        /// Set the file to {} to disable all abbreviation matching during testing.
        /// Falls back to built-in defaults only if the file is completely absent or corrupted.
        /// </summary>
        private static Dictionary<string, string[]> LoadAbbreviationMapFromFile(ILogger logger, string contentRootPath)
        {
            var configPath = Path.Combine(contentRootPath, "config", "abbreviation_map.json");
            if (!File.Exists(configPath))
            {
                logger.LogDebug("Abbreviation map not found at {Path}. Using built-in defaults.", configPath);
                return new Dictionary<string, string[]>(_builtInAbbreviationMap, StringComparer.OrdinalIgnoreCase);
            }
            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                    json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,   // tolerate trailing commas from hand-edited JSON
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
                    });

                if (loaded == null || loaded.Count == 0)
                {
                    logger.LogInformation("Abbreviation map at {Path} is empty — abbreviation matching disabled.", configPath);
                    return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                }

                logger.LogInformation("Loaded abbreviation map from {Path} ({Count} entries).", configPath, loaded.Count);
                return new Dictionary<string, string[]>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read abbreviation map from {Path}. Using built-in defaults.", configPath);
                return new Dictionary<string, string[]>(_builtInAbbreviationMap, StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task<MappingCandidatesDocument> GenerateMappingCandidatesAsync(
            string jobId,
            IEnumerable<SourceSchemaProfile> sourceProfiles,
            TargetMetadataProfile targetProfile,
            IEnumerable<SemanticSchemaProfile>? semanticProfiles = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting deterministic mapping. JobId={JobId}", jobId);

            // Deterministic flow policy:
            // 1) Apply user-curated historical mappings first.
            // 2) Then apply strict exact source-target field-name matches (case-insensitive), one-to-one.
            // No semantic, normalized, abbreviation, partial, or fuzzy logic.
            _ = semanticProfiles; // explicitly unused in strict deterministic mode

            var doc = new MappingCandidatesDocument { JobId = jobId, GeneratedAt = DateTime.UtcNow };
            var historicallyMappedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── Step 1: Apply historical mappings (highest priority) ───────────
            var manifest = await _artifactService.LoadArtifactAsync<DatasetManifest>(jobId, ArtifactType.DatasetManifest);
            if (manifest != null)
            {
                var historicalDatasets = manifest.Datasets
                    .Where(d => d.DatasetRole == DatasetRole.HISTORICAL_MAPPINGS && File.Exists(d.LocalPath));

                foreach (var dataset in historicalDatasets)
                {
                    _logger.LogInformation("Applying historical mappings. JobId={JobId} File={File}", jobId, dataset.FileName);
                    var historicalMappings = LoadHistoricalMappingsCsv(dataset.LocalPath);

                    foreach (var hm in historicalMappings)
                    {
                        // Match to a known target field (case-insensitive)
                        var targetField = targetProfile.Fields
                            .FirstOrDefault(t => string.Equals(t.FieldName, hm.TargetField, StringComparison.OrdinalIgnoreCase));
                        if (targetField == null) continue;

                        // Match to a known source field across all source profiles
                        var sourceDatasetId = sourceProfiles
                            .FirstOrDefault(p => p.Fields.Any(f =>
                                string.Equals(f.FieldName, hm.SourceField, StringComparison.OrdinalIgnoreCase)))
                            ?.DatasetId ?? string.Empty;

                        doc.Mappings.Add(new MappingCandidate
                        {
                            SourceField        = hm.SourceField,
                            SourceDataset      = sourceDatasetId,
                            TargetField        = targetField.FieldName,
                            MatchType          = "historical",
                            Confidence         = hm.Confidence,
                            Status             = hm.Confidence >= 0.90 ? MappingStatus.AUTO_MATCHED : MappingStatus.MANUAL_REVIEW_REQUIRED,
                            TransformationRule = hm.Transformation,
                            Notes              = hm.Notes
                        });

                        historicallyMappedTargets.Add(targetField.FieldName);
                        _logger.LogInformation("Historical mapping applied. JobId={JobId} Source={Source} Target={Target} Confidence={Confidence}",
                            jobId, hm.SourceField, targetField.FieldName, hm.Confidence);
                    }
                }
            }

            // ── Step 2: Strict deterministic matching (exact one-to-one only) ─
            foreach (var sourceProfile in sourceProfiles)
            {
                foreach (var sourceField in sourceProfile.Fields)
                {
                    var bestMatch = FindBestMatch(sourceField, targetProfile.Fields, sourceProfile.DatasetId,
                        excludeTargets: historicallyMappedTargets);
                    if (bestMatch != null)
                    {
                        doc.Mappings.Add(bestMatch);
                        _logger.LogDebug("Mapping found. JobId={JobId} Source={Source} Target={Target} Confidence={Confidence} MatchType={MatchType}",
                            jobId, sourceField.FieldName, bestMatch.TargetField, bestMatch.Confidence, bestMatch.MatchType);
                    }
                }
            }

            // Find unresolved target fields (no mapping)
            var mappedTargetFields = doc.Mappings.Select(m => m.TargetField).ToHashSet();
            foreach (var targetField in targetProfile.Fields)
            {
                if (!mappedTargetFields.Contains(targetField.FieldName))
                {
                    _logger.LogWarning("Target field unresolved. JobId={JobId} TargetField={TargetField}", jobId, targetField.FieldName);
                }
            }

            sw.Stop();
            _logger.LogInformation("Deterministic mapping completed. JobId={JobId} Mappings={Count} Duration={Duration}ms",
                jobId, doc.Mappings.Count, sw.ElapsedMilliseconds);

            await PersistMappingCandidatesAsync(jobId, doc);
            return doc;
        }

        public async Task PersistMappingCandidatesAsync(string jobId, MappingCandidatesDocument candidates)
        {
            await _artifactService.PersistArtifactAsync(jobId, candidates, ArtifactType.MappingCandidates, "mapping_candidates.json");
        }

        private static List<HistoricalMappingRecord> LoadHistoricalMappingsCsv(string filePath)
        {
            var records = new List<HistoricalMappingRecord>();
            try
            {
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    BadDataFound = null,
                    HeaderValidated = null
                };
                using var reader = new StreamReader(filePath);
                using var csv = new CsvReader(reader, config);
                csv.Context.RegisterClassMap<HistoricalMappingRecordMap>();
                records = csv.GetRecords<HistoricalMappingRecord>().ToList();
            }
            catch (Exception ex)
            {
                // Log is not available in static context; swallow and return empty
                _ = ex;
            }
            return records;
        }

        private MappingCandidate? FindBestMatch(SourceFieldProfile sourceField, IEnumerable<TargetFieldMetadata> targetFields,
            string sourceDataset, HashSet<string>? excludeTargets = null)
        {
            foreach (var targetField in targetFields)
            {
                if (excludeTargets != null && excludeTargets.Contains(targetField.FieldName)) continue;

                // Exact field-name match only (case-insensitive)
                if (string.Equals(sourceField.FieldName, targetField.FieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return new MappingCandidate
                    {
                        SourceField = sourceField.FieldName,
                        SourceDataset = sourceDataset,
                        TargetField = targetField.FieldName,
                        MatchType = "exact_match",
                        Confidence = 1.0,
                        Status = MappingStatus.AUTO_MATCHED
                    };
                }
            }

            return null;
        }

        private (double confidence, string matchType) ComputeMatchScore(string source, string target)
        {
            // 1. Exact match
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return (1.0, "exact_match");

            // 2. Normalized match (remove underscores)
            var normSource = Normalize(source);
            var normTarget = Normalize(target);
            if (string.Equals(normSource, normTarget, StringComparison.OrdinalIgnoreCase))
                return (0.98, "normalized_match");

            // 3. Abbreviation expansion
            var expandedSource = ExpandAbbreviations(source);
            var expandedTarget = ExpandAbbreviations(target);
            if (string.Equals(expandedSource, expandedTarget, StringComparison.OrdinalIgnoreCase))
                return (0.92, "normalized_abbreviation");

            // 4. Partial match - source contains target or vice versa
            if (normSource.Contains(normTarget, StringComparison.OrdinalIgnoreCase) ||
                normTarget.Contains(normSource, StringComparison.OrdinalIgnoreCase))
                return (0.80, "partial_match");

            // 5. Fuzzy similarity
            var similarity = ComputeJaccardSimilarity(normSource, normTarget);
            if (similarity >= 0.60)
                return (similarity * 0.85, "fuzzy_match");

            return (0, "no_match");
        }

        private static string Normalize(string field) =>
            Regex.Replace(field.ToUpperInvariant(), @"[_\-\s]", "");

        private string ExpandAbbreviations(string field)
        {
            var parts = field.ToUpperInvariant().Split('_', '-', ' ');
            var expanded = parts.Select(p =>
            {
                foreach (var kv in _abbreviationMap)
                    if (kv.Value.Contains(p, StringComparer.OrdinalIgnoreCase))
                        return kv.Key;
                return p;
            });
            return string.Join("", expanded);
        }

        private static double ComputeJaccardSimilarity(string s1, string s2)
        {
            var set1 = new HashSet<char>(s1);
            var set2 = new HashSet<char>(s2);
            var intersection = set1.Intersect(set2).Count();
            var union = set1.Union(set2).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }
    }

    // ─── Historical Mapping CSV record ────────────────────────────────────────
    public class HistoricalMappingRecord
    {
        public string SourceField    { get; set; } = string.Empty;
        public string TargetField    { get; set; } = string.Empty;
        public string Transformation { get; set; } = "DIRECT";
        public double Confidence     { get; set; } = 1.0;
        public string Notes          { get; set; } = string.Empty;
    }

    public class HistoricalMappingRecordMap : CsvHelper.Configuration.ClassMap<HistoricalMappingRecord>
    {
        public HistoricalMappingRecordMap()
        {
            Map(m => m.SourceField)    .Name("SOURCE_FIELD");
            Map(m => m.TargetField)    .Name("TARGET_FIELD");
            Map(m => m.Transformation) .Name("TRANSFORMATION").Optional();
            Map(m => m.Confidence)     .Name("CONFIDENCE").Optional();
            Map(m => m.Notes)          .Name("NOTES").Optional();
        }
    }
}
