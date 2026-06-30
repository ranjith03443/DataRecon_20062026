using DataReconciliation.Application.Transformations;
using DataReconciliation.Domain.Transformations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DataReconciliation.Infrastructure.Transformations
{
    public class SupportedOperationsRegistryProvider : ITransformationRegistryProvider
    {
        private readonly ILogger<SupportedOperationsRegistryProvider> _logger;
        private readonly IHostEnvironment _hostEnvironment;
        private SupportedOperationsRegistry? _cached;

        private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DATE_FORMATTING"] = "DATE_FORMAT",
            ["DATE_FORMAT_CONVERSION"] = "DATE_FORMAT",
            ["DECIMAL_FORMATTING"] = "DECIMAL_FORMAT",
            ["MASKING"] = "MASK",
            ["FIXED_WIDTH_FORMATTING"] = "FIXED_WIDTH_FORMAT",
            ["HARDCODED_VALUE"] = "DEFAULT_VALUE",
            ["DEFAULT_VALUE_ASSIGNMENT"] = "DEFAULT_VALUE",
            ["DATATYPE_CONVERSION"] = "STRING_TO_NUMERIC"
        };

        public SupportedOperationsRegistryProvider(
            ILogger<SupportedOperationsRegistryProvider> logger,
            IHostEnvironment hostEnvironment)
        {
            _logger = logger;
            _hostEnvironment = hostEnvironment;
        }

        public async Task<SupportedOperationsRegistry> GetRegistryAsync()
        {
            if (_cached != null) return _cached;

            var path = Path.Combine(_hostEnvironment.ContentRootPath, "config", "supported_operations.json");
            if (!File.Exists(path))
            {
                _logger.LogWarning("supported_operations.json not found at {Path}; using in-memory defaults", path);
                _cached = new SupportedOperationsRegistry
                {
                    Operations = new List<string>
                    {
                        "DATE_FORMAT","TRUNCATE","PAD_LEFT","PAD_RIGHT","VALUE_MAPPING","MASK","DECIMAL_FORMAT","CONCAT","SPLIT",
                        "DEFAULT_VALUE","NULL_REPLACEMENT","UPPERCASE","LOWERCASE","PROPERCASE","REMOVE_SPECIAL_CHARACTERS",
                        "BOOLEAN_MAPPING","CURRENCY_NORMALIZATION","FIXED_WIDTH_FORMAT","STRING_TO_NUMERIC","NUMERIC_TO_STRING"
                    }
                };
                return _cached;
            }

            await using var stream = File.OpenRead(path);
            _cached = await JsonSerializer.DeserializeAsync<SupportedOperationsRegistry>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new SupportedOperationsRegistry();

            return _cached;
        }

        public bool IsSupported(string operation)
        {
            var op = NormalizeOperation(operation);
            var registry = _cached ?? GetRegistryAsync().GetAwaiter().GetResult();
            return registry.Operations.Any(o => string.Equals(o, op, StringComparison.OrdinalIgnoreCase));
        }

        public string NormalizeOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return string.Empty;
            var op = operation.Trim().ToUpperInvariant();
            return _aliases.TryGetValue(op, out var mapped) ? mapped : op;
        }
    }
}
