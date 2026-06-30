using DataReconciliation.Application.Interfaces;
using DataReconciliation.Application.Transformations.Validation;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Transformations;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Transformations
{
    public class TransformationExecutionService : ITransformationExecutionService
    {
        private readonly ILogger<TransformationExecutionService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly ITransformationStrategyFactory _strategyFactory;
        private readonly ITransformationRegistryProvider _registryProvider;
        private readonly TransformationRuleContractBuilder _contractBuilder;
        private readonly TransformationRuleValidator _validator;
        private readonly IAIRuleInferenceService? _ruleInference;

        public TransformationExecutionService(
            ILogger<TransformationExecutionService> logger,
            IArtifactPersistenceService artifactService,
            ITransformationStrategyFactory strategyFactory,
            ITransformationRegistryProvider registryProvider,
            TransformationRuleContractBuilder contractBuilder,
            TransformationRuleValidator validator,
            IAIRuleInferenceService? ruleInference = null)
        {
            _logger = logger;
            _artifactService = artifactService;
            _strategyFactory = strategyFactory;
            _registryProvider = registryProvider;
            _contractBuilder = contractBuilder;
            _validator = validator;
            _ruleInference = ruleInference;
        }

        public async Task<TransformationExecutionResult> ExecuteAsync(
            string jobId,
            IEnumerable<CanonicalRecord> canonicalRecords,
            FinalMappingConfig mappingConfig,
            TargetMetadataProfile targetProfile)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting deterministic transformation execution. JobId={JobId} CorrelationId={CorrelationId}",
                jobId, correlationId);

            // Build and persist declarative transformation contract
            var rulesArtifact = _contractBuilder.Build(jobId, mappingConfig);
            await _artifactService.PersistArtifactAsync(jobId, rulesArtifact, ArtifactType.AuditLog, "transformation_rules.json");

            // Normalize and enrich unsupported operations via AI declarative inference (no executable code generation)
            await ResolveUnsupportedOperationsViaAIAsync(jobId, rulesArtifact);

            // Validate rules before execution
            var validationReport = await _validator.ValidateAsync(jobId, correlationId, rulesArtifact.Rules, targetProfile);

            var targetByName = targetProfile.Fields.ToDictionary(f => f.FieldName, StringComparer.OrdinalIgnoreCase);
            var rulesByTarget = rulesArtifact.Rules
                .GroupBy(r => r.TargetField, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var result = new TransformationExecutionResult
            {
                JobId = jobId,
                CorrelationId = correlationId,
                ValidationReport = validationReport,
                Audit = new TransformationAuditDocument
                {
                    JobId = jobId,
                    CorrelationId = correlationId
                }
            };

            foreach (var record in canonicalRecords.OrderBy(r => r.RowIndex))
            {
                var transformedRecord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var recordRejected = false;

                foreach (var targetField in targetProfile.Fields.OrderBy(f => f.ColumnOrder))
                {
                    try
                    {
                        rulesByTarget.TryGetValue(targetField.FieldName, out var fieldRules);
                        fieldRules ??= new List<TransformationRuleContract>();

                        string sourceField = fieldRules.FirstOrDefault()?.SourceField ?? string.Empty;
                        string? currentValue = ResolveSourceValue(record, sourceField, mappingConfig, targetField.FieldName);
                        var originalValue = currentValue;

                        foreach (var rule in fieldRules)
                        {
                            var normalizedOp = _registryProvider.NormalizeOperation(rule.Operation);

                            if (!_registryProvider.IsSupported(normalizedOp))
                            {
                                validationReport.InvalidOperations.Add(new ValidationIssue
                                {
                                    RecordId = record.RowIndex,
                                    SourceField = rule.SourceField,
                                    TargetField = rule.TargetField,
                                    Operation = rule.Operation,
                                    Value = currentValue,
                                    Message = "Unsupported operation at execution time"
                                });
                                recordRejected = true;
                                continue;
                            }

                            var strategy = _strategyFactory.Resolve(normalizedOp);
                            var opSw = System.Diagnostics.Stopwatch.StartNew();

                            string transformed;
                            if (strategy != null)
                            {
                                transformed = strategy.Transform(new TransformationExecutionInput
                                {
                                    CurrentValue = currentValue,
                                    Record = record,
                                    Mapping = mappingConfig.Mappings.FirstOrDefault(m =>
                                        string.Equals(m.TargetField, targetField.FieldName, StringComparison.OrdinalIgnoreCase)) ?? new FinalMapping(),
                                    TargetField = targetField
                                }, new TransformationRuleContract
                                {
                                    SourceField = rule.SourceField,
                                    TargetField = rule.TargetField,
                                    Operation = normalizedOp,
                                    Parameters = rule.Parameters,
                                    Confidence = rule.Confidence,
                                    GeneratedBy = rule.GeneratedBy
                                });
                            }
                            else
                            {
                                transformed = ApplyBuiltInFallback(normalizedOp, currentValue, rule.Parameters);
                            }

                            opSw.Stop();
                            _logger.LogInformation(
                                "Transformation operation executed. JobId={JobId} CorrelationId={CorrelationId} RecordId={RecordId} Operation={Operation} SourceField={SourceField} TargetField={TargetField} ExecutionDurationMs={Duration}",
                                jobId, correlationId, record.RowIndex, normalizedOp, rule.SourceField, rule.TargetField, opSw.Elapsed.TotalMilliseconds);

                            result.Audit.Entries.Add(new TransformationAuditEntry
                            {
                                RecordId = record.RowIndex,
                                SourceField = rule.SourceField,
                                TargetField = rule.TargetField,
                                OriginalValue = originalValue,
                                TransformedValue = transformed,
                                Operation = normalizedOp,
                                Timestamp = DateTime.UtcNow,
                                CorrelationId = correlationId,
                                JobId = jobId,
                                ExecutionDurationMs = opSw.Elapsed.TotalMilliseconds
                            });

                            currentValue = transformed;
                        }

                        if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(targetField.DefaultValue))
                            currentValue = targetField.DefaultValue;

                        transformedRecord[targetField.FieldName] = currentValue ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Transformation formatting issue. JobId={JobId} CorrelationId={CorrelationId} RecordId={RecordId} TargetField={TargetField}",
                            jobId, correlationId, record.RowIndex, targetField.FieldName);

                        validationReport.FormattingIssues.Add(new ValidationIssue
                        {
                            RecordId = record.RowIndex,
                            TargetField = targetField.FieldName,
                            Message = ex.Message
                        });
                        transformedRecord[targetField.FieldName] = string.Empty;
                        recordRejected = true;
                    }

                    // Max length governance check
                    if (targetByName.TryGetValue(targetField.FieldName, out var tf) && tf.FieldLength.HasValue)
                    {
                        var v = transformedRecord[targetField.FieldName] ?? string.Empty;
                        if (v.Length > tf.FieldLength.Value)
                        {
                            validationReport.MaxLengthFailures.Add(new ValidationIssue
                            {
                                RecordId = record.RowIndex,
                                TargetField = targetField.FieldName,
                                Value = v,
                                Message = $"Value length {v.Length} exceeds max {tf.FieldLength.Value}"
                            });
                            recordRejected = true;
                        }
                    }
                }

                if (recordRejected)
                    validationReport.RejectedRecords.Add(record.RowIndex);

                result.TransformedRecords.Add(transformedRecord);
            }

            await _artifactService.PersistArtifactAsync(jobId, result.Audit, ArtifactType.AuditLog, "transformation_audit.json");
            await _artifactService.PersistArtifactAsync(jobId, validationReport, ArtifactType.WorkflowLog, "validation_report.json");

            sw.Stop();
            _logger.LogInformation(
                "Deterministic transformation execution completed. JobId={JobId} CorrelationId={CorrelationId} Records={Records} Rejected={Rejected} DurationMs={Duration}",
                jobId, correlationId, result.TransformedRecords.Count, validationReport.RejectedRecords.Count, sw.Elapsed.TotalMilliseconds);

            return result;
        }

        private async Task ResolveUnsupportedOperationsViaAIAsync(string jobId, TransformationRulesArtifact artifact)
        {
            if (_ruleInference == null)
                return;

            foreach (var rule in artifact.Rules)
            {
                var normalized = _registryProvider.NormalizeOperation(rule.Operation);
                if (_registryProvider.IsSupported(normalized))
                {
                    rule.Operation = normalized;
                    continue;
                }

                var result = await _ruleInference.InferRuleAsync(
                    rule: rule.Operation,
                    fieldContext: rule.TargetField,
                    jobId: jobId,
                    workflowStep: "TransformationExecution");

                if (result == null || result.Confidence < 0.60 || string.IsNullOrWhiteSpace(result.Operation))
                    continue;

                var inferredNormalized = _registryProvider.NormalizeOperation(result.Operation);
                if (!_registryProvider.IsSupported(inferredNormalized))
                    continue;

                rule.Operation = inferredNormalized;
                if (result.Parameters != null)
                {
                    foreach (var kv in result.Parameters)
                        rule.Parameters[kv.Key] = kv.Value;
                }
                if (!string.IsNullOrWhiteSpace(result.Format))
                    rule.Parameters["outputFormat"] = result.Format;

                rule.GeneratedBy = "AI_RULE_INFERENCE";
            }
        }

        private static string? ResolveSourceValue(
            CanonicalRecord record,
            string sourceField,
            FinalMappingConfig mappingConfig,
            string targetField)
        {
            if (string.IsNullOrWhiteSpace(sourceField))
                return null;

            var mapping = mappingConfig.Mappings.FirstOrDefault(m =>
                string.Equals(m.TargetField, targetField, StringComparison.OrdinalIgnoreCase));
            var sourceDataset = mapping?.SourceDataset;

            if (!string.IsNullOrWhiteSpace(sourceDataset))
            {
                var qualified = $"{sourceDataset}.{sourceField}";
                if (record.Fields.TryGetValue(qualified, out var qv))
                    return qv?.ToString();
            }

            if (record.Fields.TryGetValue(sourceField, out var v))
                return v?.ToString();

            var fallback = record.Fields.FirstOrDefault(kv =>
                kv.Key.EndsWith($".{sourceField}", StringComparison.OrdinalIgnoreCase));
            return fallback.Value?.ToString();
        }

        private static string ApplyBuiltInFallback(string operation, string? value, Dictionary<string, string> parameters)
        {
            var current = value ?? string.Empty;

            switch (operation)
            {
                case "DEFAULT_VALUE":
                    return parameters.TryGetValue("value", out var dv) ? dv : current;

                case "NULL_REPLACEMENT":
                    if (!string.IsNullOrWhiteSpace(current)) return current;
                    return parameters.TryGetValue("value", out var nr) ? nr : string.Empty;

                case "REMOVE_SPECIAL_CHARACTERS":
                    return new string(current.Where(char.IsLetterOrDigit).ToArray());

                case "FIXED_WIDTH_FORMAT":
                    var width = parameters.TryGetValue("width", out var w) && int.TryParse(w, out var wi) ? wi : current.Length;
                    var align = parameters.TryGetValue("alignment", out var al) ? al : "LEFT";
                    var padChar = parameters.TryGetValue("padChar", out var pc) && !string.IsNullOrEmpty(pc) ? pc[0] : ' ';
                    if (current.Length > width) return current[..width];
                    return align.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
                        ? current.PadLeft(width, padChar)
                        : current.PadRight(width, padChar);

                case "STRING_TO_NUMERIC":
                    return decimal.TryParse(current, out var d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";

                case "NUMERIC_TO_STRING":
                    return current;

                default:
                    return current;
            }
        }
    }
}
