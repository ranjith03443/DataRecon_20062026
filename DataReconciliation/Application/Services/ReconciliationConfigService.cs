using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    /// <summary>
    /// Enhancement 2: Manages reconciliation configuration.
    /// Automatically suggests reconciliation fields based on primary keys,
    /// business keys, and high-confidence mappings. Allows user customization.
    /// </summary>
    public class ReconciliationConfigService : IReconciliationConfigService
    {
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly ILogger<ReconciliationConfigService> _logger;

        // Fields that suggest primary/business key nature
        private static readonly HashSet<string> PrimaryKeyIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "ID", "KEY", "PK", "PRIMARY", "ACCOUNT", "ACCT", "CIN", "CUSTOMER", "CUST",
            "CLIENT", "REFERENCE", "REF", "NUMBER", "NUM", "NO", "LOAN", "POLICY"
        };

        // Fields that suggest important business attributes for reconciliation
        private static readonly HashSet<string> BusinessFieldIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "AMOUNT", "AMT", "BALANCE", "BAL", "STATUS", "TYPE", "CODE", "DATE", "TOTAL",
            "PREMIUM", "PAYMENT", "RATE", "FREQUENCY", "TERM"
        };

        public ReconciliationConfigService(
            IArtifactPersistenceService artifactPersistence,
            ILogger<ReconciliationConfigService> logger)
        {
            _artifactPersistence = artifactPersistence;
            _logger = logger;
        }

        public async Task<ReconciliationConfig> SuggestReconciliationFieldsAsync(string jobId)
        {
            _logger.LogInformation("Suggesting reconciliation fields. JobId={JobId}", jobId);

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, ArtifactType.TargetMetadataProfile);

            var config = new ReconciliationConfig
            {
                JobId = jobId,
                ConfiguredAt = DateTime.UtcNow
            };

            if (finalMapping == null || targetProfile == null)
            {
                _logger.LogWarning("Missing artifacts for reconciliation suggestion. JobId={JobId}", jobId);
                return config;
            }

            var suggestions = new List<ReconciliationFieldConfig>();
            int priority = 1;

            // 1. Prioritize Primary Key fields
            foreach (var mapping in finalMapping.Mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.SourceField) && m.SourceField != "[UNRESOLVED]")
                .OrderByDescending(m => m.Confidence))
            {
                var targetField = targetProfile.Fields
                    .FirstOrDefault(f => f.FieldName.Equals(mapping.TargetField, StringComparison.OrdinalIgnoreCase));

                if (targetField == null) continue;

                var isPrimaryKey = IsPrimaryKeyField(mapping.TargetField, mapping.SourceField);
                var isBusinessKey = IsBusinessKeyField(mapping.TargetField, mapping.SourceField);
                var isHighConfidence = mapping.Confidence >= 0.90;

                string reason;
                string validationType;
                if (isPrimaryKey)
                {
                    reason = "Primary Key";
                    validationType = "COUNT";
                }
                else if (isBusinessKey)
                {
                    reason = "Business Key";
                    // Financial fields get SUM, others get COUNT
                    var isFinancial = BusinessFieldIndicators
                        .Where(t => new[] { "AMOUNT", "AMT", "BALANCE", "BAL", "TOTAL", "PREMIUM", "PAYMENT" }.Contains(t))
                        .Any(t => mapping.TargetField.Contains(t, StringComparison.OrdinalIgnoreCase)
                            || (mapping.SourceField?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
                    validationType = isFinancial ? "SUM" : "COUNT";
                }
                else if (isHighConfidence)
                {
                    reason = "High Confidence Mapping";
                    validationType = "COUNT";
                }
                else
                    continue;

                suggestions.Add(new ReconciliationFieldConfig
                {
                    FieldName = mapping.TargetField,
                    Priority = priority++,
                    IsMandatory = isPrimaryKey,
                    Reason = reason,
                    Confidence = mapping.Confidence,
                    ValidationType = validationType
                });
            }

            config.Fields = suggestions.Take(15).ToList(); // Limit suggestions to top 15
            config.ReconciliationKeys = config.Fields
                .Where(f => f.IsMandatory || f.Priority <= 5)
                .Select(f => f.FieldName)
                .ToList();

            // Persist the suggested config
            await _artifactPersistence.PersistArtifactAsync(
                jobId, config, ArtifactType.ReconciliationConfig, "reconciliation_config.json");

            _logger.LogInformation(
                "Reconciliation fields suggested. JobId={JobId} Fields={Count} Keys={Keys}",
                jobId, config.Fields.Count, config.ReconciliationKeys.Count);

            return config;
        }

        public async Task<ReconciliationConfig?> LoadConfigAsync(string jobId)
        {
            return await _artifactPersistence.LoadArtifactAsync<ReconciliationConfig>(
                jobId, ArtifactType.ReconciliationConfig);
        }

        public async Task SaveConfigAsync(string jobId, ReconciliationConfig config)
        {
            config.JobId = jobId;
            config.ConfiguredAt = DateTime.UtcNow;
            config.ReconciliationKeys = config.Fields
                .OrderBy(f => f.Priority)
                .Select(f => f.FieldName)
                .ToList();

            await _artifactPersistence.PersistArtifactAsync(
                jobId, config, ArtifactType.ReconciliationConfig, "reconciliation_config.json");

            _logger.LogInformation(
                "Reconciliation config saved. JobId={JobId} Fields={Count}",
                jobId, config.Fields.Count);
        }

        public async Task<ReconciliationDashboardDto> GetDashboardAsync(string jobId)
        {
            var config = await LoadConfigAsync(jobId);
            var reconResult = await _artifactPersistence.LoadArtifactAsync<ReconciliationResult>(
                jobId, ArtifactType.ReconciliationResult);
            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, ArtifactType.TargetMetadataProfile);

            var totalTargetFields = targetProfile?.Fields.Count ?? 0;
            var fieldsSelected = config?.Fields.Count ?? 0;
            var coveragePercent = totalTargetFields > 0
                ? Math.Round((double)fieldsSelected / totalTargetFields * 100, 1)
                : 0.0;

            return new ReconciliationDashboardDto
            {
                FieldsSelected = fieldsSelected,
                CoveragePercent = coveragePercent,
                MatchedRecords = reconResult?.MatchedRecords ?? 0,
                FailedRecords = reconResult?.MismatchedRecords ?? 0,
                TotalRecords = reconResult?.TotalSourceRecords ?? 0,
                ConfiguredFields = config?.Fields
                    .Select(f => new ReconciliationFieldDto
                    {
                        FieldName = f.FieldName,
                        Priority = f.Priority,
                        IsMandatory = f.IsMandatory,
                        Reason = f.Reason,
                        Confidence = f.Confidence,
                        ValidationType = f.ValidationType
                    }).ToList() ?? new List<ReconciliationFieldDto>()
            };
        }

        private static bool IsPrimaryKeyField(string targetField, string sourceField)
        {
            var combined = $"{targetField}_{sourceField}".ToUpperInvariant();
            return PrimaryKeyIndicators.Any(indicator =>
                combined.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBusinessKeyField(string targetField, string sourceField)
        {
            var combined = $"{targetField}_{sourceField}".ToUpperInvariant();
            return BusinessFieldIndicators.Any(indicator =>
                combined.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }
    }
}
