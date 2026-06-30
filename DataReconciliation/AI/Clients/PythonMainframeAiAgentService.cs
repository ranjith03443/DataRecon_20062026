using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.AI.Clients
{
    /// <summary>
    /// Calls the Python Mainframe Development AI Agent endpoints.
    ///
    /// Python service contract — POST {BaseUrl}/api/mainframe/{agent-type}:
    ///   Request body: MainframeAgentRequestDTO (JSON)
    ///   Response body: MainframeAgentResponseDTO (JSON)
    ///
    /// Endpoint map:
    ///   explain          → POST /api/mainframe/explain
    ///   review           → POST /api/mainframe/review
    ///   enhance          → POST /api/mainframe/enhance
    ///   validation       → POST /api/mainframe/validation
    ///   error_handling   → POST /api/mainframe/error-handling
    ///   documentation    → POST /api/mainframe/documentation
    ///   jcl_improvements → POST /api/mainframe/jcl-improvements
    ///   optimization     → POST /api/mainframe/optimization
    ///
    /// All responses carry governance notice:
    ///   "AI Generated Developer Guidance | Review Required"
    /// </summary>
    public class PythonMainframeAiAgentService : IMainframeAiAgentService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PythonMainframeAiAgentService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IFileIngestionService _fileIngestionService;

        // ── Endpoint map ──────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> _endpointMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "explain",          "/api/mainframe/explain"          },
                { "review",           "/api/mainframe/review"           },
                { "enhance",          "/api/mainframe/enhance"          },
                { "validation",       "/api/mainframe/validation"       },
                { "error_handling",   "/api/mainframe/error-handling"   },
                { "documentation",    "/api/mainframe/documentation"    },
                { "jcl_improvements", "/api/mainframe/jcl-improvements" },
                { "optimization",     "/api/mainframe/optimization"     },
            };

        public PythonMainframeAiAgentService(
            HttpClient httpClient,
            ILogger<PythonMainframeAiAgentService> logger,
            IConfiguration configuration,
            IArtifactPersistenceService artifactPersistence,
            IFileIngestionService fileIngestionService)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _artifactPersistence = artifactPersistence;
            _fileIngestionService = fileIngestionService;

            var timeoutSec = _configuration.GetValue<int>("AI:PythonService:TimeoutSeconds", 120);
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        // ── IMainframeAiAgentService ──────────────────────────────────────────

        public async Task<MainframeAiAgentResponse?> RunAgentAsync(MainframeAiAgentRequest request)
        {
            var promptType = (request.PromptType ?? "").ToLowerInvariant().Trim();

            if (!_endpointMap.TryGetValue(promptType, out var endpoint))
            {
                _logger.LogWarning(
                    "Unknown mainframe agent prompt type: {PromptType}. Supported: {Supported}",
                    promptType, string.Join(", ", _endpointMap.Keys));
                return null;
            }

            // ── Load artifacts from job folder ────────────────────────────────
            await LoadJobArtifactsAsync(request);

            // ── Build payload ─────────────────────────────────────────────────
            var payload = new
            {
                jobId           = request.JobId,
                promptType      = request.PromptType,
                cobolContent    = request.CobolContent,
                jclContent      = request.JclContent,
                copybookContent = request.CopybookContent,
                transformationRules = request.TransformationRules,
                valueMappings   = request.ValueMappings,
                fieldMappings   = request.FieldMappings,
                targetSchema    = request.TargetSchema,
                requestId       = request.RequestId,
                userId          = request.UserId,
            };

            var baseUrl  = _configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
            var fullUrl  = $"{baseUrl.TrimEnd('/')}{endpoint}";
            var json     = JsonConvert.SerializeObject(payload);
            var content  = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling Mainframe AI Agent. Url={Url} JobId={JobId} PromptType={PromptType}",
                fullUrl, request.JobId, promptType);

            try
            {
                var httpResponse = await _httpClient.PostAsync(fullUrl, content);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    var error = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Mainframe AI Agent returned error. Status={Status} Body={Body}",
                        httpResponse.StatusCode, error[..Math.Min(500, error.Length)]);
                    return null;
                }

                var responseBody = await httpResponse.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<MainframeAiAgentResponse>(responseBody);

                if (result != null)
                {
                    _logger.LogInformation(
                        "Mainframe AI Agent completed. JobId={JobId} PromptType={PromptType} " +
                        "Confidence={Confidence} Recommendations={Count}",
                        request.JobId, promptType, result.Confidence, result.Recommendations.Count);
                }

                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "Mainframe AI Agent service unreachable. JobId={JobId} PromptType={PromptType}",
                    request.JobId, promptType);
                return null;
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning(
                    "Mainframe AI Agent request timed out. JobId={JobId} PromptType={PromptType}",
                    request.JobId, promptType);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Mainframe AI Agent unexpected error. JobId={JobId} PromptType={PromptType}",
                    request.JobId, promptType);
                return null;
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                // Use /api/ping — instant liveness probe with no external provider calls.
                // Avoids false negatives caused by the heavyweight /api/health endpoint
                // timing out while making LLM / embedding / vector-store checks.
                var baseUrl = _configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await _httpClient.GetAsync($"{baseUrl.TrimEnd('/')}/api/ping", cts.Token);
                return result.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Mainframe AI Agent availability check failed — Python service may be starting up.");
                return false;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Loads available artifacts from the job's workflow folder
        /// and populates the request's content properties if not already set.
        /// Text asset files come from workflow/{jobId}/reports/mainframe-assets/.
        /// JSON artifacts come from IArtifactPersistenceService.
        /// </summary>
        private async Task LoadJobArtifactsAsync(MainframeAiAgentRequest request)
        {
            var jobId = request.JobId;

            try
            {
                // ── Text asset files (COBOL, JCL, copybook) ──────────────────
                var assetFolder = await ResolveAssetFolderAsync(jobId);
                if (Directory.Exists(assetFolder))
                {
                    if (string.IsNullOrWhiteSpace(request.CopybookContent))
                    {
                        var cpyFile = FindLatestFile(assetFolder, "*.cpy");
                        if (cpyFile != null)
                            request.CopybookContent = await File.ReadAllTextAsync(cpyFile);
                    }

                    if (string.IsNullOrWhiteSpace(request.CobolContent))
                    {
                        var cblFile = FindLatestFile(assetFolder, "*.cbl");
                        if (cblFile != null)
                            request.CobolContent = await File.ReadAllTextAsync(cblFile);
                    }

                    if (string.IsNullOrWhiteSpace(request.JclContent))
                    {
                        var jclFile = FindLatestFile(assetFolder, "*.jcl");
                        if (jclFile != null)
                            request.JclContent = await File.ReadAllTextAsync(jclFile);
                    }
                }

                // Also check the mainframe artifacts folder for copybook from earlier step
                var artifactFolder = await ResolveArtifactFolderAsync(jobId);
                if (Directory.Exists(artifactFolder) && string.IsNullOrWhiteSpace(request.CopybookContent))
                {
                    var cpyFile = FindLatestFile(artifactFolder, "*.cpy");
                    if (cpyFile != null)
                        request.CopybookContent = await File.ReadAllTextAsync(cpyFile);
                }

                // ── JSON artifacts ────────────────────────────────────────────
                if (request.FieldMappings == null)
                {
                    var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                        jobId, ArtifactType.FinalMappingConfig);
                    if (finalMapping?.Mappings != null)
                    {
                        request.FieldMappings = finalMapping.Mappings
                            .Select(m => new Dictionary<string, object?>
                            {
                                { "targetField",  m.TargetField  },
                                { "sourceField",  m.SourceField  },
                                { "operation",    m.Transformations.FirstOrDefault()?.Operation },
                                { "matchSource",  m.MatchSource  },
                                { "confidence",   m.Confidence   },
                            })
                            .Cast<Dictionary<string, object?>>()
                            .ToList();
                    }
                }

                if (request.ValueMappings == null)
                {
                    var valueMappings = await _artifactPersistence.LoadArtifactAsync<ValueMappingsDocument>(
                        jobId, ArtifactType.ValueMappings);
                    if (valueMappings?.Fields != null)
                    {
                        request.ValueMappings = valueMappings.Fields
                            .ToDictionary(
                                f => f.TargetField,
                                f => (object?)f.Entries.ToDictionary(
                                    e => e.SourceValue,
                                    e => e.TargetValue));
                    }
                }

                if (request.TargetSchema == null)
                {
                    var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                        jobId, ArtifactType.TargetMetadataProfile);
                    if (targetProfile != null)
                    {
                        request.TargetSchema = new Dictionary<string, object?>
                        {
                            { "targetDataset", targetProfile.TargetDataset },
                            { "fieldCount",    targetProfile.Fields?.Count  },
                        };
                    }
                }

                _logger.LogDebug(
                    "Mainframe AI Agent artifacts loaded. JobId={JobId} " +
                    "HasCOBOL={HasCOBOL} HasJCL={HasJCL} HasCopybook={HasCopybook} " +
                    "FieldMappings={Fields} ValueMappings={Values}",
                    jobId,
                    !string.IsNullOrEmpty(request.CobolContent),
                    !string.IsNullOrEmpty(request.JclContent),
                    !string.IsNullOrEmpty(request.CopybookContent),
                    request.FieldMappings?.Count ?? 0,
                    request.ValueMappings?.Count ?? 0);
            }
            catch (Exception ex)
            {
                // Artifact loading is best-effort — the agent can still run with partial data
                _logger.LogWarning(ex,
                    "Mainframe AI Agent artifact loading partially failed (non-fatal). JobId={JobId}",
                    jobId);
            }
        }

        private async Task<string> ResolveAssetFolderAsync(string jobId)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            return Path.GetFullPath(Path.Combine(reportsPath, "mainframe-assets"));
        }

        private async Task<string> ResolveArtifactFolderAsync(string jobId)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            return Path.GetFullPath(Path.Combine(reportsPath, "mainframe"));
        }

        private static string? FindLatestFile(string folder, string pattern)
        {
            return Directory.GetFiles(folder, pattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
