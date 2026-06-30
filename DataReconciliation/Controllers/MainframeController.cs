using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class MainframeController : Controller
    {
        private readonly IMainframeArtifactGenerationService _generator;
        private readonly IMainframeAssetGenerationService _assetGenerator;
        private readonly IMainframeAiAgentService _aiAgent;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly ILogger<MainframeController> _logger;

        public MainframeController(
            IMainframeArtifactGenerationService generator,
            IMainframeAssetGenerationService assetGenerator,
            IMainframeAiAgentService aiAgent,
            IArtifactPersistenceService artifactPersistence,
            ILogger<MainframeController> logger)
        {
            _generator = generator;
            _assetGenerator = assetGenerator;
            _aiAgent = aiAgent;
            _artifactPersistence = artifactPersistence;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Index(string? jobId = null)
        {
            return View(new MainframeArtifactPageDto
            {
                JobId = jobId ?? string.Empty
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate(MainframeArtifactPageDto model)
        {
            if (string.IsNullOrWhiteSpace(model.JobId))
            {
                ModelState.AddModelError(nameof(model.JobId), "Please enter a workflow job id.");
            }

            if (!ModelState.IsValid)
            {
                return View("Index", model);
            }

            try
            {
                model.Result = await _generator.GenerateAsync(
                    model.JobId.Trim(),
                    model.Request.RecordName,
                    model.Request.ApplicationName,
                    model.Request.IncludeComments);

                TempData["Success"] = "Mainframe artifacts generated successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mainframe artifact generation failed. JobId={JobId}", model.JobId);
                TempData["Error"] = ex.Message;
                return View("Index", model);
            }

            return View("Index", model);
        }

        [HttpGet]
        public async Task<IActionResult> Download(string jobId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Content("Mainframe download failed: job id and file name are required.", "text/plain");
            }

            try
            {
                var filePath = await _generator.ResolveArtifactPathAsync(jobId.Trim(), fileName.Trim());
                if (filePath == null)
                {
                    _logger.LogWarning("Mainframe artifact download requested for missing file. JobId={JobId} FileName={FileName}", jobId, fileName);
                    Response.StatusCode = StatusCodes.Status404NotFound;
                    return Content(
                        $"Mainframe artifact not found for job '{jobId}'. Please regenerate the artifacts and try again.",
                        "text/plain");
                }

                var contentType = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "application/json"
                    : "text/plain";

                return PhysicalFile(filePath, contentType, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mainframe artifact download failed. JobId={JobId} FileName={FileName}", jobId, fileName);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Content(
                    "Mainframe artifact download failed due to an internal error. Please try again or regenerate the artifacts.",
                    "text/plain");
            }
        }

        // ── Mainframe Asset Generation Agent ──────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateAssets(MainframeAssetPageDto model)
        {
            if (string.IsNullOrWhiteSpace(model.JobId))
                ModelState.AddModelError(nameof(model.JobId), "Please enter a workflow job id.");

            if (!ModelState.IsValid)
                return View("Index", BuildCombinedPage(model));

            try
            {
                model.AssetResult = await _assetGenerator.GenerateAsync(
                    model.JobId.Trim(),
                    model.Request);

                TempData["AssetSuccess"] = "Mainframe assets generated successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mainframe asset generation failed. JobId={JobId}", model.JobId);
                TempData["AssetError"] = ex.Message;
            }

            return View("Index", BuildCombinedPage(model));
        }

        [HttpGet]
        public async Task<IActionResult> DownloadAsset(string jobId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Content("Asset download failed: job id and file name are required.", "text/plain");
            }

            try
            {
                var filePath = await _assetGenerator.ResolveAssetPathAsync(jobId.Trim(), fileName.Trim());
                if (filePath == null)
                {
                    _logger.LogWarning(
                        "Mainframe asset download requested for missing file. JobId={JobId} FileName={FileName}",
                        jobId, fileName);
                    Response.StatusCode = StatusCodes.Status404NotFound;
                    return Content(
                        $"Asset not found for job '{jobId}'. Please regenerate the assets and try again.",
                        "text/plain");
                }

                var contentType = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "application/json"
                    : "text/plain";

                return PhysicalFile(filePath, contentType, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mainframe asset download failed. JobId={JobId} FileName={FileName}", jobId, fileName);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Content(
                    "Asset download failed due to an internal error.",
                    "text/plain");
            }
        }

        // ── Mainframe Development AI Agent ────────────────────────────────────

        /// <summary>
        /// AJAX endpoint — called by the AI Agent panel in the Index view.
        /// Returns JSON: MainframeAiAgentPageDto { Success, ErrorMessage, Result }.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunAgentAction(
            [FromForm] string jobId,
            [FromForm] string agentType)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(agentType))
            {
                return Json(new MainframeAiAgentPageDto
                {
                    Success = false,
                    ErrorMessage = "Job ID and agent type are required."
                });
            }

            _logger.LogInformation(
                "Mainframe AI Agent action requested. JobId={JobId} AgentType={AgentType}",
                jobId, agentType);

            // Check service availability
            if (!await _aiAgent.IsAvailableAsync())
            {
                return Json(new MainframeAiAgentPageDto
                {
                    Success = false,
                    ErrorMessage =
                        "The Mainframe AI Agent service is unavailable. " +
                        "Please ensure the Python AI service is running on the configured address."
                });
            }

            try
            {
                var request = new MainframeAiAgentRequest
                {
                    JobId      = jobId.Trim(),
                    PromptType = agentType.Trim(),
                    RequestId  = HttpContext.TraceIdentifier,
                };

                var result = await _aiAgent.RunAgentAsync(request);

                if (result == null)
                {
                    return Json(new MainframeAiAgentPageDto
                    {
                        Success      = false,
                        ErrorMessage = "AI Agent returned no response. Check the Python service logs."
                    });
                }

                return Json(new MainframeAiAgentPageDto
                {
                    Success = true,
                    Result  = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Mainframe AI Agent action failed. JobId={JobId} AgentType={AgentType}",
                    jobId, agentType);

                return Json(new MainframeAiAgentPageDto
                {
                    Success      = false,
                    ErrorMessage = $"AI Agent failed: {ex.Message}"
                });
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Combines the artifact page model into the unified Index view model.
        /// Preserves any existing artifact Result already in TempData/ViewData.
        /// </summary>
        private static MainframeArtifactPageDto BuildCombinedPage(MainframeAssetPageDto assetModel)
        {
            return new MainframeArtifactPageDto
            {
                JobId = assetModel.JobId,
                AssetPageModel = assetModel
            };
        }
    }
}
