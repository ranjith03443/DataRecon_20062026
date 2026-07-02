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

    // ─── Julian Date Conversion ───────────────────────────────────────────────
    // JULIAN_TO_DATE  : converts YYDDD or YYYYDDD → Gregorian in outputFormat
    // DATE_TO_JULIAN  : converts Gregorian date → YYYYDDD
    public class JulianDateStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) =>
            operation.Equals("JULIAN_TO_DATE", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("DATE_TO_JULIAN", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = (input.CurrentValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            if (rule.Operation.Equals("JULIAN_TO_DATE", StringComparison.OrdinalIgnoreCase))
                return JulianToGregorian(value, rule.Parameters.GetValueOrDefault("outputFormat") ?? "yyyyMMdd");

            return GregorianToJulian(value);
        }

        private static string JulianToGregorian(string julian, string outputFormat)
        {
            // Accept YYDDD (2-digit year) or YYYYDDD (4-digit year)
            int year;
            int doy;
            if (julian.Length == 5 && int.TryParse(julian[..2], out var yy) && int.TryParse(julian[2..], out doy))
                year = yy >= 0 && yy <= 49 ? 2000 + yy : 1900 + yy;
            else if (julian.Length == 7 && int.TryParse(julian[..4], out year) && int.TryParse(julian[4..], out doy))
                { /* year and doy already set */ }
            else
                return julian;

            try
            {
                var date = new DateTime(year, 1, 1).AddDays(doy - 1);
                return date.ToString(outputFormat.Replace("YYYY", "yyyy").Replace("DD", "dd"),
                    CultureInfo.InvariantCulture);
            }
            catch { return julian; }
        }

        private static string GregorianToJulian(string gregorian)
        {
            if (!DateTime.TryParse(gregorian, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                !DateTime.TryParse(gregorian, out date))
                return gregorian;
            return $"{date.Year:D4}{date.DayOfYear:D3}";
        }
    }

    // ─── COMP-3 Packed Decimal Decode ─────────────────────────────────────────
    // Accepts a hex-encoded COMP-3 string (e.g. "0123456C" = +1234.56 with 2 implied decimal places)
    // and converts it to a decimal string.
    public class Comp3DecodeStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("UNPACK_COMP3", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = (input.CurrentValue ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(value)) return "0";

            // Remove any whitespace or 0x prefix
            value = value.Replace(" ", "").Replace("0X", "");

            // Must be hex digits only
            if (!Regex.IsMatch(value, "^[0-9A-F]+$")) return input.CurrentValue ?? "0";

            // Last nibble is the sign: C/F = positive, D = negative
            var lastChar = value[^1];
            var negative = lastChar == 'D';

            // All nibbles except the last are digits, the last is the sign
            var digits = new StringBuilder();
            for (int i = 0; i < value.Length - 1; i++)
                digits.Append(value[i]);

            if (!long.TryParse(digits.ToString(), out var rawNum)) return input.CurrentValue ?? "0";

            var impliedDp = int.TryParse(rule.Parameters.GetValueOrDefault("impliedDecimalPlaces"), out var dp) ? dp : 0;
            var result = impliedDp > 0
                ? ((decimal)rawNum / (decimal)Math.Pow(10, impliedDp)).ToString($"F{impliedDp}", CultureInfo.InvariantCulture)
                : rawNum.ToString(CultureInfo.InvariantCulture);

            return negative ? $"-{result}" : result;
        }
    }

    // ─── Implicit Decimal Shift ───────────────────────────────────────────────
    // Mainframe integers often have an implied decimal: 12345 with 2 implied places → 123.45
    public class DecimalShiftStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("DECIMAL_SHIFT", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = (input.CurrentValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return "0";

            var places = int.TryParse(rule.Parameters.GetValueOrDefault("impliedDecimalPlaces") ??
                                      rule.Parameters.GetValueOrDefault("decimalPlaces"), out var dp) ? dp : 2;

            if (!long.TryParse(Regex.Replace(value, "[^0-9\\-]", ""), out var raw)) return value;
            var shifted = (decimal)raw / (decimal)Math.Pow(10, Math.Max(0, places));
            return shifted.ToString($"F{Math.Max(0, places)}", CultureInfo.InvariantCulture);
        }
    }

    // ─── Conditional Value ────────────────────────────────────────────────────
    // Returns trueValue when the input equals condition, falseValue otherwise.
    // Parameters: condition, trueValue, falseValue, [caseSensitive=false]
    public class ConditionalValueStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("CONDITIONAL_VALUE", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            var condition = rule.Parameters.GetValueOrDefault("condition") ?? string.Empty;
            var trueVal = rule.Parameters.GetValueOrDefault("trueValue") ?? value;
            var falseVal = rule.Parameters.GetValueOrDefault("falseValue") ?? value;
            var cs = rule.Parameters.TryGetValue("caseSensitive", out var csv) &&
                     csv.Equals("true", StringComparison.OrdinalIgnoreCase);

            var match = cs
                ? value == condition
                : value.Equals(condition, StringComparison.OrdinalIgnoreCase);
            return match ? trueVal : falseVal;
        }
    }

    // ─── Substring Extraction ─────────────────────────────────────────────────
    // Extracts a portion of the input by start position and optional length.
    // Parameters: start (1-based, default 1), length (default: rest of string)
    public class SubstringStrategy : ITransformationStrategy
    {
        public bool CanHandle(string operation) => operation.Equals("SUBSTRING", StringComparison.OrdinalIgnoreCase);

        public string Transform(TransformationExecutionInput input, TransformationRuleContract rule)
        {
            var value = input.CurrentValue ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return string.Empty;

            // start is 1-based (COBOL convention)
            var start = int.TryParse(rule.Parameters.GetValueOrDefault("start"), out var s) ? Math.Max(1, s) : 1;
            var zeroStart = start - 1;
            if (zeroStart >= value.Length) return string.Empty;

            if (rule.Parameters.TryGetValue("length", out var lenStr) && int.TryParse(lenStr, out var len) && len > 0)
            {
                var actualLen = Math.Min(len, value.Length - zeroStart);
                return value.Substring(zeroStart, actualLen);
            }
            return value[zeroStart..];
        }
    }
}
