using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class SchemaGeneratorController : Controller
    {
        private static readonly string[] SupportedExtensions = [".csv", ".txt", ".xls", ".xlsx"];

        private readonly IFileIngestionService _fileIngestionService;
        private readonly ITargetSchemaGeneratorService _targetSchemaGeneratorService;
        private readonly ILogger<SchemaGeneratorController> _logger;

        public SchemaGeneratorController(
            IFileIngestionService fileIngestionService,
            ITargetSchemaGeneratorService targetSchemaGeneratorService,
            ILogger<SchemaGeneratorController> logger)
        {
            _fileIngestionService = fileIngestionService;
            _targetSchemaGeneratorService = targetSchemaGeneratorService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View(new TargetSchemaGeneratorPageDto());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate(TargetSchemaGeneratorPageDto model)
        {
            var request = model.Request;
            if (request.SourceFile == null || request.SourceFile.Length == 0)
            {
                ModelState.AddModelError("Request.SourceFile", "Please upload a CSV or Excel source file.");
            }

            var extension = Path.GetExtension(request.SourceFile?.FileName ?? string.Empty).ToLowerInvariant();
            if (!SupportedExtensions.Contains(extension))
            {
                ModelState.AddModelError("Request.SourceFile", "Supported file types are CSV, TXT, XLS, and XLSX.");
            }

            if (!ModelState.IsValid)
            {
                return View("Index", model);
            }

            var jobId = $"SCHEMA_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            var sourceFilePath = await _fileIngestionService.IngestFileAsync(
                jobId,
                request.SourceFile!.OpenReadStream(),
                request.SourceFile.FileName,
                DatasetRole.SOURCE);

            model.Result = await _targetSchemaGeneratorService.GenerateAsync(
                jobId,
                request.SchemaName,
                request.SourceFile.FileName,
                sourceFilePath,
                request.UseAiEnrichment);

            _logger.LogInformation(
                "Target schema generator completed. JobId={JobId} SourceFile={SourceFile} Workbook={Workbook}",
                model.Result.JobId,
                model.Result.SourceFileName,
                model.Result.WorkbookFileName);

            TempData["Success"] = $"Target schema workbook generated for {request.SourceFile.FileName}.";
            return View("Index", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateMultiSource(MultiSourceSchemaRequest request)
        {
            if (request.SourceFiles == null || request.SourceFiles.Count < 2)
            {
                TempData["Error"] = "Please select at least 2 source files for multi-source generation.";
                return View("Index", new TargetSchemaGeneratorPageDto());
            }

            foreach (var file in request.SourceFiles)
            {
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext))
                {
                    TempData["Error"] = $"Unsupported file type '{ext}' in '{file.FileName}'. Use CSV, TXT, XLS, or XLSX.";
                    return View("Index", new TargetSchemaGeneratorPageDto());
                }
            }

            var jobId = $"MULTI_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            var sourceFiles = new List<(string FileName, string FilePath)>();

            foreach (var file in request.SourceFiles)
            {
                var path = await _fileIngestionService.IngestFileAsync(
                    jobId, file.OpenReadStream(), file.FileName, DatasetRole.SOURCE);
                sourceFiles.Add((file.FileName, path));
            }

            var result = await _targetSchemaGeneratorService.GenerateMultiSourceAsync(
                jobId, request.SchemaName, sourceFiles, request.UseAiEnrichment);

            _logger.LogInformation(
                "Multi-source schema completed. JobId={JobId} Files={Count} Workbook={Workbook}",
                result.JobId, request.SourceFiles.Count, result.WorkbookFileName);

            TempData["Success"] = $"Multi-source schema generated from {request.SourceFiles.Count} files — {result.Fields.Count} target fields.";
            return View("Index", new TargetSchemaGeneratorPageDto { Result = result });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadMapping(string jobId)
        {
            var (bytes, fileName) = await _targetSchemaGeneratorService.DownloadMappingAsync(jobId);
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportMapping(ImportMappingRequest request)
        {
            if (request.MappingFile == null || request.MappingFile.Length == 0)
            {
                TempData["Error"] = "Please select a mapping Excel file to upload.";
                return RedirectToAction(nameof(Index));
            }

            using var stream = request.MappingFile.OpenReadStream();
            var result = await _targetSchemaGeneratorService.ImportMappingAsync(
                request.JobId, request.SchemaName, stream);

            TempData["Success"] = $"Mapping imported successfully — {result.Fields.Count} fields loaded.";
            return View("Index", new TargetSchemaGeneratorPageDto { Result = result });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEdits(SaveSchemaEditsRequest request)
        {
            var result = await _targetSchemaGeneratorService.SaveEditsAsync(request);
            TempData["Success"] = "Schema changes saved. A new workbook has been generated.";
            return View("Index", new TargetSchemaGeneratorPageDto { Result = result });
        }

        [HttpGet]
        public async Task<IActionResult> Download(string jobId, string fileName)
        {
            var workbookPath = await _targetSchemaGeneratorService.ResolveWorkbookPathAsync(jobId, fileName);
            if (workbookPath == null)
            {
                return NotFound();
            }

            return PhysicalFile(
                workbookPath,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Path.GetFileName(workbookPath));
        }
    }
}