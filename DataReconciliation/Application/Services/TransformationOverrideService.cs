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
        /// UI-friendly catalog of transformation operations shown in the override dropdown.
        /// Each entry is mapped to a canonical registry operation by BuildTransformationRule.
        /// </summary>
        public static readonly string[] TransformationCatalog = new[]
        {
            "DIRECT",
            // Date (pre-defined format combos for common mainframe/banking patterns)
            "DATE_YYYYMMDD",
            "DATE_DDMMYYYY",
            "DATE_MMDDYYYY",
            "DATE_DD/MM/YYYY",
            "DATE_MM/DD/YYYY",
            "DATE_YYYY/MM/DD",
            "DATE_YYYY-MM-DD",
            // Padding & width
            "PAD_LEFT_ZEROS",
            "PAD_LEFT_SPACES",
            "PAD_RIGHT_SPACES",
            "TRUNCATE",
            "FIXED_WIDTH_FORMAT",
            // Case
            "UPPERCASE",
            "LOWERCASE",
            "PROPERCASE",
            // Masking & numeric
            "MASK",
            "DECIMAL_FORMAT",
            "STRING_TO_NUMERIC",
            "NUMERIC_TO_STRING",
            "CURRENCY_NORMALIZATION",
            // String
            "REMOVE_SPECIAL_CHARACTERS",
            // Logic & mapping
            "BOOLEAN_MAPPING",
            "VALUE_MAPPING",
            "NULL_REPLACEMENT",
            // Structural
            "CONCAT",
            "SPLIT",
            // Assignment & custom
            "DEFAULT_VALUE",
            "CUSTOM",
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
            var op   = (operation ?? string.Empty).ToUpperInvariant().Trim();
            var expr = (customExpression ?? string.Empty).Trim();

            switch (op)
            {
                case "DIRECT": return null;

                // ── Date: map UI-friendly format names to canonical DATE_FORMAT ──────────
                case "DATE_YYYYMMDD":   return DateFmt("yyyyMMdd");
                case "DATE_DDMMYYYY":   return DateFmt("ddMMyyyy");
                case "DATE_MMDDYYYY":   return DateFmt("MMddyyyy");
                case "DATE_DD/MM/YYYY": return DateFmt("dd/MM/yyyy");
                case "DATE_MM/DD/YYYY": return DateFmt("MM/dd/yyyy");
                case "DATE_YYYY/MM/DD": return DateFmt("yyyy/MM/dd");
                case "DATE_YYYY-MM-DD": return DateFmt("yyyy-MM-dd");

                // ── Padding: canonical PAD_LEFT / PAD_RIGHT ───────────────────────────
                // expr (optional) = target total length, e.g. "10"
                case "PAD_LEFT_ZEROS":   return PadOp("PAD_LEFT",  "0", FirstPart(expr));
                case "PAD_LEFT_SPACES":  return PadOp("PAD_LEFT",  " ", FirstPart(expr));
                case "PAD_RIGHT_SPACES": return PadOp("PAD_RIGHT", " ", FirstPart(expr));
                case "LEFT_PAD":         return PadOp("PAD_LEFT",  "0", null);   // backward-compat
                case "RIGHT_PAD":        return PadOp("PAD_RIGHT", " ", null);   // backward-compat

                // ── Width / truncation ────────────────────────────────────────────────
                // TRUNCATE expr (optional) = max length, e.g. "20"
                case "TRUNCATE":
                {
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(expr)) ai["maxLength"] = expr;
                    return new TransformationRule { Operation = "TRUNCATE", AIParameters = ai.Count > 0 ? ai : null };
                }

                // FIXED_WIDTH_FORMAT expr (optional) = "width,alignment,padChar"  e.g. "10,LEFT,0"
                case "FIXED_WIDTH_FORMAT":
                {
                    var p  = SplitParts(expr, 3);
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(p[0])) ai["width"]     = p[0];
                    if (!string.IsNullOrWhiteSpace(p[1])) ai["alignment"] = p[1].ToUpperInvariant();
                    if (!string.IsNullOrWhiteSpace(p[2])) ai["padChar"]   = p[2];
                    return new TransformationRule { Operation = "FIXED_WIDTH_FORMAT", AIParameters = ai.Count > 0 ? ai : null };
                }

                // ── Case ──────────────────────────────────────────────────────────────
                case "UPPERCASE":  return new TransformationRule { Operation = "UPPERCASE" };
                case "LOWERCASE":  return new TransformationRule { Operation = "LOWERCASE" };
                case "PROPERCASE": return new TransformationRule { Operation = "PROPERCASE" };

                // ── Masking ───────────────────────────────────────────────────────────
                // expr (optional) = "visibleStart,visibleEnd,maskChar"  e.g. "0,4,*"
                case "MASK":
                {
                    var p  = SplitParts(expr, 3);
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(p[0])) ai["visibleStart"] = p[0];
                    if (!string.IsNullOrWhiteSpace(p[1])) ai["visibleEnd"]   = p[1];
                    if (!string.IsNullOrWhiteSpace(p[2])) ai["maskChar"]     = p[2];
                    return new TransformationRule { Operation = "MASK", AIParameters = ai.Count > 0 ? ai : null };
                }

                // ── Numeric ───────────────────────────────────────────────────────────
                // DECIMAL_FORMAT expr (optional) = "decimalPlaces" or "decimalPlaces,stripSeparator"
                case "DECIMAL_FORMAT":
                {
                    var p  = SplitParts(expr, 2);
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(p[0])) ai["decimalPlaces"] = p[0];
                    if (!string.IsNullOrWhiteSpace(p[1])) ai["stripDecimalSeparator"] = p[1];
                    return new TransformationRule { Operation = "DECIMAL_FORMAT", AIParameters = ai.Count > 0 ? ai : null };
                }

                case "STRING_TO_NUMERIC":    return new TransformationRule { Operation = "STRING_TO_NUMERIC" };
                case "NUMERIC_TO_STRING":    return new TransformationRule { Operation = "NUMERIC_TO_STRING" };

                // CURRENCY_NORMALIZATION expr (optional) = decimal places, e.g. "2"
                case "CURRENCY_NORMALIZATION":
                {
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(expr)) ai["decimalPlaces"] = expr;
                    return new TransformationRule { Operation = "CURRENCY_NORMALIZATION", AIParameters = ai.Count > 0 ? ai : null };
                }

                // ── String ────────────────────────────────────────────────────────────
                case "REMOVE_SPECIAL_CHARACTERS": return new TransformationRule { Operation = "REMOVE_SPECIAL_CHARACTERS" };

                // ── Logic & mapping ───────────────────────────────────────────────────
                case "BOOLEAN_MAPPING": return new TransformationRule { Operation = "BOOLEAN_MAPPING" };

                case "VALUE_MAPPING": return new TransformationRule { Operation = "VALUE_MAPPING", Rules = new Dictionary<string, string>() };

                // NULL_REPLACEMENT expr = replacement value when source is blank, e.g. "N/A"
                case "NULL_REPLACEMENT":
                    return new TransformationRule
                    {
                        Operation    = "NULL_REPLACEMENT",
                        AIParameters = !string.IsNullOrWhiteSpace(expr)
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["value"] = expr }
                            : null
                    };

                // ── Structural ────────────────────────────────────────────────────────
                // CONCAT expr = comma-separated field names, e.g. "FIRST_NAME,LAST_NAME"
                case "CONCAT":
                    return new TransformationRule
                    {
                        Operation    = "CONCAT",
                        AIParameters = !string.IsNullOrWhiteSpace(expr)
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["fields"] = expr }
                            : null
                    };

                // SPLIT expr = "delimiter,index"  e.g. "/,1"
                case "SPLIT":
                {
                    var p  = SplitParts(expr, 2);
                    var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(p[0])) ai["delimiter"] = p[0];
                    if (!string.IsNullOrWhiteSpace(p[1])) ai["index"]     = p[1];
                    return new TransformationRule { Operation = "SPLIT", AIParameters = ai.Count > 0 ? ai : null };
                }

                // ── Assignment ────────────────────────────────────────────────────────
                // DEFAULT_VALUE / HARDCODED (backward-compat) expr = the constant value
                case "DEFAULT_VALUE":
                case "HARDCODED":
                    return new TransformationRule
                    {
                        Operation    = "DEFAULT_VALUE",
                        DefaultValue = string.IsNullOrWhiteSpace(expr) ? null : expr
                    };

                // ── Custom ────────────────────────────────────────────────────────────
                case "CUSTOM":
                    return new TransformationRule
                    {
                        Operation    = "CUSTOM",
                        AIParameters = !string.IsNullOrWhiteSpace(expr)
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["expression"] = expr }
                            : null
                    };

                default:
                    return new TransformationRule { Operation = operation };
            }

            // ── Local helpers ─────────────────────────────────────────────────────────

            static TransformationRule DateFmt(string fmt) =>
                new() { Operation = "DATE_FORMAT", AIParameters = new(StringComparer.OrdinalIgnoreCase) { ["outputFormat"] = fmt } };

            static TransformationRule PadOp(string canonicalOp, string padChar, string? width)
            {
                var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["padChar"] = padChar };
                if (!string.IsNullOrWhiteSpace(width)) ai["totalLength"] = width;
                return new TransformationRule { Operation = canonicalOp, AIParameters = ai };
            }

            static string[] SplitParts(string input, int count)
            {
                var raw = input.Split(',', StringSplitOptions.None).Select(p => p.Trim()).ToArray();
                if (raw.Length >= count) return raw;
                return raw.Concat(Enumerable.Repeat(string.Empty, count - raw.Length)).ToArray();
            }

            static string FirstPart(string input) => input.Split(',')[0].Trim();
        }
    }
}
