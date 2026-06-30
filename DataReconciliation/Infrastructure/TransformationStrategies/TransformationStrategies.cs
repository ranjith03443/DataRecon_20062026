using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Infrastructure.TransformationStrategies
{
    // ─── Strategy Interface ────────────────────────────────────────────────────
    public interface ITransformationStrategy
    {
        string OperationName { get; }
        string Apply(string? value, TransformationRule rule);
    }

    // ─── Date Formatting Strategy ──────────────────────────────────────────────
    public class DateFormattingStrategy : ITransformationStrategy
    {
        public string OperationName => "DATE_FORMATTING";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            if (!DateTime.TryParse(value, out var date)) return value;

            var format = rule.Format ?? "yyyyMMdd";
            return format
                .Replace("YYYY", date.Year.ToString("D4"))
                .Replace("MM", date.Month.ToString("D2"))
                .Replace("DD", date.Day.ToString("D2"))
                .Replace("yyyy", date.Year.ToString("D4"))
                .Replace("MM", date.Month.ToString("D2"))
                .Replace("dd", date.Day.ToString("D2"));
        }
    }

    // ─── Decimal Formatting Strategy ──────────────────────────────────────────
    public class DecimalFormattingStrategy : ITransformationStrategy
    {
        public string OperationName => "DECIMAL_FORMATTING";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return "0";
            if (!decimal.TryParse(value, out var dec)) return value ?? string.Empty;

            var decimalPlaces = 2;
            return dec.ToString($"F{decimalPlaces}").Replace(".", "").Replace(",", "");
        }
    }

    // ─── Masking Strategy ─────────────────────────────────────────────────────
    public class MaskingStrategy : ITransformationStrategy
    {
        public string OperationName => "MASKING";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var pattern = rule.MaskPattern ?? "****";

            if (value.Length <= 4) return new string('*', value.Length);
            return value[..2] + new string('*', value.Length - 4) + value[^2..];
        }
    }

    // ─── Value Mapping Strategy ────────────────────────────────────────────────
    public class ValueMappingStrategy : ITransformationStrategy
    {
        public string OperationName => "VALUE_MAPPING";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (rule.Rules == null) return value;

            return rule.Rules.TryGetValue(value.Trim(), out var mapped) ? mapped : value;
        }
    }

    // ─── Hardcoded Value Strategy ─────────────────────────────────────────────
    public class HardcodedValueStrategy : ITransformationStrategy
    {
        public string OperationName => "HARDCODED_VALUE";

        public string Apply(string? value, TransformationRule rule) =>
            rule.DefaultValue ?? string.Empty;
    }

    // ─── Fixed Width Formatting Strategy ─────────────────────────────────────
    public class FixedWidthFormattingStrategy : ITransformationStrategy
    {
        public string OperationName => "FIXED_WIDTH_FORMATTING";

        public string Apply(string? value, TransformationRule rule)
        {
            var str = value ?? string.Empty;
            var width = rule.FixedWidth ?? str.Length;
            var padChar = rule.PadCharacter?.Length > 0 ? rule.PadCharacter[0] : ' ';
            var alignment = rule.Alignment ?? "LEFT";

            if (str.Length > width) return str[..width]; // Truncate

            return alignment.ToUpperInvariant() == "RIGHT"
                ? str.PadLeft(width, padChar)
                : str.PadRight(width, padChar);
        }
    }

    // ─── Datatype Conversion Strategy ────────────────────────────────────────
    public class DatatypeConversionStrategy : ITransformationStrategy
    {
        public string OperationName => "DATATYPE_CONVERSION";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            // Basic conversion support
            if (decimal.TryParse(value, out var dec)) return ((long)dec).ToString();
            return value;
        }
    }

    // ─── Default Value Assignment Strategy ───────────────────────────────────
    public class DefaultValueAssignmentStrategy : ITransformationStrategy
    {
        public string OperationName => "DEFAULT_VALUE_ASSIGNMENT";

        public string Apply(string? value, TransformationRule rule) =>
            string.IsNullOrWhiteSpace(value)
                ? (rule.DefaultValue ?? string.Empty)
                : value;
    }

    // ─── Upper Case Strategy ──────────────────────────────────────────────────
    public class UpperCaseStrategy : ITransformationStrategy
    {
        public string OperationName => "UPPERCASE";

        public string Apply(string? value, TransformationRule rule) =>
            value?.ToUpperInvariant() ?? string.Empty;
    }

    // ─── Trim Strategy ────────────────────────────────────────────────────────
    public class TrimStrategy : ITransformationStrategy
    {
        public string OperationName => "TRIM";

        public string Apply(string? value, TransformationRule rule) =>
            value?.Trim() ?? string.Empty;
    }

    // ─── Date Format Conversion Strategy (AI-inferred) ────────────────────────
    // Handles the DATE_FORMAT_CONVERSION operation returned by /api/rule-inference.
    // Uses AIParameters["input_format"] and AIParameters["output_format"] / Format.
    public class DateFormatConversionStrategy : ITransformationStrategy
    {
        public string OperationName => "DATE_FORMAT_CONVERSION";

        public string Apply(string? value, TransformationRule rule)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            // Resolve formats — prefer AIParameters, fall back to Format field
            var inputFmt  = rule.AIParameters?.GetValueOrDefault("input_format");
            var outputFmt = rule.AIParameters?.GetValueOrDefault("output_format") ?? rule.Format;

            // Attempt to parse using input format hint, otherwise generic parse
            DateTime date;
            if (!string.IsNullOrWhiteSpace(inputFmt))
            {
                // Normalise common format tokens to .NET format
                var dotNetInputFmt = NormaliseFormat(inputFmt);
                if (!DateTime.TryParseExact(value, dotNetInputFmt,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out date))
                {
                    if (!DateTime.TryParse(value, out date)) return value;
                }
            }
            else
            {
                if (!DateTime.TryParse(value, out date)) return value;
            }

            if (string.IsNullOrWhiteSpace(outputFmt)) return date.ToString("yyyyMMdd");

            return ApplyOutputFormat(date, outputFmt);
        }

        private static string NormaliseFormat(string fmt) =>
            fmt.Replace("YYYY", "yyyy").Replace("DD", "dd");

        private static string ApplyOutputFormat(DateTime date, string fmt)
        {
            // Replace common tokens not native to .NET
            var result = fmt
                .Replace("YYYY", date.Year.ToString("D4"))
                .Replace("yyyy", date.Year.ToString("D4"))
                .Replace("MM",   date.Month.ToString("D2"))
                .Replace("DD",   date.Day.ToString("D2"))
                .Replace("dd",   date.Day.ToString("D2"));
            return result;
        }
    }

    // ─── Transformation Strategy Factory ─────────────────────────────────────
    public class TransformationStrategyFactory
    {
        private readonly Dictionary<string, ITransformationStrategy> _strategies;
        private readonly ILogger<TransformationStrategyFactory> _logger;

        public TransformationStrategyFactory(
            IEnumerable<ITransformationStrategy> strategies,
            ILogger<TransformationStrategyFactory> logger)
        {
            _strategies = strategies.ToDictionary(s => s.OperationName, StringComparer.OrdinalIgnoreCase);
            _logger = logger;
        }

        public ITransformationStrategy? GetStrategy(string operationName)
        {
            if (_strategies.TryGetValue(operationName, out var strategy))
                return strategy;

            _logger.LogWarning("No strategy found for operation: {Operation}", operationName);
            return null;
        }
    }
}
