using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.AI.Clients
{
    /// <summary>
    /// Calls the Python /api/value-mapping-agent endpoint to infer source-value → target-value mappings
    /// for low-cardinality coded fields (e.g. Y/N → Active/Inactive, numeric codes → descriptions).
    /// </summary>
    public class PythonValueMappingAgentService : IValueMappingAIAgentService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PythonValueMappingAgentService> _logger;
        private readonly IConfiguration _configuration;

        private const string AgentEndpoint = "/api/value-mapping-agent";

        public PythonValueMappingAgentService(
            HttpClient httpClient,
            ILogger<PythonValueMappingAgentService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;

            var timeoutSec = _configuration.GetValue<int>("AI:PythonService:TimeoutSeconds", 60);
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        public async Task<ValueMappingCandidateEntry> SuggestMappingsAsync(
            string jobId,
            string sourceField,
            string targetField,
            List<string> sourceValues,
            string? targetDescription = null,
            List<string>? sourceParameterValues = null,
            List<string>? targetParameterValues = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation(
                "ValueMappingAgent request. JobId={JobId} SourceField={SourceField} TargetField={TargetField} Values={Count}",
                jobId, sourceField, targetField, sourceValues.Count);

            var requestBody = new
            {
                jobId,
                sourceField,
                targetField,
                sourceValues,
                targetDescription,
                sourceParameterValues,
                targetParameterValues,
                workflowStep = "ValueMappingDiscovery"
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(AgentEndpoint, content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("ValueMappingAgent HTTP {Status}. JobId={JobId}", response.StatusCode, jobId);
                    return BuildFallbackEntry(jobId, sourceField, targetField, sourceValues);
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var parsed = JsonConvert.DeserializeObject<dynamic>(responseJson);

                var entry = new ValueMappingCandidateEntry
                {
                    SourceField = sourceField,
                    TargetField = targetField,
                    DistinctSourceValues = sourceValues,
                    DistinctValueCount = sourceValues.Count,
                    Status = "Pending",
                    AiConfidence = (double)(parsed?.confidence ?? 0.0),
                    SuggestedMappings = new List<ValueMappingCandidateRule>()
                };

                if (parsed?.mappings != null)
                {
                    foreach (var m in parsed.mappings)
                    {
                        entry.SuggestedMappings.Add(new ValueMappingCandidateRule
                        {
                            SourceValue = (string)(m.sourceValue ?? ""),
                            TargetValue = (string)(m.targetValue ?? ""),
                            Confidence = (double)(m.confidence ?? 0.7),
                            IsAiSuggested = true
                        });
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "ValueMappingAgent completed. JobId={JobId} Field={Field} Suggestions={Count} Confidence={Confidence} Duration={Duration}ms",
                    jobId, sourceField, entry.SuggestedMappings.Count, entry.AiConfidence, sw.ElapsedMilliseconds);

                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ValueMappingAgent call failed. JobId={JobId} Field={Field}", jobId, sourceField);
                return BuildFallbackEntry(jobId, sourceField, targetField, sourceValues);
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var baseUrl = _configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
                var response = await _httpClient.GetAsync("/api/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static ValueMappingCandidateEntry BuildFallbackEntry(
            string jobId, string sourceField, string targetField, List<string> sourceValues) =>
            new()
            {
                SourceField = sourceField,
                TargetField = targetField,
                DistinctSourceValues = sourceValues,
                DistinctValueCount = sourceValues.Count,
                Status = "Pending",
                AiConfidence = 0,
                SuggestedMappings = sourceValues.Select(v => new ValueMappingCandidateRule
                {
                    SourceValue = v,
                    TargetValue = string.Empty,
                    Confidence = 0,
                    IsAiSuggested = false
                }).ToList()
            };
    }
}
