using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class WorkflowApiController : ControllerBase
    {
        private readonly IWorkflowOrchestratorService _orchestrator;
        private readonly IWorkflowJobRepository _jobRepo;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly ILogger<WorkflowApiController> _logger;

        public WorkflowApiController(
            IWorkflowOrchestratorService orchestrator,
            IWorkflowJobRepository jobRepo,
            IArtifactPersistenceService artifactPersistence,
            ILogger<WorkflowApiController> logger)
        {
            _orchestrator = orchestrator;
            _jobRepo = jobRepo;
            _artifactPersistence = artifactPersistence;
            _logger = logger;
        }

        [HttpGet("{jobId}/status")]
        public async Task<IActionResult> GetStatus(string jobId)
        {
            var job = await _jobRepo.GetByJobIdAsync(jobId);
            if (job == null) return NotFound();

            return Ok(new
            {
                jobId = job.JobId,
                status = job.Status.ToString(),
                currentStep = job.CurrentStep.ToString(),
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                errorDetails = job.ErrorDetails
            });
        }

        [HttpGet("{jobId}/artifact/{artifactType}")]
        public async Task<IActionResult> GetArtifact(string jobId, string artifactType)
        {
            if (!Enum.TryParse<ArtifactType>(artifactType, out var type))
                return BadRequest($"Invalid artifact type: {artifactType}");

            var exists = await _artifactPersistence.ArtifactExistsAsync(jobId, type);
            if (!exists) return NotFound($"Artifact {artifactType} not found for job {jobId}");

            // Return artifact path info
            return Ok(new
            {
                jobId,
                artifactType,
                available = true
            });
        }

        [HttpPost("{jobId}/replay/{fromStep}")]
        public async Task<IActionResult> Replay(string jobId, string fromStep)
        {
            if (!Enum.TryParse<WorkflowStep>(fromStep, out var step))
                return BadRequest($"Invalid step: {fromStep}");

            try
            {
                var context = await _orchestrator.ReplayWorkflowAsync(jobId, step);
                return Ok(new
                {
                    jobId,
                    status = context.WorkflowStatus.ToString(),
                    isReplay = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay failed. JobId={JobId}", jobId);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
