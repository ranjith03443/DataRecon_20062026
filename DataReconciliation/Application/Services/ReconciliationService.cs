using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class ReconciliationService : IReconciliationService
    {
        private readonly ILogger<ReconciliationService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        public ReconciliationService(
            ILogger<ReconciliationService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
        }

        public async Task<ReconciliationResult> ReconcileAsync(
            string jobId,
            IEnumerable<CanonicalRecord> sourceRecords,
            IEnumerable<Dictionary<string, string>> transformedRecords,
            TargetMetadataProfile targetProfile)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting reconciliation. JobId={JobId}", jobId);

            var sourceList = sourceRecords.ToList();
            var transformedList = transformedRecords.ToList();

            var result = new ReconciliationResult
            {
                JobId = jobId,
                GeneratedAt = DateTime.UtcNow,
                TotalSourceRecords = sourceList.Count,
                TotalTargetRecords = transformedList.Count
            };

            // 1. Row count validation
            if (sourceList.Count != transformedList.Count)
            {
                result.ValidationErrors.Add($"Row count mismatch: Source={sourceList.Count} Transformed={transformedList.Count}");
                _logger.LogWarning("Row count mismatch. JobId={JobId} Source={S} Transformed={T}", jobId, sourceList.Count, transformedList.Count);
            }

            // 2. Field-level validation per record
            int matchedCount = 0;
            int mismatchCount = 0;

            for (int i = 0; i < Math.Min(sourceList.Count, transformedList.Count); i++)
            {
                var source = sourceList[i];
                var transformed = transformedList[i];
                var recordMismatches = new List<ReconciliationMismatch>();

                foreach (var targetField in targetProfile.Fields)
                {
                    transformed.TryGetValue(targetField.FieldName, out var transformedValue);
                    var validationErrors = ValidateField(targetField, transformedValue);

                    foreach (var error in validationErrors)
                    {
                        recordMismatches.Add(new ReconciliationMismatch
                        {
                            RowIndex = i,
                            FieldName = targetField.FieldName,
                            TransformedValue = transformedValue,
                            ExpectedFormat = targetField.Format ?? targetField.Datatype,
                            MismatchReason = error
                        });
                    }
                }

                if (recordMismatches.Any())
                {
                    result.Mismatches.AddRange(recordMismatches);
                    mismatchCount++;
                }
                else
                {
                    matchedCount++;
                }
            }

            result.MatchedRecords = matchedCount;
            result.MismatchedRecords = mismatchCount;
            result.ErrorRecords = Math.Abs(sourceList.Count - transformedList.Count);

            // 3. Load user-configured reconciliation fields (if any)
            var reconConfig = await _artifactService.LoadArtifactAsync<ReconciliationConfig>(
                jobId, ArtifactType.ReconciliationConfig);

            // Load the final mapping to resolve source field names for configured recon fields
            var finalMapping = await _artifactService.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            if (reconConfig != null && reconConfig.Fields.Any())
            {
                // Use the user-configured reconciliation fields
                _logger.LogInformation("Using user-configured reconciliation fields. JobId={JobId} Count={Count}", jobId, reconConfig.Fields.Count);

                foreach (var reconField in reconConfig.Fields)
                {
                    var fieldName = reconField.FieldName;
                    var validationType = (reconField.ValidationType ?? "COUNT").ToUpperInvariant();

                    // Find the corresponding source field from the final mapping
                    var mapping = finalMapping?.Mappings.FirstOrDefault(m =>
                        m.TargetField.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                    var sourceFieldName = mapping?.SourceField;
                    var sourceDataset = mapping?.SourceDataset;

                    if (validationType == "SUM")
                    {
                        // SUM: compute total of this field in both source and target
                        decimal targetTotal = 0m;
                        foreach (var record in transformedList)
                        {
                            if (record.TryGetValue(fieldName, out var raw) && !string.IsNullOrWhiteSpace(raw))
                            {
                                if (decimal.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                                    targetTotal += val;
                                else if (decimal.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var val2))
                                    targetTotal += val2;
                            }
                        }
                        result.FieldTotals[fieldName] = targetTotal;

                        // Source total
                        decimal sourceTotal = 0m;
                        if (!string.IsNullOrWhiteSpace(sourceFieldName))
                        {
                            foreach (var record in sourceList)
                            {
                                var qualifiedKey = string.IsNullOrEmpty(sourceDataset)
                                    ? sourceFieldName : $"{sourceDataset}.{sourceFieldName}";

                                string? rawVal = null;
                                if (record.Fields.TryGetValue(qualifiedKey, out var qv)) rawVal = qv?.ToString();
                                else if (record.Fields.TryGetValue(sourceFieldName, out var sv)) rawVal = sv?.ToString();

                                if (!string.IsNullOrWhiteSpace(rawVal))
                                {
                                    if (decimal.TryParse(rawVal.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                                        sourceTotal += val;
                                    else if (decimal.TryParse(rawVal.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var val2))
                                        sourceTotal += val2;
                                }
                            }
                        }
                        result.SourceFieldTotals[fieldName] = sourceTotal;
                    }
                    else // COUNT
                    {
                        // COUNT: count distinct non-empty values in source and target
                        var targetDistinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var record in transformedList)
                        {
                            if (record.TryGetValue(fieldName, out var raw) && !string.IsNullOrWhiteSpace(raw))
                                targetDistinct.Add(raw.Trim());
                        }
                        result.TargetFieldCounts[fieldName] = targetDistinct.Count;

                        var sourceDistinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(sourceFieldName))
                        {
                            foreach (var record in sourceList)
                            {
                                var qualifiedKey = string.IsNullOrEmpty(sourceDataset)
                                    ? sourceFieldName : $"{sourceDataset}.{sourceFieldName}";

                                string? rawVal = null;
                                if (record.Fields.TryGetValue(qualifiedKey, out var qv)) rawVal = qv?.ToString();
                                else if (record.Fields.TryGetValue(sourceFieldName, out var sv)) rawVal = sv?.ToString();

                                if (!string.IsNullOrWhiteSpace(rawVal))
                                    sourceDistinct.Add(rawVal.Trim());
                            }
                        }
                        result.SourceFieldCounts[fieldName] = sourceDistinct.Count;
                    }
                }
            }
            else
            {
                // Fallback: compute totals for financial control fields (legacy behaviour)
                foreach (var targetField in targetProfile.Fields.Where(IsFinancialControlTotalCandidate))
                {
                    decimal total = 0m;

                    foreach (var record in transformedList)
                    {
                        if (!record.TryGetValue(targetField.FieldName, out var raw) || string.IsNullOrWhiteSpace(raw))
                            continue;

                        var trimmed = raw.Trim();
                        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv))
                        {
                            total += inv;
                            continue;
                        }

                        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out var cur))
                            total += cur;
                    }

                    result.FieldTotals[targetField.FieldName] = total;

                    // Also compute source-side total via the final mapping so the report can compare
                    var mapping = finalMapping?.Mappings.FirstOrDefault(m =>
                        m.TargetField.Equals(targetField.FieldName, StringComparison.OrdinalIgnoreCase));
                    if (mapping?.SourceField != null)
                    {
                        decimal sourceTotal = 0m;
                        foreach (var record in sourceList)
                        {
                            var qualifiedKey = string.IsNullOrEmpty(mapping.SourceDataset)
                                ? mapping.SourceField : $"{mapping.SourceDataset}.{mapping.SourceField}";

                            string? rawVal = null;
                            if (record.Fields.TryGetValue(qualifiedKey, out var qv)) rawVal = qv?.ToString();
                            else if (record.Fields.TryGetValue(mapping.SourceField, out var sv)) rawVal = sv?.ToString();

                            if (!string.IsNullOrWhiteSpace(rawVal))
                            {
                                if (decimal.TryParse(rawVal.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                                    sourceTotal += val;
                                else if (decimal.TryParse(rawVal.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var val2))
                                    sourceTotal += val2;
                            }
                        }
                        result.SourceFieldTotals[targetField.FieldName] = sourceTotal;
                    }
                }
            }

            // 4. Determine overall status
            result.OverallStatus = result.MismatchedRecords == 0 && result.ErrorRecords == 0
                ? ReconciliationStatus.SUCCESS
                : result.MatchedRecords > 0
                    ? ReconciliationStatus.PARTIAL_SUCCESS
                    : ReconciliationStatus.FAILED;

            sw.Stop();
            _logger.LogInformation("Reconciliation completed. JobId={JobId} Status={Status} Matched={Matched} Mismatched={Mismatched} Duration={Duration}ms",
                jobId, result.OverallStatus, result.MatchedRecords, result.MismatchedRecords, sw.ElapsedMilliseconds);

            await PersistReconciliationResultAsync(jobId, result);
            return result;
        }

        public async Task PersistReconciliationResultAsync(string jobId, ReconciliationResult result)
        {
            await _artifactService.PersistArtifactAsync(jobId, result, ArtifactType.ReconciliationResult, "reconciliation_result.json");
        }

        private static IEnumerable<string> ValidateField(TargetFieldMetadata field, string? value)
        {
            var errors = new List<string>();

            // Required field check
            if (field.IsRequired && string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Required field '{field.FieldName}' is empty");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(value)) return errors;

            // Field length check
            if (field.FieldLength.HasValue && value.Length > field.FieldLength.Value)
                errors.Add($"Value exceeds max length {field.FieldLength}");

            // Datatype validation
            var dtype = field.Datatype.ToUpperInvariant();
            if (dtype.Contains("NUMERIC") || dtype.Contains("INT"))
            {
                var trimmed = value.Trim();
                var isNumeric = decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ||
                                decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out _);

                if (!isNumeric)
                    errors.Add($"Expected numeric value, got '{value}'");
            }
            else if (dtype.Contains("DECIMAL"))
            {
                var trimmed = value.Trim();
                var isDecimal = decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ||
                                decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out _);

                if (!isDecimal)
                    errors.Add($"Expected decimal value, got '{value}'");
            }
            else if (dtype.Contains("DATE"))
            {
                if (!DateTime.TryParse(value.Trim(), out _))
                    errors.Add($"Expected date value, got '{value}'");
            }

            // Validation pattern
            if (!string.IsNullOrWhiteSpace(field.ValidationPattern))
            {
                if (!Regex.IsMatch(value, field.ValidationPattern))
                    errors.Add($"Value does not match required pattern '{field.ValidationPattern}'");
            }

            return errors;
        }

        private static bool IsFinancialControlTotalCandidate(TargetFieldMetadata field)
        {
            var datatype = field.Datatype ?? string.Empty;
            var name = field.FieldName ?? string.Empty;

            var isNumeric = datatype.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase)
                            || datatype.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase)
                            || datatype.Contains("INT", StringComparison.OrdinalIgnoreCase);

            if (!isNumeric)
                return false;

            // Exclude technical identifiers and non-financial numeric attributes
            var excludedNameTokens = new[]
            {
                "ID", "NO", "NUM", "NUMBER", "ACCT", "ACCOUNT", "CIN", "IBAN", "CODE", "KEY",
                "FLAG", "DATE", "DAY", "MONTH", "YEAR", "STATUS", "TYPE", "RATE", "TENOR", "TERM", "SEQ"
            };

            if (excludedNameTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;

            // Include known monetary/value-bearing patterns
            var financialNameTokens = new[]
            {
                "AMOUNT", "AMT", "BAL", "BALANCE", "PREM", "FEE", "INT", "INTEREST", "PRIN", "PRINCIPAL",
                "TOTAL", "VALUE", "CHARGE", "PAYMENT"
            };

            if (financialNameTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return true;

            // Numeric with explicit scale (e.g., NUMERIC(11,2)) is usually value-bearing
            return datatype.Contains(",", StringComparison.OrdinalIgnoreCase);
        }
    }
}
