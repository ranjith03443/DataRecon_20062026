using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.Application.Services
{
    public class SemanticSchemaEnrichmentService : ISemanticSchemaEnrichmentService
    {
        private readonly ILogger<SemanticSchemaEnrichmentService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public SemanticSchemaEnrichmentService(
            ILogger<SemanticSchemaEnrichmentService> logger,
            IArtifactPersistenceService artifactService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _artifactService = artifactService;
            _httpClient = httpClientFactory.CreateClient("PythonService");
            _configuration = configuration;

            var timeoutSec = _configuration.GetValue<int>("AI:PythonService:TimeoutSeconds", 30);
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        public async Task<SemanticSchemaProfile> EnrichSchemaAsync(string jobId, SourceSchemaProfile profile)
        {
            _logger.LogInformation("Starting semantic schema enrichment. JobId={JobId} Dataset={Dataset}", jobId, profile.Dataset);

            var enrichedProfile = new SemanticSchemaProfile
            {
                JobId = jobId,
                Dataset = profile.Dataset,
                EnrichedAt = DateTime.UtcNow
            };

            _logger.LogInformation("Enriching all {Count} fields via AI. JobId={JobId}", profile.Fields.Count, jobId);

            foreach (var field in profile.Fields)
            {
                var enrichment = new SemanticFieldEnrichment
                {
                    FieldName       = field.FieldName,
                    WasAIEnriched   = false,
                    ConfidenceScore = 0.0,
                    SemanticMeaning = field.PossibleMeaning,
                    BusinessCategory = InferBusinessCategory(field.FieldName)
                };

                var aiEnrichment = await CallSemanticEnrichmentAsync(jobId, field, profile.Dataset);
                if (aiEnrichment != null)
                {
                    enrichment.SemanticMeaning        = aiEnrichment.SemanticMeaning        ?? enrichment.SemanticMeaning;
                    enrichment.ExpandedAbbreviation   = aiEnrichment.ExpandedAbbreviation;
                    enrichment.BusinessCategory       = aiEnrichment.BusinessCategory       ?? enrichment.BusinessCategory;
                    enrichment.InterpretedDescription = aiEnrichment.InterpretedDescription;
                    enrichment.DataTypeHint           = aiEnrichment.DataTypeHint;
                    enrichment.ConfidenceScore        = aiEnrichment.ConfidenceScore;
                    enrichment.WasAIEnriched          = true;
                    _logger.LogInformation(
                        "Field enriched via /api/semantic-enrichment. JobId={JobId} Field={Field} Meaning={Meaning} Confidence={Conf}",
                        jobId, field.FieldName, enrichment.SemanticMeaning, enrichment.ConfidenceScore);
                }
                else
                {
                    _logger.LogWarning(
                        "AI enrichment unavailable for field — using deterministic fallback. JobId={JobId} Field={Field}",
                        jobId, field.FieldName);
                }

                enrichedProfile.EnrichedFields.Add(enrichment);
            }

            await PersistEnrichedProfileAsync(jobId, enrichedProfile);
            _logger.LogInformation("Semantic enrichment completed. JobId={JobId} EnrichedCount={Count}", jobId, enrichedProfile.EnrichedFields.Count);
            return enrichedProfile;
        }

        public async Task PersistEnrichedProfileAsync(string jobId, SemanticSchemaProfile profile)
        {
            var fileName = $"semantic_schema_profile_{profile.Dataset.Replace('.', '_')}.json";
            await _artifactService.PersistArtifactAsync(jobId, profile, ArtifactType.SemanticSchemaProfile, fileName);
        }

        // ── Calls the dedicated Python /api/semantic-enrichment endpoint ─────────
        private async Task<SemanticFieldEnrichment?> CallSemanticEnrichmentAsync(
            string jobId, SourceFieldProfile field, string dataset)
        {
            try
            {
                var baseUrl  = _configuration["AI:PythonService:BaseUrl"]              ?? "http://localhost:8000";
                var endpoint = _configuration["AI:PythonService:EnrichmentEndpoint"]   ?? "/api/semantic-enrichment";
                var fullUrl  = $"{baseUrl.TrimEnd('/')}{endpoint}";

                var additionalContext = $"dataset={dataset}, datatype={field.Datatype}";
                if (field.SampleValues.Any())
                    additionalContext += $", samples={string.Join(",", field.SampleValues.Take(3))}";

                var payload = new
                {
                    fieldName         = field.FieldName,
                    additionalContext,
                    jobId,
                    requestId         = $"enrich_{dataset}_{field.FieldName}"
                };

                var json     = JsonConvert.SerializeObject(payload);
                var content  = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogDebug("Calling /api/semantic-enrichment. Url={Url} Field={Field}", fullUrl, field.FieldName);

                var httpResponse = await _httpClient.PostAsync(fullUrl, content);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Enrichment endpoint returned {Status} for field {Field}. JobId={JobId}",
                        httpResponse.StatusCode, field.FieldName, jobId);
                    return null;
                }

                var responseBody = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogDebug("Enrichment response. Field={Field} Body={Body}", field.FieldName, responseBody);

                // Python response: { fieldName, possibleMeaning, businessCategory, description,
                //                    dataTypeHint, abbreviationsExpanded, confidence, confidenceLevel }
                // Map to SemanticFieldEnrichment domain model
                var dynamic = JsonConvert.DeserializeObject<dynamic>(responseBody);
                if (dynamic == null) return null;

                var enrichment = new SemanticFieldEnrichment
                {
                    FieldName             = field.FieldName,
                    SemanticMeaning       = (string?)dynamic.possibleMeaning   ?? (string?)dynamic.semanticMeaning,
                    BusinessCategory      = (string?)dynamic.businessCategory,
                    InterpretedDescription = (string?)dynamic.description,
                    ExpandedAbbreviation  = BuildExpandedAbbreviation(dynamic.abbreviationsExpanded),
                    DataTypeHint          = (string?)dynamic.dataTypeHint,
                    ConfidenceScore       = ParseDouble(dynamic.confidence?.ToString(), 0.85),
                    WasAIEnriched         = true
                };

                return enrichment;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI enrichment failed for field {FieldName}. JobId={JobId}", field.FieldName, jobId);
                return null;
            }
        }

        private static string? BuildExpandedAbbreviation(dynamic? abbreviationsExpanded)
        {
            if (abbreviationsExpanded == null) return null;
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    (string)abbreviationsExpanded.ToString());
                return dict != null ? string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}")) : null;
            }
            catch { return null; }
        }

        private static double ParseDouble(string? value, double fallback)
        {
            if (double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            return fallback;
        }

        private static string InferBusinessCategory(string fieldName) =>
            fieldName.ToUpperInvariant() switch
            {
                var n when n.Contains("CUST") || n.Contains("CLIENT") => "Customer",
                var n when n.Contains("LOAN") || n.Contains("CREDIT") || n.Contains("BAL") || n.Contains("AMT") => "Financial",
                var n when n.Contains("DATE") || n.Contains("DT") || n.Contains("OPEN") || n.Contains("CLOSE") => "Date",
                var n when n.Contains("ADDR") || n.Contains("CITY") || n.Contains("STATE") || n.Contains("ZIP") => "Address",
                var n when n.Contains("STATUS") || n.Contains("STAT") || n.Contains("FLAG") => "Status",
                var n when n.Contains("ACCT") || n.Contains("ACCOUNT") => "Account",
                _ => "General"
            };
    }
}
