using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class MappingConsolidationService : IMappingConsolidationService
    {
        private readonly ILogger<MappingConsolidationService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        public MappingConsolidationService(
            ILogger<MappingConsolidationService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
        }

        public async Task<FinalMappingConfig> ConsolidateMappingsAsync(
            string jobId,
            MappingCandidatesDocument deterministicMappings,
            AIMappingResponse aiMappings,
            TargetMetadataProfile targetProfile)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting mapping consolidation. JobId={JobId}", jobId);

            var finalConfig = new FinalMappingConfig
            {
                JobId = jobId,
                Version = "1.0",
                FinalizedAt = DateTime.UtcNow
            };

            // Index deterministic mappings by source field
            var deterministicIndex = deterministicMappings.Mappings
                .GroupBy(m => m.TargetField)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Confidence).First(),
                    StringComparer.OrdinalIgnoreCase);

            // Index AI mappings by target field
            var aiIndex = aiMappings.AiMappings
                .GroupBy(m => m.TargetField)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Confidence).First(),
                    StringComparer.OrdinalIgnoreCase);

            // Process each target field
            foreach (var targetField in targetProfile.Fields.OrderBy(f => f.ColumnOrder))
            {
                FinalMapping? finalMapping = null;

                // 1. High-confidence deterministic mapping
                if (deterministicIndex.TryGetValue(targetField.FieldName, out var detMapping) &&
                    detMapping.Confidence >= 0.90)
                {
                    finalMapping = CreateFinalMapping(detMapping, targetField);
                    _logger.LogDebug("Deterministic mapping applied. JobId={JobId} Source={Source} Target={Target} Confidence={Confidence}",
                        jobId, detMapping.SourceField, targetField.FieldName, detMapping.Confidence);
                }
                // 2. AI mapping as fallback
                else if (aiIndex.TryGetValue(targetField.FieldName, out var aiMapping) && aiMapping.Confidence >= 0.70)
                {
                    finalMapping = new FinalMapping
                    {
                        SourceField   = aiMapping.SourceField,
                        SourceDataset = aiMapping.SourceDataset,   // fix: was always blank
                        TargetField   = aiMapping.TargetField,
                        Confidence    = aiMapping.Confidence,
                        // AI-sourced mappings always use AI_MATCHED so users can distinguish them
                        // from deterministic AUTO_MATCHED even when confidence is HIGH.
                        // The confidence level is visible in the Confidence column.
                        Status        = MappingStatus.AI_MATCHED,
                        MatchSource   = "AI Inference",
                        Transformations = BuildTransformationsFromAI(aiMapping, targetField)
                    };
                    _logger.LogDebug("AI mapping applied. JobId={JobId} Source={Source} Target={Target} Confidence={Confidence}",
                        jobId, aiMapping.SourceField, targetField.FieldName, aiMapping.Confidence);
                }
                // 3. Low-confidence deterministic as last resort
                else if (deterministicIndex.TryGetValue(targetField.FieldName, out var lowDetMapping))
                {
                    finalMapping = CreateFinalMapping(lowDetMapping, targetField);
                    finalMapping.Status = MappingStatus.MANUAL_REVIEW_REQUIRED;
                    finalMapping.MatchSource = MatchTypeToLabel(lowDetMapping.MatchType);
                }
                else
                {
                    // Unresolved - mark for manual review
                    finalMapping = new FinalMapping
                    {
                        SourceField = "[UNRESOLVED]",
                        TargetField = targetField.FieldName,
                        Confidence = 0,
                        Status = MappingStatus.UNRESOLVED,
                        MatchSource = "Unresolved",
                        Transformations = new List<TransformationRule>()
                    };
                    _logger.LogWarning("Mapping unresolved. JobId={JobId} TargetField={TargetField}", jobId, targetField.FieldName);
                }

                // Apply fixed-value output rule as a transformation only.
                // This keeps mapping status/source based on actual source-target matching.
                AppendHardcodedTransformationIfConfigured(finalMapping, targetField);

                if (finalMapping != null)
                    finalConfig.Mappings.Add(finalMapping);
            }

            // Validate for duplicates
            var duplicates = finalConfig.Mappings
                .Where(m => m.SourceField != "[HARDCODED]" && m.SourceField != "[UNRESOLVED]")
                .GroupBy(m => $"{m.SourceField}:{m.TargetField}")
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Any())
                _logger.LogWarning("Duplicate mappings detected. JobId={JobId} Duplicates={Duplicates}", jobId, string.Join(", ", duplicates));

            sw.Stop();
            _logger.LogInformation("Mapping consolidation completed. JobId={JobId} FinalMappings={Count} Duration={Duration}ms",
                jobId, finalConfig.Mappings.Count, sw.ElapsedMilliseconds);

            await PersistFinalMappingConfigAsync(jobId, finalConfig);
            return finalConfig;
        }

        public async Task PersistFinalMappingConfigAsync(string jobId, FinalMappingConfig config)
        {
            await _artifactService.PersistArtifactAsync(jobId, config, ArtifactType.FinalMappingConfig, "final_mapping_config.json");
        }

        private static FinalMapping CreateFinalMapping(MappingCandidate candidate, TargetFieldMetadata targetField)
        {
            return new FinalMapping
            {
                SourceField = candidate.SourceField,
                SourceDataset = candidate.SourceDataset,
                TargetField = candidate.TargetField,
                Confidence = candidate.Confidence,
                Status = candidate.Status,
                MatchSource = MatchTypeToLabel(candidate.MatchType),
                Transformations = BuildTransformationsFromTarget(targetField)
            };
        }

        private static void AppendHardcodedTransformationIfConfigured(FinalMapping? mapping, TargetFieldMetadata targetField)
        {
            if (mapping == null || string.IsNullOrWhiteSpace(targetField.HardcodedValue))
                return;

            mapping.Transformations ??= new List<TransformationRule>();

            // Keep only one HARDCODED_VALUE rule and make it run first.
            mapping.Transformations.RemoveAll(t =>
                string.Equals(t.Operation, "HARDCODED_VALUE", StringComparison.OrdinalIgnoreCase));

            mapping.Transformations.Insert(0, new TransformationRule
            {
                Operation = "HARDCODED_VALUE",
                DefaultValue = targetField.HardcodedValue
            });
        }

        /// <summary>Converts internal MatchType codes to end-user-friendly labels.</summary>
        private static string MatchTypeToLabel(string matchType) => matchType switch
        {
            "historical"              => "Prior Mapping Data",
            "exact_match"             => "Exact Name Match",
            "normalized_match"        => "Normalised Name Match",
            "normalized_abbreviation" => "Abbreviation (Built-in Dictionary)",
            "partial_match"           => "Partial Name Match",
            "fuzzy_match"             => "Fuzzy Name Match",
            var t when t != null && t.StartsWith("semantic_exact")        => "AI-enriched Exact Match",
            var t when t != null && t.StartsWith("semantic_normalized")   => "AI-enriched Name Match",
            var t when t != null && t.StartsWith("semantic_abbreviation") => "AI-enriched Abbreviation",
            var t when t != null && t.StartsWith("semantic_")             => "AI-enriched Match",
            "hardcoded_value"         => "Fixed Value",
            _                         => "Rule-based Match"
        };

        private static readonly HashSet<string> _dateDataTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "DATE", "DATETIME", "TIMESTAMP", "TIME"
        };

        private static bool IsDateType(string datatype) =>
            _dateDataTypes.Any(d => datatype.Contains(d, StringComparison.OrdinalIgnoreCase));

        // A date-format pattern: must contain YYYY or yy AND a month token that is a standalone
        // date component (not just any occurrence of MM inside a word like VARCHAR or EMAIL).
        private static readonly System.Text.RegularExpressions.Regex _dateFmtPattern =
            new(@"(?:YYYY|yyyy|YY|yy).*(?:MM|mm)|(?:MM|mm).*(?:YYYY|yyyy|YY|yy)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static List<TransformationRule> BuildTransformationsFromTarget(TargetFieldMetadata targetField)
        {
            var rules = new List<TransformationRule>();

            if (!string.IsNullOrWhiteSpace(targetField.Format))
            {
                // Only treat as date formatting if the field's datatype is a date type
                // OR the format string matches a recognisable date pattern (contains year + month tokens)
                bool isDateFmt = IsDateType(targetField.Datatype) ||
                                 _dateFmtPattern.IsMatch(targetField.Format);
                if (isDateFmt)
                    rules.Add(new TransformationRule { Operation = "DATE_FORMATTING", Format = targetField.Format });
            }

            if (RequiresDecimalFormatting(targetField))
                rules.Add(new TransformationRule { Operation = "DECIMAL_FORMATTING", FixedWidth = targetField.FieldLength });

            if (targetField.FieldLength.HasValue)
                rules.Add(new TransformationRule
                {
                    Operation = "FIXED_WIDTH_FORMATTING",
                    FixedWidth = targetField.FieldLength,
                    PadCharacter = " ",
                    Alignment = "LEFT"
                });

            return rules;
        }

        private static bool RequiresDecimalFormatting(TargetFieldMetadata targetField)
        {
            if (!(targetField.Datatype.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                  targetField.Datatype.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var fieldName = targetField.FieldName.ToUpperInvariant();

            // Account / identifier / code / sequence fields should not be forced into decimal formatting.
            var identifierLike = new[] { "ACCT", "ACCOUNT", "KONTO", "ID", "NO", "NUM", "CODE", "REF", "SEQ" };
            if (identifierLike.Any(token => fieldName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Decimal formatting is appropriate only for measure/amount-style fields.
            var decimalLike = new[] { "AMT", "AMOUNT", "BAL", "TOTAL", "PRICE", "COST", "FEE", "RATE", "PERCENT", "QTY", "QUANTITY" };
            return decimalLike.Any(token => fieldName.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static List<TransformationRule> BuildTransformationsFromAI(AIMappingEntry aiMapping, TargetFieldMetadata targetField)
        {
            var rules = BuildTransformationsFromTarget(targetField);

            if (!string.IsNullOrWhiteSpace(aiMapping.TransformationRule) &&
                aiMapping.TransformationRule != "DIRECT_MAPPING")
            {
                rules.Insert(0, new TransformationRule { Operation = aiMapping.TransformationRule });
            }

            return rules;
        }
    }
}
