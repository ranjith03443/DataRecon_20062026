using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Models;
using DataReconciliation.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Extensions.Http;
using System.Net.Http.Json;
using System.Text;

namespace DataReconciliation.AI.Clients
{
    public class OpenAIInferenceService : IAIInferenceService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenAIInferenceService> _logger;
        private readonly AICacheService _cacheService;
        private readonly IAIInferenceAuditRepository _auditRepo;
        private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
        private readonly IConfiguration _configuration;

        public OpenAIInferenceService(
            HttpClient httpClient,
            ILogger<OpenAIInferenceService> logger,
            AICacheService cacheService,
            IAIInferenceAuditRepository auditRepo,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _cacheService = cacheService;
            _auditRepo = auditRepo;
            _configuration = configuration;

            _retryPolicy = HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (outcome, timespan, attempt, ctx) =>
                    {
                        logger.LogWarning("AI retry attempt {Attempt} after {Delay}s. Reason: {Reason}",
                            attempt, timespan.TotalSeconds, outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                    });
        }

        public async Task<AIInferenceResponse> InferAsync(AIInferenceRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("AI inference request. JobId={JobId} Step={Step} CacheKey={CacheKey}",
                request.JobId, request.Step, request.CacheKey);

            // Check cache first
            if (_cacheService.TryGetResponse(request.CacheKey, out var cachedResponse) && cachedResponse != null)
            {
                _logger.LogInformation("AI cache hit. JobId={JobId} CacheKey={CacheKey}", request.JobId, request.CacheKey);
                await RecordAuditAsync(request, cachedResponse, wasCached: true);
                return cachedResponse;
            }

            var response = new AIInferenceResponse();

            try
            {
                var aiResponse = await CallAIEndpointAsync(request);
                response.Success = aiResponse.Success;
                response.Content = aiResponse.Content;
                response.ConfidenceScore = aiResponse.ConfidenceScore;
                response.DurationMs = sw.Elapsed.TotalMilliseconds;

                if (response.Success && response.ConfidenceScore >= 0.90)
                    _cacheService.SetResponse(request.CacheKey, response);

                _logger.LogInformation("AI inference completed. JobId={JobId} Confidence={Confidence} Duration={Duration}ms",
                    request.JobId, response.ConfidenceScore, response.DurationMs);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.ErrorMessage = ex.Message;
                sw.Stop();
                response.DurationMs = sw.Elapsed.TotalMilliseconds;

                _logger.LogError(ex, "AI inference failed. JobId={JobId} Step={Step}", request.JobId, request.Step);
            }

            await RecordAuditAsync(request, response, wasCached: false);
            return response;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var apiUrl = _configuration["AI:ApiUrl"];
                if (string.IsNullOrWhiteSpace(apiUrl)) return false;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var result = await _httpClient.GetAsync(apiUrl, cts.Token);
                return result.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<AIInferenceResponse> CallAIEndpointAsync(AIInferenceRequest request)
        {
            var apiUrl = _configuration["AI:ApiUrl"] ?? "https://api.openai.com/v1/chat/completions";
            var apiKey = _configuration["AI:ApiKey"] ?? string.Empty;
            var model = _configuration["AI:Model"] ?? "gpt-3.5-turbo";

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("AI API key not configured. Returning empty response. JobId={JobId}", request.JobId);
                return new AIInferenceResponse
                {
                    Success = false,
                    ErrorMessage = "AI API key not configured",
                    ConfidenceScore = 0
                };
            }

            var payload = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = "You are a precise data mapping assistant. Respond ONLY with valid JSON." },
                    new { role = "user", content = request.Prompt }
                },
                temperature = 0.1,
                max_tokens = 1024
            };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var httpResponse = await _retryPolicy.ExecuteAsync(() =>
                _httpClient.PostAsync(apiUrl, content));

            if (!httpResponse.IsSuccessStatusCode)
            {
                return new AIInferenceResponse
                {
                    Success = false,
                    ErrorMessage = $"AI API returned {httpResponse.StatusCode}"
                };
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var parsed = JsonConvert.DeserializeObject<dynamic>(responseJson);

            var messageContent = parsed?.choices?[0]?.message?.content?.ToString() ?? string.Empty;

            return new AIInferenceResponse
            {
                Success = true,
                Content = messageContent,
                ConfidenceScore = ExtractConfidence(messageContent)
            };
        }

        private static double ExtractConfidence(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;
            try
            {
                var obj = JsonConvert.DeserializeObject<dynamic>(content);
                var conf = obj?.confidence ?? obj?.confidenceScore;
                double score = 0;
                if (conf != null && double.TryParse(conf.ToString(), out score)) return score;
            }
            catch { }
            return 0.85; // Default moderate confidence
        }

        private async Task RecordAuditAsync(AIInferenceRequest request, AIInferenceResponse response, bool wasCached)
        {
            try
            {
                await _auditRepo.CreateAsync(new AIInferenceAudit
                {
                    JobId = request.JobId,
                    Step = request.Step,
                    PromptText = request.Prompt,
                    ResponseText = response.Content,
                    ConfidenceScore = response.ConfidenceScore,
                    WasCached = wasCached,
                    WasAccepted = response.Success,
                    RetryCount = response.RetryCount,
                    RequestedAt = DateTime.UtcNow,
                    RespondedAt = DateTime.UtcNow,
                    DurationMs = response.DurationMs,
                    ErrorDetails = response.ErrorMessage,
                    CacheKey = request.CacheKey
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record AI audit. JobId={JobId}", request.JobId);
            }
        }
    }
}
