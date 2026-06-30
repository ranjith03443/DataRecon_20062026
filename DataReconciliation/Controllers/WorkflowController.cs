using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class WorkflowController : Controller
    {
        private readonly IWorkflowOrchestratorService _orchestrator;
        private readonly IWorkflowJobRepository _jobRepo;
        private readonly IWorkflowStepExecutionRepository _stepRepo;
        private readonly IWorkflowArtifactRepository _artifactRepo;
        private readonly IFileIngestionService _fileIngestion;
        private readonly IDatasetRegistrationService _datasetRegistration;
        private readonly ILogger<WorkflowController> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public WorkflowController(
            IWorkflowOrchestratorService orchestrator,
            IWorkflowJobRepository jobRepo,
            IWorkflowStepExecutionRepository stepRepo,
            IWorkflowArtifactRepository artifactRepo,
            IFileIngestionService fileIngestion,
            IDatasetRegistrationService datasetRegistration,
            ILogger<WorkflowController> logger,
            IServiceScopeFactory scopeFactory)
        {
            _orchestrator = orchestrator;
            _jobRepo = jobRepo;
            _stepRepo = stepRepo;
            _artifactRepo = artifactRepo;
            _fileIngestion = fileIngestion;
            _datasetRegistration = datasetRegistration;
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var jobs = await _jobRepo.GetAllAsync();
            var dtos = jobs.Select(j => new WorkflowJobDto
            {
                JobId = j.JobId,
                JobName = j.JobName,
                Status = j.Status.ToString(),
                CurrentStep = j.CurrentStep.ToString(),
                CreatedAt = j.CreatedAt,
                CompletedAt = j.CompletedAt,
                ErrorDetails = j.ErrorDetails
            }).ToList();
            return View(dtos);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromForm] CreateWorkflowRequest request)
        {
            if (!ModelState.IsValid)
                return View(request);

            var datasets = request.Datasets ?? new List<DatasetUploadDto>();
            if (!datasets.Any())
            {
                ModelState.AddModelError("", "Please upload at least one source dataset and one target schema file.");
                return View(request);
            }

            var hasTargetSchema = datasets.Any(d =>
                Enum.TryParse<DatasetRole>(d.DatasetRole, out var role) && role == DatasetRole.TARGET_SCHEMA);
            if (!hasTargetSchema)
            {
                ModelState.AddModelError("", "Target schema file is required (DatasetRole = TARGET_SCHEMA).");
                return View(request);
            }

            var hasSourceDataset = datasets.Any(d =>
                Enum.TryParse<DatasetRole>(d.DatasetRole, out var role) && role == DatasetRole.SOURCE);
            if (!hasSourceDataset)
            {
                ModelState.AddModelError("", "At least one source dataset is required (DatasetRole = SOURCE).");
                return View(request);
            }

            var jobId = $"JOB_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}";
            var manifest = new DatasetManifest
            {
                JobId = jobId,
                JobName = request.JobName,
                CreatedAt = DateTime.UtcNow
            };

            // Ingest uploaded files
            foreach (var dataset in datasets)
            {
                if (dataset.File == null || dataset.File.Length == 0)
                {
                    ModelState.AddModelError("", $"File missing for dataset {dataset.DatasetId}");
                    return View(request);
                }

                if (!Enum.TryParse<DatasetRole>(dataset.DatasetRole, out var role))
                {
                    ModelState.AddModelError("", $"Invalid dataset role: {dataset.DatasetRole}");
                    return View(request);
                }

                if (!Enum.TryParse<DatasetType>(dataset.DatasetType, out var type))
                {
                    ModelState.AddModelError("", $"Invalid dataset type: {dataset.DatasetType}");
                    return View(request);
                }

                var filePath = await _fileIngestion.IngestFileAsync(jobId, dataset.File.OpenReadStream(), dataset.File.FileName, role);

                manifest.Datasets.Add(new DatasetRegistration
                {
                    DatasetId = dataset.DatasetId,
                    FileName = dataset.File.FileName,
                    DatasetRole = role,
                    DatasetType = type,
                    DatasetAlias = dataset.DatasetAlias,
                    DatasetDomain = dataset.DatasetDomain,
                    LocalPath = filePath
                });

                _logger.LogInformation("File ingested. JobId={JobId} DatasetId={DatasetId} FileName={FileName}",
                    jobId, dataset.DatasetId, dataset.File.FileName);
            }

            // Pre-create the job record in DB so the Details page finds it immediately
            await _jobRepo.CreateAsync(new DataReconciliation.Domain.Entities.WorkflowJob
            {
                JobId = jobId,
                JobName = request.JobName,
                Status = DataReconciliation.Domain.Enums.WorkflowStatus.Pending,
                CorrelationId = Guid.NewGuid().ToString()
            });

            // ── Ingest optional parameter files (non-blocking; no workflow role required) ──
            if (request.SourceParameterFile != null && request.SourceParameterFile.Length > 0)
            {
                var paramPath = await _fileIngestion.IngestFileAsync(
                    jobId, request.SourceParameterFile.OpenReadStream(),
                    "source_parameters" + Path.GetExtension(request.SourceParameterFile.FileName), DatasetRole.SOURCE);
                _logger.LogInformation("Source parameter file ingested. JobId={JobId} Path={Path}", jobId, paramPath);
                manifest.SourceParameterFilePath = paramPath;
            }

            if (request.TargetParameterFile != null && request.TargetParameterFile.Length > 0)
            {
                var paramPath = await _fileIngestion.IngestFileAsync(
                    jobId, request.TargetParameterFile.OpenReadStream(),
                    "target_parameters" + Path.GetExtension(request.TargetParameterFile.FileName), DatasetRole.TARGET_SCHEMA);
                _logger.LogInformation("Target parameter file ingested. JobId={JobId} Path={Path}", jobId, paramPath);
                manifest.TargetParameterFilePath = paramPath;
            }

            if (request.ValueMappingsExcelFile != null && request.ValueMappingsExcelFile.Length > 0)
            {
                var extension = Path.GetExtension(request.ValueMappingsExcelFile.FileName).ToLowerInvariant();
                if (extension is not ".xlsx" and not ".xls")
                {
                    ModelState.AddModelError("", "Value mappings seed file must be .xlsx or .xls.");
                    return View(request);
                }

                var vmPath = await _fileIngestion.IngestFileAsync(
                    jobId,
                    request.ValueMappingsExcelFile.OpenReadStream(),
                    "value_mappings_seed" + extension,
                    DatasetRole.SOURCE);

                _logger.LogInformation("Value mappings seed Excel ingested. JobId={JobId} Path={Path}", jobId, vmPath);
                manifest.ValueMappingsExcelFilePath = vmPath;
            }

            // Start workflow asynchronously using a NEW DI scope (avoids disposed DbContext)
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestratorService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WorkflowController>>();
                try
                {
                    await orchestrator.StartWorkflowAsync(manifest);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background workflow failed. JobId={JobId}", jobId);
                }
            });

            TempData["Success"] = $"Workflow started successfully. Job ID: {jobId}";
            return RedirectToAction(nameof(Details), new { jobId });
        }

        [HttpGet]
        public async Task<IActionResult> Details(string jobId)
        {
            var job = await _jobRepo.GetWithStepsAsync(jobId);
            if (job == null) return NotFound();

            var steps = await _stepRepo.GetByJobIdAsync(jobId);
            var artifacts = await _artifactRepo.GetByJobIdAsync(jobId);

            var dto = new WorkflowDetailsDto
            {
                Job = new WorkflowJobDto
                {
                    JobId = job.JobId,
                    JobName = job.JobName,
                    Status = job.Status.ToString(),
                    CurrentStep = job.CurrentStep.ToString(),
                    CreatedAt = job.CreatedAt,
                    CompletedAt = job.CompletedAt,
                    ErrorDetails = job.ErrorDetails
                },
                Steps = steps.Select(s => new WorkflowStepDto
                {
                    Step = s.Step.ToString(),
                    Status = s.Status.ToString(),
                    StartedAt = s.StartedAt,
                    CompletedAt = s.CompletedAt,
                    DurationMs = s.DurationMs,
                    RetryCount = s.RetryCount,
                    ErrorDetails = s.ErrorDetails
                }).ToList(),
                Artifacts = artifacts.Select(a => new ArtifactDto
                {
                    ArtifactType = a.ArtifactType.ToString(),
                    ArtifactName = a.ArtifactName,
                    FilePath = a.FilePath,
                    CreatedAt = a.CreatedAt,
                    Version = a.Version
                }).ToList()
            };

            return View(dto);
        }

        [HttpGet]
        public async Task<IActionResult> Status(string jobId)
        {
            var job = await _jobRepo.GetByJobIdAsync(jobId);
            if (job == null) return NotFound();

            return Json(new
            {
                jobId = job.JobId,
                status = job.Status.ToString(),
                currentStep = job.CurrentStep.ToString(),
                completedAt = job.CompletedAt
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ConfirmMappings(string jobId)
        {
            _logger.LogInformation("Mapping approval confirmed. JobId={JobId}", jobId);

            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestratorService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WorkflowController>>();
                try
                {
                    // Resume from TransformationExecution — mappings are already consolidated
                    await orchestrator.ReplayWorkflowAsync(jobId, WorkflowStep.TransformationExecution);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Post-approval workflow failed. JobId={JobId}", jobId);
                }
            });

            TempData["Success"] = "Mappings confirmed. Transformation and reconciliation are now running — refresh to track progress.";
            return RedirectToAction(nameof(Details), new { jobId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Replay(string jobId, WorkflowStep fromStep)
        {
            _logger.LogInformation("Replay requested. JobId={JobId} FromStep={Step}", jobId, fromStep);

            // Run in a background scope — same pattern as GenerateFile — so the HTTP
            // request returns immediately instead of blocking until all steps complete.
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestratorService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WorkflowController>>();
                try
                {
                    await orchestrator.ReplayWorkflowAsync(jobId, fromStep);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Replay failed. JobId={JobId} FromStep={Step}", jobId, fromStep);
                }
            });

            TempData["Success"] = $"Replay from step {fromStep} started — refresh this page to track progress.";
            return RedirectToAction(nameof(Details), new { jobId });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadArtifact(string jobId, string artifactType)
        {
            var artifacts = await _artifactRepo.GetByJobIdAsync(jobId);
            var artifact = artifacts.FirstOrDefault(a => a.ArtifactType.ToString() == artifactType);

            if (artifact == null || !System.IO.File.Exists(artifact.FilePath))
                return NotFound();

            var bytes = await System.IO.File.ReadAllBytesAsync(artifact.FilePath);
            var contentType = artifact.FilePath.EndsWith(".dat") ? "application/octet-stream" : "application/json";
            return File(bytes, contentType, artifact.ArtifactName);
        }

        // ── Folder-scan endpoint ──────────────────────────────────────────────

        [HttpPost]
        public IActionResult ScanFolder([FromBody] ScanFolderRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.FolderPath))
                return BadRequest(new { error = "Folder path is required." });

            if (!Directory.Exists(request.FolderPath))
                return BadRequest(new { error = $"Folder not found: {request.FolderPath}" });

            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".csv", ".xlsx", ".xls", ".json", ".txt", ".dat" };

            var results = Directory.GetFiles(request.FolderPath)
                .Select(fp =>
                {
                    var info = new FileInfo(fp);
                    var ext  = info.Extension.ToLowerInvariant();
                    if (!supported.Contains(ext))
                        return new ScannedFileDto { FileName = info.Name, IsUnsupported = true, FileSizeBytes = info.Length, Extension = ext, DatasetId = Path.GetFileNameWithoutExtension(info.Name) };

                    return ClassifyFile(info.Name, info.Length);
                })
                .ToList();

            return Json(results);
        }

        private static ScannedFileDto ClassifyFile(string fileName, long sizeBytes)
        {
            var stem     = Path.GetFileNameWithoutExtension(fileName);
            var ext      = Path.GetExtension(fileName).ToLowerInvariant();
            var stemLow  = stem.ToLowerInvariant();

            string Strip(string prefix) => stem.Length > prefix.Length ? stem[prefix.Length..] : stem;

            // Parameter files (check before generic src_/tgt_ prefixes)
            if (stemLow.StartsWith("src_param_"))
                return Param("SOURCE", Strip("src_param_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("source_param_"))
                return Param("SOURCE", Strip("source_param_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("tgt_param_"))
                return Param("TARGET", Strip("tgt_param_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("target_param_"))
                return Param("TARGET", Strip("target_param_"), fileName, sizeBytes, ext);

            // Value mapping seed excel
            if ((stemLow.StartsWith("value_mapping_") || stemLow.StartsWith("value_mappings_") || stemLow.StartsWith("vm_"))
                && (ext == ".xlsx" || ext == ".xls"))
                return ValueMapping(fileName, sizeBytes, ext);

            // Historical mappings
            if (stemLow.StartsWith("historical_rules_"))
                return Dataset("HISTORICAL_MAPPINGS", "JSON", Strip("historical_rules_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("historic_rules_"))
                return Dataset("HISTORICAL_MAPPINGS", "JSON", Strip("historic_rules_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("historical_"))
                return Dataset("HISTORICAL_MAPPINGS", "JSON", Strip("historical_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("historic_"))
                return Dataset("HISTORICAL_MAPPINGS", "JSON", Strip("historic_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("hist_"))
                return Dataset("HISTORICAL_MAPPINGS", "JSON", Strip("hist_"), fileName, sizeBytes, ext);

            // Mapping rules
            if (stemLow.StartsWith("mapping_rules_"))
                return Dataset("MAPPING_RULES", "CSV", Strip("mapping_rules_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("rules_"))
                return Dataset("MAPPING_RULES", "CSV", Strip("rules_"), fileName, sizeBytes, ext);

            // Target schema
            if (stemLow.StartsWith("target_"))
            {
                var dtype = (ext == ".xlsx" || ext == ".xls") ? "EXCEL" : "CSV";
                return Dataset("TARGET_SCHEMA", dtype, Strip("target_"), fileName, sizeBytes, ext);
            }
            if (stemLow.StartsWith("tgt_"))
            {
                var dtype = (ext == ".xlsx" || ext == ".xls") ? "EXCEL" : "CSV";
                return Dataset("TARGET_SCHEMA", dtype, Strip("tgt_"), fileName, sizeBytes, ext);
            }

            // Source
            if (stemLow.StartsWith("source_"))
                return Dataset("SOURCE", "CSV", Strip("source_"), fileName, sizeBytes, ext);
            if (stemLow.StartsWith("src_"))
                return Dataset("SOURCE", "CSV", Strip("src_"), fileName, sizeBytes, ext);

            // Unclassified
            return new ScannedFileDto
            {
                FileName = fileName, DatasetId = stem, IsUnclassified = true,
                FileSizeBytes = sizeBytes, Extension = ext
            };
        }

        private static ScannedFileDto Dataset(string role, string dtype, string id, string fileName, long size, string ext) =>
            new() { FileName = fileName, DetectedRole = role, DatasetId = id, DatasetType = dtype, FileSizeBytes = size, Extension = ext };

        private static ScannedFileDto Param(string paramRole, string id, string fileName, long size, string ext) =>
            new() { FileName = fileName, IsParameterFile = true, ParameterFileRole = paramRole, DatasetId = id, FileSizeBytes = size, Extension = ext };

        private static ScannedFileDto ValueMapping(string fileName, long size, string ext) =>
            new() { FileName = fileName, IsValueMappingFile = true, DatasetId = Path.GetFileNameWithoutExtension(fileName), FileSizeBytes = size, Extension = ext };

        // ── Create from folder ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateFromFolder([FromForm] CreateFromFolderRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.FolderPath) || !Directory.Exists(request.FolderPath))
            {
                ModelState.AddModelError("", $"Folder not found: {request.FolderPath}");
                return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
            }

            var datasets = (request.Datasets ?? new List<FolderDatasetAssignmentDto>())
                .Where(d => !string.IsNullOrEmpty(d.DatasetRole))
                .ToList();

            if (!datasets.Any(d => d.DatasetRole == "TARGET_SCHEMA"))
            {
                ModelState.AddModelError("", "A TARGET_SCHEMA file is required. Ensure a file with the 'target_' or 'tgt_' prefix is present and checked.");
                return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
            }
            if (!datasets.Any(d => d.DatasetRole == "SOURCE"))
            {
                ModelState.AddModelError("", "At least one SOURCE file is required. Ensure a file with the 'src_' or 'source_' prefix is present and checked.");
                return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
            }

            var jobId = $"JOB_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}";
            var manifest = new DatasetManifest { JobId = jobId, JobName = request.JobName, CreatedAt = DateTime.UtcNow };

            foreach (var ds in datasets)
            {
                var sourceFile = Path.Combine(request.FolderPath, ds.FileName);
                if (!System.IO.File.Exists(sourceFile))
                {
                    ModelState.AddModelError("", $"File not found in folder: {ds.FileName}");
                    return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                }
                if (!Enum.TryParse<DatasetRole>(ds.DatasetRole, out var role))
                {
                    ModelState.AddModelError("", $"Invalid role: {ds.DatasetRole}");
                    return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                }
                if (!Enum.TryParse<DatasetType>(ds.DatasetType, out var type))
                {
                    ModelState.AddModelError("", $"Invalid type: {ds.DatasetType}");
                    return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                }

                try
                {
                    await using var stream = OpenFileWithRetry(sourceFile);
                    var filePath = await _fileIngestion.IngestFileAsync(jobId, stream, ds.FileName, role);
                    manifest.Datasets.Add(new DatasetRegistration
                    {
                        DatasetId = ds.DatasetId,
                        FileName  = ds.FileName,
                        DatasetRole   = role,
                        DatasetType   = type,
                        DatasetAlias  = ds.DatasetAlias,
                        DatasetDomain = ds.DatasetDomain,
                        LocalPath     = filePath
                    });
                    _logger.LogInformation("Folder file ingested. JobId={JobId} DatasetId={DatasetId} File={File}", jobId, ds.DatasetId, ds.FileName);
                }
                catch (IOException ioEx) when (ioEx.Message.Contains("used by another process"))
                {
                    ModelState.AddModelError("", $"File is locked by another process: {ds.FileName}. Please close any open files in Excel or other applications and try again.");
                    _logger.LogWarning("File locked. JobId={JobId} File={File} Error={Error}", jobId, ds.FileName, ioEx.Message);
                    return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                }
            }

            // Optional parameter files
            if (!string.IsNullOrEmpty(request.SourceParameterFilePath))
            {
                var full = Path.Combine(request.FolderPath, request.SourceParameterFilePath);
                if (System.IO.File.Exists(full))
                {
                    try
                    {
                        await using var s = OpenFileWithRetry(full);
                        manifest.SourceParameterFilePath = await _fileIngestion.IngestFileAsync(jobId, s, "source_parameters" + Path.GetExtension(full), DatasetRole.SOURCE);
                    }
                    catch (IOException ioEx) when (ioEx.Message.Contains("used by another process"))
                    {
                        ModelState.AddModelError("", $"Source parameter file is locked: {request.SourceParameterFilePath}. Please close any open files and try again.");
                        return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                    }
                }
            }
            if (!string.IsNullOrEmpty(request.TargetParameterFilePath))
            {
                var full = Path.Combine(request.FolderPath, request.TargetParameterFilePath);
                if (System.IO.File.Exists(full))
                {
                    try
                    {
                        await using var s = OpenFileWithRetry(full);
                        manifest.TargetParameterFilePath = await _fileIngestion.IngestFileAsync(jobId, s, "target_parameters" + Path.GetExtension(full), DatasetRole.TARGET_SCHEMA);
                    }
                    catch (IOException ioEx) when (ioEx.Message.Contains("used by another process"))
                    {
                        ModelState.AddModelError("", $"Target parameter file is locked: {request.TargetParameterFilePath}. Please close any open files and try again.");
                        return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                    }
                }
            }

            if (!string.IsNullOrEmpty(request.ValueMappingsExcelFilePath))
            {
                var full = Path.Combine(request.FolderPath, request.ValueMappingsExcelFilePath);
                if (System.IO.File.Exists(full))
                {
                    var extension = Path.GetExtension(full).ToLowerInvariant();
                    if (extension is ".xlsx" or ".xls")
                    {
                        try
                        {
                            await using var s = OpenFileWithRetry(full);
                            manifest.ValueMappingsExcelFilePath = await _fileIngestion.IngestFileAsync(jobId, s, "value_mappings_seed" + extension, DatasetRole.SOURCE);
                        }
                        catch (IOException ioEx) when (ioEx.Message.Contains("used by another process"))
                        {
                            ModelState.AddModelError("", $"Value mappings file is locked: {request.ValueMappingsExcelFilePath}. Please close the file in Excel and try again.");
                            return View("Create", new CreateWorkflowRequest { JobName = request.JobName, Description = request.Description });
                        }
                    }
                }
            }

            await _jobRepo.CreateAsync(new DataReconciliation.Domain.Entities.WorkflowJob
            {
                JobId   = jobId,
                JobName = request.JobName,
                Status  = DataReconciliation.Domain.Enums.WorkflowStatus.Pending,
                CorrelationId = Guid.NewGuid().ToString()
            });

            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestratorService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WorkflowController>>();
                try { await orchestrator.StartWorkflowAsync(manifest); }
                catch (Exception ex) { logger.LogError(ex, "Background workflow (folder) failed. JobId={JobId}", jobId); }
            });

            TempData["Success"] = $"Workflow started successfully. Job ID: {jobId}";
            return RedirectToAction(nameof(Details), new { jobId });
        }

        /// <summary>
        /// Opens a file with retry logic to handle files locked by other processes.
        /// </summary>
        private FileStream OpenFileWithRetry(string filePath, int maxRetries = 5, int delayMs = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    return System.IO.File.OpenRead(filePath);
                }
                catch (IOException ex) when (ex.Message.Contains("used by another process") && i < maxRetries - 1)
                {
                    _logger.LogWarning("File locked, retrying... Attempt {Attempt}/{MaxRetries} for {FilePath}", i + 1, maxRetries, filePath);
                    System.Threading.Thread.Sleep(delayMs);
                }
            }

            // If all retries failed, throw the exception
            return System.IO.File.OpenRead(filePath);
        }
    }
}
