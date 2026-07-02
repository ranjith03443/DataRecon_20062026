using DataReconciliation.Application.Interfaces;
using DataReconciliation.Application.Transformations;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.AI.Clients
{
    /// <summary>
    /// Calls the Python /api/rule-inference endpoint to interpret a free-text
    /// business rule and return a structured transformation operation.
    ///
    /// Python service contract — POST {BaseUrl}{RuleInferenceEndpoint}:
    ///   Request body:
    ///     {
    ///       "rule":                "Convert date from MM/DD/YYYY to YYYYMMDD",
    ///       "fieldContext":        "BIRTH_DATE",
    ///       "jobId":               "JOB_20260525_ABC123",
    ///       "workflowStep":        "TransformationExecution",
    ///       "supportedOperations": ["DATE_FORMAT","TRUNCATE","PAD_LEFT", ...]
    ///     }
    ///   Response body:
    ///     {
    ///       "operation":       "DATE_FORMAT",
    ///       "format":          "YYYYMMDD",
    ///       "parameters":      { "input_format": "MM/DD/YYYY", "output_format": "YYYYMMDD" },
    ///       "description":     "Convert date string from MM/DD/YYYY to YYYYMMDD format",
    ///       "confidence":      0.97,
    ///       "confidenceLevel": "HIGH",
    ///       "reasoning":       "Rule explicitly states source and target date formats.",
    ///       "cached":          false
    ///     }
    /// </summary>
    public class PythonRuleInferenceService : IAIRuleInferenceService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PythonRuleInferenceService> _logger;
        private readonly IConfiguration _configuration;
        private readonly ITransformationRegistryProvider _registry;

        // In-process cache: rule text → result, so the same rule is only inferred once per job
        private readonly Dictionary<string, RuleInferenceResult> _sessionCache = new(StringComparer.OrdinalIgnoreCase);

        public PythonRuleInferenceService(
            HttpClient httpClient,
            ILogger<PythonRuleInferenceService> logger,
            IConfiguration configuration,
            ITransformationRegistryProvider registry)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _registry = registry;

            var timeoutSec = _configuration.GetValue<int>("AI:PythonService:TimeoutSeconds", 30);
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        public async Task<RuleInferenceResult?> InferRuleAsync(
            string rule, string fieldContext, string jobId, string workflowStep = "TransformationExecution")
        {
            if (string.IsNullOrWhiteSpace(rule)) return null;

            // Session-level cache: same rule text → same result
            var cacheKey = $"{rule}|{fieldContext}".ToLowerInvariant();
            if (_sessionCache.TryGetValue(cacheKey, out var cached))
            {
                _logger.LogDebug("Rule inference cache hit. FieldContext={FieldContext} Rule={Rule}", fieldContext, rule[..Math.Min(60, rule.Length)]);
                return cached;
            }

            var baseUrl   = _configuration["AI:PythonService:BaseUrl"]               ?? "http://localhost:8000";
            var endpoint  = _configuration["AI:PythonService:RuleInferenceEndpoint"] ?? "/api/rule-inference";
            var fullUrl   = $"{baseUrl.TrimEnd('/')}{endpoint}";

            // Attach the live registry so Python/AI is constrained to return only names the C# engine can execute
            var registryOps = (await _registry.GetRegistryAsync()).Operations;

            var payload = new
            {
                rule,
                fieldContext,
                jobId,
                workflowStep,
                supportedOperations = registryOps
            };

            var json    = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling Python rule inference. Url={Url} FieldContext={Field} Rule={Rule}",
                fullUrl, fieldContext, rule[..Math.Min(80, rule.Length)]);

            try
            {
                var httpResponse = await _httpClient.PostAsync(fullUrl, content);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    var error = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Rule inference failed. Status={Status} Body={Body} Field={Field}",
                        httpResponse.StatusCode, error, fieldContext);
                    return null;
                }

                var responseBody = await httpResponse.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<RuleInferenceResult>(responseBody);

                if (result != null)
                {
                    _sessionCache[cacheKey] = result;
                    _logger.LogInformation(
                        "Rule inference success. FieldContext={Field} Operation={Op} Confidence={Conf}",
                        fieldContext, result.Operation, result.Confidence);
                }

                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Rule inference service unreachable. Field={Field}", fieldContext);
                return null;
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Rule inference timed out. Field={Field}", fieldContext);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rule inference unexpected error. Field={Field}", fieldContext);
                return null;
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var baseUrl = _configuration["AI:PythonService:BaseUrl"];
                if (string.IsNullOrWhiteSpace(baseUrl)) return false;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var result = await _httpClient.GetAsync($"{baseUrl.TrimEnd('/')}/api/health", cts.Token);
                return result.IsSuccessStatusCode;
            }
            catch { return false; }
        }
    }
}
