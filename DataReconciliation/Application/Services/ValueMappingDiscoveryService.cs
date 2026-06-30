using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    /// <summary>
    /// Deterministic service that scans canonical records for each mapped source field,
    /// extracts distinct values, and flags fields with &lt;= threshold distinct values as value-mapping candidates.
    /// Enhancement 4: Excludes identifier fields and uses configurable threshold.
    /// Result is stored as value_mapping_candidates.json in the job artifacts folder.
    /// </summary>
    public class ValueMappingDiscoveryService : IValueMappingDiscoveryService
    {
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly ILogger<ValueMappingDiscoveryService> _logger;
        private readonly int _maxDistinctValuesForCandidate;

        /// <summary>
        /// Enhancement 4: Identifier field patterns that should NEVER be value mapping candidates.
        /// </summary>
        private static readonly string[] IdentifierPatterns = new[]
        {
            "ID", "KEY", "REFERENCE", "REF", "ACCOUNT", "ACCOUNT_NUMBER", "ACCT",
            "CUSTOMER", "CUST", "CLIENT", "NUMBER", "NUM"
        };

        public ValueMappingDiscoveryService(
            IArtifactPersistenceService artifactPersistence,
            ILogger<ValueMappingDiscoveryService> logger,
            IConfiguration configuration)
        {
            _artifactPersistence = artifactPersistence;
            _logger = logger;
            _maxDistinctValuesForCandidate = configuration.GetValue<int>("ValueMappingThreshold", 20);
        }

        /// <summary>
        /// Enhancement 4: Determines if a field is an identifier field that should be excluded from value mapping.
        /// </summary>
        public static bool IsIdentifierField(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return false;

            var upperFieldName = fieldName.ToUpperInvariant().Replace("-", "_");

            foreach (var pattern in IdentifierPatterns)
            {
                // Check if field name contains the identifier pattern as a word boundary
                // e.g., CUSTOMER_ID matches "ID" and "CUSTOMER"
                var parts = upperFieldName.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => part.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Also check if the entire field ends with the pattern
                if (upperFieldName.EndsWith($"_{pattern}") || upperFieldName.EndsWith(pattern))
                {
                    // Only match if it's a word boundary (not part of another word)
                    if (upperFieldName == pattern || upperFieldName.EndsWith($"_{pattern}"))
                        return true;
                }
            }

            return false;
        }

        public async Task<ValueMappingCandidatesDocument> DiscoverAsync(string jobId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Value mapping discovery started. JobId={JobId} Threshold={Threshold}", jobId, _maxDistinctValuesForCandidate);

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            var canonicalRecords = await _artifactPersistence.LoadArtifactAsync<List<CanonicalRecord>>(
                jobId, ArtifactType.TransformationOutput);

            // Also try loading canonical_records.json from the same artifact folder
            if (canonicalRecords == null || !canonicalRecords.Any())
            {
                canonicalRecords = await _artifactPersistence.LoadArtifactByNameAsync<List<CanonicalRecord>>(
                    jobId, "canonical_records.json");
            }

            var doc = new ValueMappingCandidatesDocument
            {
                JobId = jobId,
                DiscoveredAt = DateTime.UtcNow
            };

            if (finalMapping == null)
            {
                _logger.LogWarning("No final mapping config found for discovery. JobId={JobId}", jobId);
                return doc;
            }

            // Get existing value_mappings to know which fields already have rules configured
            var existingValueMappings = await _artifactPersistence.LoadArtifactAsync<ValueMappingsDocument>(
                jobId, ArtifactType.ValueMappings);
            var existingMappedFields = existingValueMappings?.Fields
                .ToDictionary(f => f.TargetField, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ValueMappingFieldDocument>(StringComparer.OrdinalIgnoreCase);

            // Also load source schema profiles to get distinct values from profiling step
            var manifest = await _artifactPersistence.LoadArtifactAsync<DatasetManifest>(
                jobId, ArtifactType.DatasetManifest);

            var sourceFieldDistinctValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (manifest != null)
            {
                foreach (var dataset in manifest.Datasets.Where(d => d.DatasetRole == DatasetRole.SOURCE))
                {
                    var profile = await _artifactPersistence.LoadArtifactByNameAsync<SourceSchemaProfile>(
                        jobId, $"source_schema_profile_{dataset.DatasetId}.json");
                    if (profile != null)
                    {
                        foreach (var field in profile.Fields.Where(f => f.DistinctValues.Any()))
                        {
                            var key = $"{dataset.DatasetId}.{field.FieldName}";
                            sourceFieldDistinctValues[key] = field.DistinctValues;
                            sourceFieldDistinctValues[field.FieldName] = field.DistinctValues; // unqualified key too
                        }
                    }
                }
            }

            foreach (var mapping in finalMapping.Mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.SourceField) && m.SourceField != "[UNRESOLVED]"))
            {
                // Enhancement 4: Check if field is an identifier — skip if so
                var isIdentifier = IsIdentifierField(mapping.SourceField) || IsIdentifierField(mapping.TargetField);
                if (isIdentifier)
                {
                    _logger.LogDebug(
                        "Field excluded from value mapping (Identifier). JobId={JobId} Source={Source} Target={Target}",
                        jobId, mapping.SourceField, mapping.TargetField);
                    continue;
                }

                // Collect distinct values from canonical records
                var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (canonicalRecords != null)
                {
                    foreach (var record in canonicalRecords)
                    {
                        string? val = null;
                        var qualifiedKey = string.IsNullOrEmpty(mapping.SourceDataset)
                            ? mapping.SourceField
                            : $"{mapping.SourceDataset}.{mapping.SourceField}";

                        if (record.Fields.TryGetValue(qualifiedKey, out var qv)) val = qv?.ToString();
                        else if (record.Fields.TryGetValue(mapping.SourceField, out var sv)) val = sv?.ToString();

                        if (!string.IsNullOrWhiteSpace(val))
                            distinctValues.Add(val.Trim());
                    }
                }

                // Fall back to schema profile distinct values if canonical records not available
                if (!distinctValues.Any())
                {
                    var profileKey = string.IsNullOrEmpty(mapping.SourceDataset)
                        ? mapping.SourceField
                        : $"{mapping.SourceDataset}.{mapping.SourceField}";

                    if (sourceFieldDistinctValues.TryGetValue(profileKey, out var profileVals))
                        foreach (var v in profileVals) distinctValues.Add(v);
                    else if (sourceFieldDistinctValues.TryGetValue(mapping.SourceField, out var pv2))
                        foreach (var v in pv2) distinctValues.Add(v);
                }

                // Enhancement 4: Use configurable threshold
                if (!distinctValues.Any() || distinctValues.Count > _maxDistinctValuesForCandidate)
                {
                    if (distinctValues.Any())
                    {
                        _logger.LogDebug(
                            "Field excluded from value mapping (Exceeds Threshold). JobId={JobId} Source={Source} DistinctCount={Count} Threshold={Threshold}",
                            jobId, mapping.SourceField, distinctValues.Count, _maxDistinctValuesForCandidate);
                    }
                    continue;
                }

                // Determine how many rules are already configured in the final_mapping_config
                var existingRules = mapping.Transformations
                    .FirstOrDefault(t => string.Equals(t.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));
                var configuredRuleCount = existingRules?.Rules?.Count ?? 0;

                // Build suggested mappings from existing configured rules
                var suggestedMappings = existingRules?.Rules?
                    .Select(kv => new ValueMappingCandidateRule
                    {
                        SourceValue = kv.Key,
                        TargetValue = kv.Value,
                        Confidence = 1.0,
                        IsAiSuggested = false
                    }).ToList() ?? new List<ValueMappingCandidateRule>();

                var covered = suggestedMappings.Count(r => !string.IsNullOrWhiteSpace(r.TargetValue));
                var coverage = distinctValues.Count > 0
                    ? Math.Round((double)covered / distinctValues.Count * 100, 1)
                    : 0;

                var status = configuredRuleCount >= distinctValues.Count ? "Complete"
                    : configuredRuleCount > 0 ? "Partial"
                    : "Pending";

                doc.Candidates.Add(new ValueMappingCandidateEntry
                {
                    SourceField = mapping.SourceField,
                    SourceDataset = mapping.SourceDataset,
                    TargetField = mapping.TargetField,
                    DistinctSourceValues = distinctValues.OrderBy(v => v).ToList(),
                    DistinctValueCount = distinctValues.Count,
                    Status = status,
                    SuggestedMappings = suggestedMappings,
                    AiConfidence = 0
                });

                _logger.LogDebug(
                    "Value mapping candidate found. JobId={JobId} Source={Source} Target={Target} DistinctCount={Count} Status={Status}",
                    jobId, mapping.SourceField, mapping.TargetField, distinctValues.Count, status);
            }

            await _artifactPersistence.PersistArtifactAsync(
                jobId, doc, ArtifactType.ValueMappingCandidates, "value_mapping_candidates.json");

            sw.Stop();
            _logger.LogInformation(
                "Value mapping discovery completed. JobId={JobId} Candidates={Count} Duration={Duration}ms Threshold={Threshold}",
                jobId, doc.Candidates.Count, sw.ElapsedMilliseconds, _maxDistinctValuesForCandidate);

            return doc;
        }
    }
}
