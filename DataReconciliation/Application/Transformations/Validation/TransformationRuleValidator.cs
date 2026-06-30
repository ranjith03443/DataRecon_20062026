using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Application.Transformations.Validation
{
    public class TransformationRuleValidator
    {
        private readonly ITransformationRegistryProvider _registryProvider;

        public TransformationRuleValidator(ITransformationRegistryProvider registryProvider)
        {
            _registryProvider = registryProvider;
        }

        public async Task<TransformationValidationReport> ValidateAsync(
            string jobId,
            string correlationId,
            IEnumerable<TransformationRuleContract> rules,
            TargetMetadataProfile targetProfile)
        {
            _ = await _registryProvider.GetRegistryAsync();

            var report = new TransformationValidationReport
            {
                JobId = jobId,
                CorrelationId = correlationId
            };

            var targetByField = targetProfile.Fields
                .ToDictionary(f => f.FieldName, StringComparer.OrdinalIgnoreCase);

            foreach (var rule in rules)
            {
                var op = _registryProvider.NormalizeOperation(rule.Operation);

                if (!_registryProvider.IsSupported(op))
                {
                    report.InvalidOperations.Add(new ValidationIssue
                    {
                        SourceField = rule.SourceField,
                        TargetField = rule.TargetField,
                        Operation = rule.Operation,
                        Message = "Unsupported operation"
                    });
                    continue;
                }

                if (!targetByField.TryGetValue(rule.TargetField, out var targetField))
                    continue;

                ValidateParameterStructure(rule, op, report);
                ValidateDatatypeCompatibility(rule, op, targetField, report);
            }

            return report;
        }

        private static void ValidateParameterStructure(
            TransformationRuleContract rule,
            string operation,
            TransformationValidationReport report)
        {
            bool missing(string key) => !rule.Parameters.ContainsKey(key) || string.IsNullOrWhiteSpace(rule.Parameters[key]);

            switch (operation)
            {
                case "DATE_FORMAT":
                    if (missing("outputFormat"))
                        report.ParameterFailures.Add(Issue(rule, "DATE_FORMAT requires parameter 'outputFormat'"));
                    break;
                case "TRUNCATE":
                    if (missing("maxLength"))
                        report.ParameterFailures.Add(Issue(rule, "TRUNCATE requires parameter 'maxLength'"));
                    break;
                case "PAD_LEFT":
                case "PAD_RIGHT":
                    if (missing("totalLength"))
                        report.ParameterFailures.Add(Issue(rule, "PAD_LEFT/PAD_RIGHT requires parameter 'totalLength'"));
                    break;
                case "SPLIT":
                    if (missing("delimiter") || missing("index"))
                        report.ParameterFailures.Add(Issue(rule, "SPLIT requires 'delimiter' and 'index'"));
                    break;
                case "DEFAULT_VALUE":
                    // DEFAULT_VALUE without 'value' is treated as pass-through by execution fallback.
                    // Keep this valid to avoid false parameter failures for mappings with no explicit transform.
                    break;
                case "NULL_REPLACEMENT":
                    if (missing("value"))
                        report.ParameterFailures.Add(Issue(rule, "NULL_REPLACEMENT requires 'value'"));
                    break;
                case "FIXED_WIDTH_FORMAT":
                    if (missing("width"))
                        report.ParameterFailures.Add(Issue(rule, "FIXED_WIDTH_FORMAT requires 'width'"));
                    break;
            }
        }

        private static void ValidateDatatypeCompatibility(
            TransformationRuleContract rule,
            string operation,
            TargetFieldMetadata targetField,
            TransformationValidationReport report)
        {
            var dt = targetField.Datatype ?? string.Empty;
            bool isNumeric = dt.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                             dt.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase) ||
                             dt.Contains("INT", StringComparison.OrdinalIgnoreCase);
            bool isDate = dt.Contains("DATE", StringComparison.OrdinalIgnoreCase) ||
                          dt.Contains("TIME", StringComparison.OrdinalIgnoreCase);

            if (operation == "DECIMAL_FORMAT" && !isNumeric)
                report.DatatypeFailures.Add(Issue(rule, "DECIMAL_FORMAT requires numeric target datatype"));

            if (operation == "DATE_FORMAT" && !isDate)
                report.DatatypeFailures.Add(Issue(rule, "DATE_FORMAT is expected for date/time target datatype"));
        }

        private static ValidationIssue Issue(TransformationRuleContract rule, string message) => new()
        {
            SourceField = rule.SourceField,
            TargetField = rule.TargetField,
            Operation = rule.Operation,
            Message = message
        };
    }
}
