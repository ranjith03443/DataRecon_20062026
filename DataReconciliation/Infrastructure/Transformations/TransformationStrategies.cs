using DataReconciliation.Domain.Transformations;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataReconciliation.Infrastructure.Transformations
{
    public class DateFormatStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("DATE_FORMAT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var output = rule.Parameters.GetValueOrDefault("outputFormat")
                         ?? rule.Parameters.GetValueOrDefault("output_format")
                         ?? "yyyyMMdd";
            var inputFmt = rule.Parameters.GetValueOrDefault("inputFormat")
                          ?? rule.Parameters.GetValueOrDefault("input_format");

            DateTime dt;
            if (!string.IsNullOrWhiteSpace(inputFmt))
            {
                var netInput = inputFmt.Replace("YYYY", "yyyy").Replace("DD", "dd");
                if (!DateTime.TryParseExact(value, netInput, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) &&
                    !DateTime.TryParse(value, out dt))
                    return value;
            }
            else if (!DateTime.TryParse(value, out dt))
            {
                return value;
            }

            var netOutput = output.Replace("YYYY", "yyyy").Replace("DD", "dd");
            return dt.ToString(netOutput, CultureInfo.InvariantCulture);
        }
    }

    public class TruncateStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            var maxLenText = rule.Parameters.GetValueOrDefault("maxLength") ?? rule.Parameters.GetValueOrDefault("max_length");
            if (!int.TryParse(maxLenText, out var maxLen) || maxLen < 0) return value;
            return value.Length <= maxLen ? value : value[..maxLen];
        }
    }

    public class PaddingStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) =>
            operation.Equals("PAD_LEFT", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("PAD_RIGHT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            var lenText = rule.Parameters.GetValueOrDefault("totalLength") ??
                          rule.Parameters.GetValueOrDefault("total_length") ??
                          rule.Parameters.GetValueOrDefault("width");
            if (!int.TryParse(lenText, out var totalLen) || totalLen <= 0) return value;

            var padChar = rule.Parameters.GetValueOrDefault("padChar") ??
                          rule.Parameters.GetValueOrDefault("pad_character") ?? " ";
            var pc = string.IsNullOrEmpty(padChar) ? ' ' : padChar[0];

            return rule.Operation.Equals("PAD_LEFT", StringComparison.OrdinalIgnoreCase)
                ? value.PadLeft(totalLen, pc)
                : value.PadRight(totalLen, pc);
        }
    }

    public class DecimalFormatStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("DECIMAL_FORMAT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            if (!decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) &&
                !decimal.TryParse(value, out d))
                return value;

            var dpText = rule.Parameters.GetValueOrDefault("decimalPlaces") ??
                         rule.Parameters.GetValueOrDefault("decimal_places");
            var dp = int.TryParse(dpText, out var p) ? Math.Max(0, p) : 2;
            var formatted = d.ToString($"F{dp}", CultureInfo.InvariantCulture);
            return rule.Parameters.TryGetValue("stripDecimalSeparator", out var strip) && strip.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? formatted.Replace(".", "")
                : formatted;
        }
    }

    public class ValueMappingStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("VALUE_MAPPING", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = (input.CurrentValue ?? string.Empty).Trim();
            var mapKey = $"map:{value}";
            if (rule.Parameters.TryGetValue(mapKey, out var mapped)) return mapped;
            return value;
        }
    }

    public class MaskingStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("MASK", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return value;

            var visibleStart = int.TryParse(rule.Parameters.GetValueOrDefault("visibleStart"), out var vs) ? Math.Max(0, vs) : 2;
            var visibleEnd = int.TryParse(rule.Parameters.GetValueOrDefault("visibleEnd"), out var ve) ? Math.Max(0, ve) : 2;
            var maskChar = rule.Parameters.GetValueOrDefault("maskChar") ?? "*";
            var mc = string.IsNullOrEmpty(maskChar) ? '*' : maskChar[0];

            if (value.Length <= visibleStart + visibleEnd)
                return new string(mc, value.Length);

            var middle = new string(mc, value.Length - visibleStart - visibleEnd);
            return value[..visibleStart] + middle + value[^visibleEnd..];
        }
    }

    public class ConcatStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("CONCAT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var delimiter = rule.Parameters.GetValueOrDefault("delimiter") ?? string.Empty;
            var fields = rule.Parameters.GetValueOrDefault("fields");
            if (string.IsNullOrWhiteSpace(fields)) return input.CurrentValue ?? string.Empty;

            var parts = new List<string>();
            foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (input.Record.Fields.TryGetValue(f, out var v) && v != null)
                    parts.Add(v.ToString() ?? string.Empty);
                else
                {
                    var q = input.Record.Fields.FirstOrDefault(kv => kv.Key.EndsWith($".{f}", StringComparison.OrdinalIgnoreCase));
                    parts.Add(q.Value?.ToString() ?? string.Empty);
                }
            }
            return string.Join(delimiter, parts);
        }
    }

    public class SplitStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("SPLIT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            var delimiter = rule.Parameters.GetValueOrDefault("delimiter") ?? " ";
            var idxText = rule.Parameters.GetValueOrDefault("index") ?? "0";
            var index = int.TryParse(idxText, out var i) ? i : 0;
            var parts = value.Split(delimiter);
            return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
        }
    }

    public class UppercaseStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("UPPERCASE", StringComparison.OrdinalIgnoreCase);
        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
            => (input.CurrentValue ?? string.Empty).ToUpperInvariant();
    }

    public class LowercaseStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("LOWERCASE", StringComparison.OrdinalIgnoreCase);
        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
            => (input.CurrentValue ?? string.Empty).ToLowerInvariant();
    }

    public class ProperCaseStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("PROPERCASE", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            var ti = CultureInfo.InvariantCulture.TextInfo;
            return ti.ToTitleCase(value.ToLowerInvariant());
        }
    }

    public class BooleanMappingStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("BOOLEAN_MAPPING", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = (input.CurrentValue ?? string.Empty).Trim();
            var trueValues = (rule.Parameters.GetValueOrDefault("trueValues") ?? "Y,YES,TRUE,1")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var falseValues = (rule.Parameters.GetValueOrDefault("falseValues") ?? "N,NO,FALSE,0")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (trueValues.Contains(value)) return rule.Parameters.GetValueOrDefault("trueOutput") ?? "1";
            if (falseValues.Contains(value)) return rule.Parameters.GetValueOrDefault("falseOutput") ?? "0";
            return rule.Parameters.GetValueOrDefault("defaultOutput") ?? "0";
        }
    }

    public class CurrencyNormalizationStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("CURRENCY_NORMALIZATION", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var cleaned = Regex.Replace(value, "[^0-9\\.-]", string.Empty);
            if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) &&
                !decimal.TryParse(cleaned, out d))
                return value;

            var dp = int.TryParse(rule.Parameters.GetValueOrDefault("decimalPlaces"), out var parsed) ? parsed : 2;
            return d.ToString($"F{Math.Max(0, dp)}", CultureInfo.InvariantCulture);
        }
    }
}
