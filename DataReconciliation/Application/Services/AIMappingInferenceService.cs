using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DataReconciliation.Application.Services
{
    public class AIMappingInferenceService : IAIMappingInferenceService
    {
        private readonly ILogger<AIMappingInferenceService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly IAIInferenceService _aiService;
        private readonly IAIResponseValidator _responseValidator;
        private readonly IConfiguration _configuration;
        private const double ConfidenceThreshold = 0.85;

        public AIMappingInferenceService(
            ILogger<AIMappingInferenceService> logger,
            IArtifactPersistenceService artifactService,
            IAIInferenceService aiService,
            IAIResponseValidator responseValidator,
            IConfiguration configuration)
        {
            _logger = logger;
            _artifactService = artifactService;
            _aiService = aiService;
            _responseValidator = responseValidator;
            _configuration = configuration;
        }

        public async Task<AIMappingResponse> InferMappingsAsync(
            string jobId,
            MappingCandidatesDocument candidates,
            TargetMetadataProfile targetProfile,
            IEnumerable<SemanticSchemaProfile>? semanticProfiles = null,
            IEnumerable<SourceSchemaProfile>? sourceProfiles = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting AI mapping inference. JobId={JobId}", jobId);

            var response = new AIMappingResponse { JobId = jobId, GeneratedAt = DateTime.UtcNow };

            // Build lookup indexes from Step 5 enrichment and Step 3 source profiling
            var semanticIndex = semanticProfiles
                ?.SelectMany(p => p.EnrichedFields)
                .GroupBy(e => e.FieldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, SemanticFieldEnrichment>(StringComparer.OrdinalIgnoreCase);

            var sourceIndex = sourceProfiles
                ?.SelectMany(p => p.Fields.Select(f => (Profile: p, Field: f)))
                .GroupBy(x => x.Field.FieldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Field, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, SourceFieldProfile>(StringComparer.OrdinalIgnoreCase);

            // ── AI kill-switch ───────────────────────────────────────────────
            var aiEnabled = _configuration.GetValue<bool>("AI:Enabled", true);
            if (!aiEnabled)
            {
                _logger.LogWarning("AI is disabled via AI:Enabled=false. Skipping AI mapping. JobId={JobId}", jobId);
                await PersistAIMappingResponseAsync(jobId, response);
                return response;   // returns empty response; consolidation falls back to deterministic
            }

            // ── Log which provider is active ─────────────────────────────────
            var provider = _configuration["AI:Provider"] ?? "OpenAI";
            _logger.LogInformation("AI provider = {Provider}. JobId={JobId}", provider, jobId);

            // ── Guard: target profile must have fields for AI to map against ─
            if (targetProfile.Fields == null || targetProfile.Fields.Count == 0)
            {
                _logger.LogWarning(
                    "Target metadata profile has no fields — AI mapping skipped. " +
                    "Check that the uploaded target schema Excel has a 'TARGET FIELDS' column in its first sheet. " +
                    "JobId={JobId}", jobId);
                await PersistAIMappingResponseAsync(jobId, response);
                return response;
            }

            // Only invoke AI for unresolved or low-confidence mappings
            var unresolvedCandidates = candidates.Mappings
                .Where(m => m.Status == MappingStatus.MANUAL_REVIEW_REQUIRED ||
                            m.Status == MappingStatus.UNRESOLVED ||
                            m.Confidence < ConfidenceThreshold)
                .ToList();

            // ── Also add source fields that scored 0 against ALL targets and were never
            //    added to the candidates document (e.g. HANDY_NUM when abbreviation dict is empty).
            //    Without this, AI never gets a chance to map them.
            if (sourceProfiles != null)
            {
                var representedSources = candidates.Mappings
                    .Select(m => m.SourceField)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var sp in sourceProfiles)
                {
                    foreach (var sf in sp.Fields)
                    {
                        if (!representedSources.Contains(sf.FieldName))
                        {
                            unresolvedCandidates.Add(new MappingCandidate
                            {
                                SourceField   = sf.FieldName,
                                SourceDataset = sp.DatasetId,
                                MatchType     = "unresolved",
                                Confidence    = 0,
                                Status        = MappingStatus.UNRESOLVED
                            });
                            _logger.LogDebug(
                                "Source field had no deterministic candidate — adding for AI. JobId={JobId} Field={Field}",
                                jobId, sf.FieldName);
                        }
                    }
                }
            }

            _logger.LogInformation("AI inference required for {Count} mappings. JobId={JobId}", unresolvedCandidates.Count, jobId);

            foreach (var candidate in unresolvedCandidates)
            {
                try
                {
                    var aiEntry = await InferSingleMappingAsync(jobId, candidate, targetProfile, semanticIndex, sourceIndex);
                    if (aiEntry != null)
                    {
                        response.AiMappings.Add(aiEntry);
                        _logger.LogInformation("AI mapping inferred. JobId={JobId} Source={Source} Target={Target} Confidence={Confidence}",
                            jobId, candidate.SourceField, aiEntry.TargetField, aiEntry.Confidence);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI mapping failed for field {SourceField}. JobId={JobId} - continuing workflow.",
                        candidate.SourceField, jobId);
                    // Do not fail entire workflow
                }
            }

            sw.Stop();
            _logger.LogInformation("AI mapping inference completed. JobId={JobId} AIMappings={Count} Duration={Duration}ms",
                jobId, response.AiMappings.Count, sw.ElapsedMilliseconds);

            await PersistAIMappingResponseAsync(jobId, response);
            return response;
        }

        public async Task PersistAIMappingResponseAsync(string jobId, AIMappingResponse response)
        {
            await _artifactService.PersistArtifactAsync(jobId, response, ArtifactType.AIMappingResponse, "ai_mapping_response.json");
        }

        private async Task<AIMappingEntry?> InferSingleMappingAsync(
            string jobId, MappingCandidate candidate, TargetMetadataProfile targetProfile,
            Dictionary<string, SemanticFieldEnrichment>? semanticIndex = null,
            Dictionary<string, SourceFieldProfile>? sourceIndex = null)
        {
            // Resolve enriched metadata from Step 5 (SemanticSchemaEnrichment) and Step 3 (SourceSchemaProfiling)
            SemanticFieldEnrichment? enrichment = null;
            semanticIndex?.TryGetValue(candidate.SourceField, out enrichment);

            SourceFieldProfile? sourceFieldProfile = null;
            sourceIndex?.TryGetValue(candidate.SourceField, out sourceFieldProfile);

            var sourceDatatype    = enrichment?.DataTypeHint
                                    ?? sourceFieldProfile?.Datatype
                                    ?? "string";
            var sourceDescription = enrichment?.InterpretedDescription
                                    ?? enrichment?.SemanticMeaning
                                    ?? candidate.Notes
                                    ?? string.Empty;
            var sampleValues      = sourceFieldProfile?.SampleValues?.Take(5).Cast<object>().ToArray()
                                    ?? Array.Empty<object>();
            var nullable          = sourceFieldProfile?.Nullable ?? true;

            _logger.LogDebug(
                "AI metadata resolved. JobId={JobId} Field={Field} Datatype={Datatype} Description={Desc} Samples={Samples} WasAIEnriched={Enriched}",
                jobId, candidate.SourceField, sourceDatatype, sourceDescription, sampleValues.Length,
                enrichment?.WasAIEnriched ?? false);

            var targetContext = string.Join("\n", targetProfile.Fields.Select(f =>
                $"  - {f.FieldName}: {f.Datatype}, {f.Description}"));

            var prompt = $@"You are a data mapping expert for mainframe data onboarding.
Map the source field to the most appropriate target field.

Source Field: {candidate.SourceField}
Source Dataset: {candidate.SourceDataset}

Target Fields:
{targetContext}

Respond with JSON only:
{{
  ""sourceField"": ""{candidate.SourceField}"",
  ""targetField"": ""<best matching target field name>"",
  ""transformationRule"": ""<DIRECT_MAPPING|DATE_CONVERSION|VALUE_MAPPING|NUMERIC_FORMAT>"",
  ""confidence"": <0.0 to 1.0>,
  ""reasoning"": ""<brief explanation>""
}}";

            // Target field names as candidates for the Python /api/semantic-mapping endpoint
            var targetCandidates = targetProfile.Fields
                .Select(f => f.FieldName)
                .ToList<object>();

            var request = new AIInferenceRequest
            {
                JobId = jobId,
                Prompt = prompt,
                Step = WorkflowStep.AISemanticMapping,
                CacheKey = $"map_{candidate.SourceField}_{candidate.SourceDataset}",
                Context = new Dictionary<string, object>
                {
                    { "sourceField",           candidate.SourceField },
                    { "existingConfidence",    candidate.Confidence },
                    { "targetFieldCandidates", targetCandidates },
                    { "sourceDatatype",        sourceDatatype },
                    { "sourceDescription",     sourceDescription },
                    { "sampleValues",          sampleValues },
                    { "nullable",              nullable }
                }
            };

            var aiResponse = await _aiService.InferAsync(request);
            if (!aiResponse.Success || string.IsNullOrWhiteSpace(aiResponse.Content)) return null;

            if (!_responseValidator.ValidateStructure(aiResponse.Content))
            {
                _logger.LogWarning("AI response validation failed. JobId={JobId} SourceField={SourceField}", jobId, candidate.SourceField);
                return null;
            }

            var entry = _responseValidator.DeserializeAndValidate<AIMappingEntry>(aiResponse.Content);
            if (entry == null) return null;

            entry.Confidence = aiResponse.ConfidenceScore > 0 ? aiResponse.ConfidenceScore : entry.Confidence;
            entry.ConfidenceLevel = entry.Confidence > 0.90 ? ConfidenceLevel.High
                : entry.Confidence >= 0.70 ? ConfidenceLevel.Medium
                : ConfidenceLevel.Low;

            // Carry the source dataset from the candidate — AIMappingEntry JSON never includes it
            entry.SourceDataset = candidate.SourceDataset;

            return entry;
        }
    }
}
