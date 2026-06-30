using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    /// <summary>
    /// Enhancement 3: Manages transformation overrides.
    /// Allows users to override AI-suggested transformations with their own selections
    /// from a predefined transformation catalog, or specify custom expressions.
    /// </summary>
    public class TransformationOverrideService : ITransformationOverrideService
    {
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly ILogger<TransformationOverrideService> _logger;

        /// <summary>
        /// The catalog of available transformation operations.
        /// </summary>
        public static readonly string[] TransformationCatalog = new[]
        {
            "DIRECT",
            "DATE_YYYYMMDD",
            "DATE_DDMMYYYY",
            "DATE_MMDDYYYY",
            "DATE_DD/MM/YYYY",
            "DATE_MM/DD/YYYY",
            "DATE_YYYY/MM/DD",
            "LEFT_PAD",
            "RIGHT_PAD",
            "TRUNCATE",
            "UPPERCASE",
            "LOWERCASE",
            "CONCAT",
            "VALUE_MAPPING",
            "HARDCODED",
            "CUSTOM"
        };

        public TransformationOverrideService(
            IArtifactPersistenceService artifactPersistence,
            ILogger<TransformationOverrideService> logger)
        {
            _artifactPersistence = artifactPersistence;
            _logger = logger;
        }

        public async Task<List<TransformationOverrideDto>> GetOverridesAsync(string jobId)
        {
            _logger.LogInformation("Loading transformation overrides. JobId={JobId}", jobId);

            // Load existing overrides if any
            var overrideConfig = await _artifactPersistence.LoadArtifactAsync<TransformationOverrideConfig>(
                jobId, ArtifactType.TransformationOverrides);

            if (overrideConfig != null)
            {
                return overrideConfig.Overrides.Select(o => new TransformationOverrideDto
                {
                    TargetField = o.TargetField,
                    SourceField = o.SourceField,
                    SuggestedTransformation = o.SuggestedTransformation,
                    Confidence = o.Confidence,
                    OverrideTransformation = o.OverrideTransformation,
                    CustomExpression = o.CustomExpression,
                    Status = o.Status
                }).ToList();
            }

            // Build from final mapping config if no overrides yet
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
                return new List<TransformationOverrideDto>();

            var overrides = finalMapping.Mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.SourceField) && m.SourceField != "[UNRESOLVED]")
                .Select(m =>
                {
                    var primaryTransform = m.Transformations
                        .FirstOrDefault(t => !string.Equals(t.Operation, "FIXED_WIDTH_FORMATTING", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(t.Operation, "HARDCODED_VALUE", StringComparison.OrdinalIgnoreCase));

                    var suggestedOp = primaryTransform?.Operation ?? "DIRECT";

                    return new TransformationOverrideDto
                    {
                        TargetField = m.TargetField,
                        SourceField = m.SourceField,
                        SuggestedTransformation = suggestedOp,
                        Confidence = m.Confidence,
                        OverrideTransformation = string.Empty,
                        CustomExpression = null,
                        Status = "AI_Suggested"
                    };
                })
                .OrderBy(o => o.TargetField)
                .ToList();

            return overrides;
        }

        public async Task SaveOverridesAsync(string jobId, List<TransformationOverrideDto> overrides)
        {
            _logger.LogInformation("Saving transformation overrides. JobId={JobId} Count={Count}", jobId, overrides.Count);

            var config = new TransformationOverrideConfig
            {
                JobId = jobId,
                ApprovedAt = DateTime.UtcNow,
                Overrides = overrides.Select(o => new TransformationOverrideEntry
                {
                    TargetField = o.TargetField,
                    SourceField = o.SourceField,
                    SuggestedTransformation = o.SuggestedTransformation,
                    Confidence = o.Confidence,
                    OverrideTransformation = o.OverrideTransformation,
                    CustomExpression = o.CustomExpression,
                    Status = !string.IsNullOrWhiteSpace(o.OverrideTransformation) ? "User_Override" : o.Status
                }).ToList()
            };

            await _artifactPersistence.PersistArtifactAsync(
                jobId, config, ArtifactType.TransformationOverrides, "transformation_overrides.json");

            _logger.LogInformation("Transformation overrides saved. JobId={JobId}", jobId);
        }

        public async Task ApplyOverridesToFinalMappingAsync(string jobId)
        {
            var overrideConfig = await _artifactPersistence.LoadArtifactAsync<TransformationOverrideConfig>(
                jobId, ArtifactType.TransformationOverrides);

            if (overrideConfig == null || !overrideConfig.Overrides.Any())
            {
                _logger.LogInformation("No overrides to apply. JobId={JobId}", jobId);
                return;
            }

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
            {
                _logger.LogWarning("No final mapping config found for override application. JobId={JobId}", jobId);
                return;
            }

            int applied = 0;
            foreach (var overrideEntry in overrideConfig.Overrides
                .Where(o => !string.IsNullOrWhiteSpace(o.OverrideTransformation)))
            {
                var mapping = finalMapping.Mappings.FirstOrDefault(m =>
                    m.TargetField.Equals(overrideEntry.TargetField, StringComparison.OrdinalIgnoreCase));

                if (mapping == null) continue;

                // Remove existing non-fixed transformations and apply override
                mapping.Transformations.RemoveAll(t =>
                    !string.Equals(t.Operation, "FIXED_WIDTH_FORMATTING", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(t.Operation, "HARDCODED_VALUE", StringComparison.OrdinalIgnoreCase));

                var newRule = BuildTransformationRule(overrideEntry.OverrideTransformation, overrideEntry.CustomExpression);
                if (newRule != null)
                {
                    mapping.Transformations.Insert(0, newRule);
                    applied++;
                }
            }

            if (applied > 0)
            {
                await _artifactPersistence.PersistArtifactAsync(
                    jobId, finalMapping, ArtifactType.FinalMappingConfig, "final_mapping_config.json");

                _logger.LogInformation(
                    "Transformation overrides applied to final mapping. JobId={JobId} Applied={Count}",
                    jobId, applied);
            }
        }

        private static TransformationRule? BuildTransformationRule(string operation, string? customExpression)
        {
            return operation.ToUpperInvariant() switch
            {
                "DIRECT" => null, // No transformation needed
                "DATE_YYYYMMDD" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "yyyyMMdd" },
                "DATE_DDMMYYYY" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "ddMMyyyy" },
                "DATE_MMDDYYYY" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "MMddyyyy" },
                "DATE_DD/MM/YYYY" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "dd/MM/yyyy" },
                "DATE_MM/DD/YYYY" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "MM/dd/yyyy" },
                "DATE_YYYY/MM/DD" => new TransformationRule { Operation = "DATE_FORMATTING", Format = "yyyy/MM/dd" },
                "LEFT_PAD" => new TransformationRule { Operation = "FIXED_WIDTH_FORMATTING", Alignment = "RIGHT", PadCharacter = "0" },
                "RIGHT_PAD" => new TransformationRule { Operation = "FIXED_WIDTH_FORMATTING", Alignment = "LEFT", PadCharacter = " " },
                "TRUNCATE" => new TransformationRule { Operation = "FIXED_WIDTH_FORMATTING", Alignment = "LEFT" },
                "UPPERCASE" => new TransformationRule { Operation = "UPPERCASE" },
                "LOWERCASE" => new TransformationRule { Operation = "LOWERCASE" },
                "CONCAT" => new TransformationRule
                {
                    Operation = "CONCATENATION",
                    AIParameters = !string.IsNullOrWhiteSpace(customExpression)
                        ? new Dictionary<string, string> { { "expression", customExpression } }
                        : null
                },
                "VALUE_MAPPING" => new TransformationRule { Operation = "VALUE_MAPPING", Rules = new Dictionary<string, string>() },
                "HARDCODED" => new TransformationRule
                {
                    Operation = "HARDCODED_VALUE",
                    DefaultValue = customExpression
                },
                "CUSTOM" => new TransformationRule
                {
                    Operation = "CUSTOM",
                    AIParameters = !string.IsNullOrWhiteSpace(customExpression)
                        ? new Dictionary<string, string> { { "expression", customExpression } }
                        : null
                },
                _ => new TransformationRule { Operation = operation }
            };
        }
    }
}
