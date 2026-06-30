using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Models;
using DataReconciliation.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.AI.Clients
{
    /// <summary>
    /// Calls the local Python AI service (FastAPI) instead of OpenAI directly.
    /// Configure in appsettings.json:
    ///   "AI": {
    ///     "Enabled": true,
    ///     "Provider": "PythonService",
    ///     "PythonService": {
    ///       "BaseUrl": "http://localhost:8000",
    ///       "MappingEndpoint": "/api/semantic-mapping",
    ///       "TimeoutSeconds": 30
    ///     }
    ///   }
    ///
    /// Python service contract — POST {BaseUrl}{MappingEndpoint}:
    ///   Request body:
    ///     {
    ///       "jobId":                "JOB_20260525_ABC123",
    ///       "sourceField":          "LOAN_AMT",
    ///       "targetFieldCandidates": [ "LOAN_AMOUNT", "PRINCIPAL" ],
    ///       "sourceMetadata": {
    ///         "datatype":    "NUMERIC",
    ///         "description": "Loan amount in dollars"
    ///       },
    ///       "requestId":    "<cache_key>",
    ///       "workflowStep": "AISemanticMapping"
    ///     }
    ///   Response body:
    ///     {
    ///       "jobId":          "JOB_20260525_ABC123",
    ///       "mapping":        "LOAN_AMOUNT",
    ///       "confidence":     0.93,
    ///       "confidenceLevel":"HIGH",
    ///       "reasoning":      "LOAN_AMT is an abbreviation of LOAN_AMOUNT",
    ///       "alternatives":   [ { "field": "PRINCIPAL", "confidence": 0.55 } ],
    ///       "promptVersion":  "v1",
    ///       "cached":         false,
    ///       "requestId":      "<cache_key>"
    ///     }
    /// The response is normalised into the AIMappingEntry JSON contract before storing in Content.
    /// </summary>
    public class PythonAIInferenceService : IAIInferenceService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PythonAIInferenceService> _logger;
        private readonly AICacheService _cacheService;
        private readonly IAIInferenceAuditRepository _auditRepo;
        private readonly IConfiguration _configuration;

        public PythonAIInferenceService(
            HttpClient httpClient,
            ILogger<PythonAIInferenceService> logger,
            AICacheService cacheService,
            IAIInferenceAuditRepository auditRepo,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _cacheService = cacheService;
            _auditRepo = auditRepo;
            _configuration = configuration;

            var timeoutSec = _configuration.GetValue<int>("AI:PythonService:TimeoutSeconds", 30);
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        public async Task<AIInferenceResponse> InferAsync(AIInferenceRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Python AI inference request. JobId={JobId} Step={Step} CacheKey={CacheKey}",
                request.JobId, request.Step, request.CacheKey);

            // ── Cache check (shared with OpenAI path) ──────────────────────
            if (_cacheService.TryGetResponse(request.CacheKey, out var cachedResponse) && cachedResponse != null)
            {
                _logger.LogInformation("AI cache hit. JobId={JobId} CacheKey={CacheKey}", request.JobId, request.CacheKey);
                await RecordAuditAsync(request, cachedResponse, wasCached: true);
                return cachedResponse;
            }

            var response = new AIInferenceResponse();

            try
            {
                response = await CallPythonServiceAsync(request);
                response.DurationMs = sw.Elapsed.TotalMilliseconds;

                if (response.Success && response.ConfidenceScore >= 0.90)
                    _cacheService.SetResponse(request.CacheKey, response);

                _logger.LogInformation("Python AI inference completed. JobId={JobId} Confidence={Confidence} Duration={Duration}ms",
                    request.JobId, response.ConfidenceScore, response.DurationMs);
            }
            catch (HttpRequestException ex)
            {
                response.Success = false;
                response.ErrorMessage = $"Python service unreachable: {ex.Message}";
                _logger.LogError(ex, "Python AI service unreachable. JobId={JobId} Step={Step}", request.JobId, request.Step);
            }
            catch (TaskCanceledException)
            {
                response.Success = false;
                response.ErrorMessage = "Python AI service timed out.";
                _logger.LogError("Python AI service timed out. JobId={JobId} Step={Step}", request.JobId, request.Step);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Python AI inference failed. JobId={JobId} Step={Step}", request.JobId, request.Step);
            }

            sw.Stop();
            response.DurationMs = sw.Elapsed.TotalMilliseconds;
            await RecordAuditAsync(request, response, wasCached: false);
            return response;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var baseUrl = _configuration["AI:PythonService:BaseUrl"];
                if (string.IsNullOrWhiteSpace(baseUrl)) return false;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // Ping /health or / — Python service should respond 200
                var result = await _httpClient.GetAsync($"{baseUrl.TrimEnd('/')}/health", cts.Token);
                return result.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private async Task<AIInferenceResponse> CallPythonServiceAsync(AIInferenceRequest request)
        {
            var baseUrl  = _configuration["AI:PythonService:BaseUrl"]         ?? "http://localhost:8000";
            var endpoint = _configuration["AI:PythonService:MappingEndpoint"] ?? "/api/semantic-mapping";
            var fullUrl  = $"{baseUrl.TrimEnd('/')}{endpoint}";

            // ── Extract structured fields from Context ──────────────────────
            request.Context.TryGetValue("sourceField",          out var sfObj);
            request.Context.TryGetValue("targetFieldCandidates", out var tfcObj);
            request.Context.TryGetValue("sourceDatatype",       out var dtObj);
            request.Context.TryGetValue("sourceDescription",    out var descObj);
            request.Context.TryGetValue("sampleValues",         out var svObj);
            request.Context.TryGetValue("nullable",             out var nullObj);
            request.Context.TryGetValue("maxLength",            out var mlObj);

            var sourceField          = sfObj?.ToString() ?? string.Empty;
            var targetFieldCandidates = tfcObj is IEnumerable<object> list
                ? list.Select(x => x.ToString() ?? string.Empty).ToList()
                : tfcObj?.ToString()?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(x => x.Trim()).ToList() ?? new List<string>();

            // Guard: skip call if mandatory fields are missing — avoids a guaranteed 422
            if (string.IsNullOrWhiteSpace(sourceField) || targetFieldCandidates.Count == 0)
            {
                _logger.LogWarning(
                    "Skipping Python AI call — sourceField or targetFieldCandidates is empty. " +
                    "JobId={JobId} SourceField='{SF}' CandidateCount={Count}",
                    request.JobId, sourceField, targetFieldCandidates.Count);
                return new AIInferenceResponse
                {
                    Success      = false,
                    ErrorMessage = "targetFieldCandidates is empty — target schema may have no fields."
                };
            }
            var payload = new
            {
                jobId                = request.JobId,
                sourceField,
                targetFieldCandidates,
                sourceMetadata = new
                {
                    datatype     = dtObj?.ToString()   ?? "string",
                    description  = descObj?.ToString() ?? string.Empty,
                    nullable     = nullObj is bool nb ? nb : true,
                    sample_values = svObj is IEnumerable<object> svList
                        ? svList.Select(x => x.ToString() ?? string.Empty).ToList()
                        : new List<string>(),
                    max_length   = mlObj != null && int.TryParse(mlObj.ToString(), out var ml) ? ml : (int?)null
                },
                requestId    = request.CacheKey,
                workflowStep = request.Step.ToString()
            };

            var json    = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogDebug("Calling Python AI service. Url={Url} JobId={JobId} SourceField={SourceField}",
                fullUrl, request.JobId, sourceField);

            var httpResponse = await _httpClient.PostAsync(fullUrl, content);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var error = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogWarning("Python AI service error. Status={Status} Body={Body}", httpResponse.StatusCode, error);
                return new AIInferenceResponse
                {
                    Success      = false,
                    ErrorMessage = $"Python service returned {httpResponse.StatusCode}: {error}"
                };
            }

            var responseBody = await httpResponse.Content.ReadAsStringAsync();
            _logger.LogDebug("Python AI service response. JobId={JobId} Body={Body}", request.JobId, responseBody);

            // ── Parse Python response and normalise to AIMappingEntry schema ─
            double confidenceScore = 0;
            string normalisedContent = responseBody;
            int? promptTokens = null, completionTokens = null, totalTokens = null;
            double? estimatedCostUsd = null;
            try
            {
                var pythonResp = JsonConvert.DeserializeObject<dynamic>(responseBody);
                if (pythonResp != null)
                {
                    double.TryParse(
                        ((string?)pythonResp.confidence?.ToString()) ?? "0",
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out confidenceScore);

                    // Parse token usage
                    int pt = 0, ct = 0, tt = 0; double cost = 0;
                    if (pythonResp.promptTokens != null && int.TryParse((string?)pythonResp.promptTokens.ToString(), out pt)) promptTokens = pt;
                    if (pythonResp.completionTokens != null && int.TryParse((string?)pythonResp.completionTokens.ToString(), out ct)) completionTokens = ct;
                    if (pythonResp.totalTokens != null && int.TryParse((string?)pythonResp.totalTokens.ToString(), out tt)) totalTokens = tt;
                    if (pythonResp.estimatedCostUsd != null && double.TryParse((string?)pythonResp.estimatedCostUsd.ToString(),
                        System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out cost))
                        estimatedCostUsd = cost;

                    // Normalise into AIMappingEntry-compatible JSON consumed by downstream validators
                    var normalised = new
                    {
                        sourceField      = sourceField,
                        targetField      = (string?)pythonResp.mapping?.ToString() ?? string.Empty,
                        transformationRule = "DIRECT_MAPPING",
                        confidence       = confidenceScore,
                        confidenceLevel  = (string?)pythonResp.confidenceLevel?.ToString() ?? string.Empty,
                        reasoning        = (string?)pythonResp.reasoning?.ToString() ?? string.Empty,
                        promptVersion    = (string?)pythonResp.promptVersion?.ToString(),
                        cached           = (bool?)pythonResp.cached ?? false
                    };
                    normalisedContent = JsonConvert.SerializeObject(normalised);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Python AI response. Falling back to raw body. JobId={JobId}", request.JobId);
                confidenceScore = ExtractConfidence(responseBody);
            }

            return new AIInferenceResponse
            {
                Success           = true,
                Content           = normalisedContent,
                ConfidenceScore   = confidenceScore,
                PromptTokens      = promptTokens,
                CompletionTokens  = completionTokens,
                TotalTokens       = totalTokens,
                EstimatedCostUsd  = estimatedCostUsd
            };
        }

        private static double ExtractConfidence(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;
            try
            {
                var obj  = JsonConvert.DeserializeObject<dynamic>(content);
                var conf = (obj?.confidence ?? obj?.confidenceScore)?.ToString();
                if (!string.IsNullOrEmpty(conf))
                {
                    double score = 0;
                    if (double.TryParse(conf, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out score))
                        return score;
                }
            }
            catch { }
            return 0.85;
        }

        private async Task RecordAuditAsync(AIInferenceRequest request, AIInferenceResponse response, bool wasCached)
        {
            try
            {
                await _auditRepo.CreateAsync(new AIInferenceAudit
                {
                    JobId           = request.JobId,
                    Step            = request.Step,
                    PromptText      = request.Prompt,
                    ResponseText    = response.Content,
                    ConfidenceScore = response.ConfidenceScore,
                    WasCached       = wasCached,
                    WasAccepted     = response.Success,
                    RetryCount      = response.RetryCount,
                    RequestedAt     = DateTime.UtcNow,
                    RespondedAt     = DateTime.UtcNow,
                    DurationMs      = response.DurationMs,
                    ErrorDetails    = response.ErrorMessage,
                    CacheKey        = request.CacheKey,
                    PromptTokens    = response.PromptTokens,
                    CompletionTokens = response.CompletionTokens,
                    TotalTokens     = response.TotalTokens,
                    EstimatedCostUsd = response.EstimatedCostUsd
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record AI audit. JobId={JobId}", request.JobId);
            }
        }
    }
}
