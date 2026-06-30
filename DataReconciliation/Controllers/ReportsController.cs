using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Application.Services;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Models;
using ExcelDataReader;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DataReconciliation.Controllers
{
    public class ReportsController : Controller
    {
        private readonly IWorkflowJobRepository _jobRepo;
        private readonly IWorkflowArtifactRepository _artifactRepo;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IErrorAuditLogRepository _errorLog;
        private readonly IAIInferenceAuditRepository _aiAudit;
        private readonly IWorkflowOrchestratorService _orchestrator;
        private readonly IFileIngestionService _fileIngestionService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportsController> _logger;

        private readonly IValueMappingDiscoveryService? _valueMappingDiscovery;
        private readonly IValueMappingAIAgentService? _valueMappingAgent;
        private readonly IDeltaFileUploadService? _deltaFileUpload;
        private readonly IReconciliationConfigService? _reconciliationConfig;
        private readonly ITransformationOverrideService? _transformationOverride;
        private readonly IReconciliationService? _reconciliationService;

        public ReportsController(
            IWorkflowJobRepository jobRepo,
            IWorkflowArtifactRepository artifactRepo,
            IArtifactPersistenceService artifactPersistence,
            IErrorAuditLogRepository errorLog,
            IAIInferenceAuditRepository aiAudit,
            IWorkflowOrchestratorService orchestrator,
            IFileIngestionService fileIngestionService,
            IServiceScopeFactory scopeFactory,
            ILogger<ReportsController> logger,
            IValueMappingDiscoveryService? valueMappingDiscovery = null,
            IValueMappingAIAgentService? valueMappingAgent = null,
            IDeltaFileUploadService? deltaFileUpload = null,
            IReconciliationConfigService? reconciliationConfig = null,
            ITransformationOverrideService? transformationOverride = null,
            IReconciliationService? reconciliationService = null)
        {
            _jobRepo = jobRepo;
            _artifactRepo = artifactRepo;
            _artifactPersistence = artifactPersistence;
            _errorLog = errorLog;
            _aiAudit = aiAudit;
            _orchestrator = orchestrator;
            _fileIngestionService = fileIngestionService;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _valueMappingDiscovery = valueMappingDiscovery;
            _valueMappingAgent = valueMappingAgent;
            _deltaFileUpload = deltaFileUpload;
            _reconciliationConfig = reconciliationConfig;
            _transformationOverride = transformationOverride;
            _reconciliationService = reconciliationService;
        }
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var jobs = await _jobRepo.GetAllAsync();
            return View(jobs);
        }

        [HttpGet]
        public async Task<IActionResult> Reconciliation(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return RedirectToAction(nameof(Index));

            var result = await _artifactPersistence.LoadArtifactAsync<DataReconciliation.Domain.Models.ReconciliationResult>(
                jobId, Domain.Enums.ArtifactType.ReconciliationResult);

            // result may be null if workflow is still running — the view handles this gracefully
            ViewBag.JobId = jobId;
            return View(result);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunReconciliation(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return RedirectToAction("Index", "Workflow");

            if (_reconciliationService == null)
            {
                TempData["Error"] = "Reconciliation service not available.";
                return RedirectToAction(nameof(Reconciliation), new { jobId });
            }

            var canonicalRecords = await _artifactPersistence.LoadArtifactByNameAsync<List<CanonicalRecord>>(
                jobId, "canonical_records.json");

            var transformedRecords = await _artifactPersistence.LoadArtifactAsync<List<Dictionary<string, string>>>(
                jobId, Domain.Enums.ArtifactType.TransformationOutput);

            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, Domain.Enums.ArtifactType.TargetMetadataProfile);

            if (canonicalRecords == null || transformedRecords == null || targetProfile == null)
            {
                TempData["Error"] = "Cannot re-run reconciliation: output has not been generated yet. Please click 'Generate Output File' first.";
                return RedirectToAction(nameof(Reconciliation), new { jobId });
            }

            await _reconciliationService.ReconcileAsync(jobId, canonicalRecords, transformedRecords, targetProfile);

            _logger.LogInformation("Reconciliation re-run triggered from report page. JobId={JobId}", jobId);
            return RedirectToAction(nameof(Reconciliation), new { jobId });
        }

        [HttpGet]
        public async Task<IActionResult> AIAudit(string jobId)
        {
            var audits = await _aiAudit.GetByJobIdAsync(jobId);
            ViewBag.JobId = jobId;
            return View(audits.ToList());
        }

        [HttpGet]
        public async Task<IActionResult> ErrorLog(string jobId)
        {
            var errors = await _errorLog.GetByJobIdAsync(jobId);
            ViewBag.JobId = jobId;
            return View(errors.ToList());
        }

        [HttpGet]
        public async Task<IActionResult> Mappings(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return RedirectToAction("Index", "Workflow");

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<DataReconciliation.Domain.Models.FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            // Load the manifest to discover all source datasets, then load each per-dataset profile
            // Profiles are saved as source_schema_profile_{datasetId}.json (one per source dataset)
            var manifest = await _artifactPersistence.LoadArtifactAsync<DataReconciliation.Domain.Models.DatasetManifest>(
                jobId, Domain.Enums.ArtifactType.DatasetManifest);

            var allSourceFields = new List<string>();
            var sourceFieldLengths = new Dictionary<string, int>(); // Map source field name to MaxLength
            if (manifest != null)
            {
                foreach (var dataset in manifest.Datasets
                    .Where(d => d.DatasetRole == Domain.Enums.DatasetRole.SOURCE))
                {
                    var profile = await _artifactPersistence.LoadArtifactByNameAsync<DataReconciliation.Domain.Models.SourceSchemaProfile>(
                        jobId, $"source_schema_profile_{dataset.DatasetId}.json");
                    if (profile != null)
                    {
                        allSourceFields.AddRange(profile.Fields.Select(f => f.FieldName));
                        // Store source field lengths
                        foreach (var field in profile.Fields)
                        {
                            sourceFieldLengths[field.FieldName] = field.MaxLength;
                        }
                    }
                }
            }

            // Load target metadata to get target field lengths
            var targetMetadata = await _artifactPersistence.LoadArtifactAsync<DataReconciliation.Domain.Models.TargetMetadataProfile>(
                jobId, Domain.Enums.ArtifactType.TargetMetadataProfile);

            var targetFieldLengths = new Dictionary<string, int?>(); // Map target field name to FieldLength
            if (targetMetadata != null)
            {
                foreach (var field in targetMetadata.Fields)
                {
                    targetFieldLengths[field.FieldName] = field.FieldLength;
                }
            }

            var templateInfos = await LoadValueMappingTemplateInfosAsync(jobId);
            var manifestForSeed = await _artifactPersistence.LoadArtifactAsync<DatasetManifest>(
                jobId, Domain.Enums.ArtifactType.DatasetManifest);
            var hasUploadedValueMappingSeed = !string.IsNullOrWhiteSpace(manifestForSeed?.ValueMappingsExcelFilePath)
                && System.IO.File.Exists(manifestForSeed.ValueMappingsExcelFilePath);
            var uploadedValueMappingSeedName = hasUploadedValueMappingSeed
                ? Path.GetFileName(manifestForSeed!.ValueMappingsExcelFilePath)
                : null;

            ViewBag.JobId = jobId;
            ViewBag.SourceFields = allSourceFields.Distinct().OrderBy(f => f).ToList();
            ViewBag.ValueMappingTemplates = templateInfos;
            ViewBag.SourceFieldLengths = sourceFieldLengths;
            ViewBag.TargetFieldLengths = targetFieldLengths;
            ViewBag.HasUploadedValueMappingSeed = hasUploadedValueMappingSeed;
            ViewBag.UploadedValueMappingSeedName = uploadedValueMappingSeedName;

            return View(finalMapping);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMappings(string jobId, SaveMappingsRequest request)
        {
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
            {
                TempData["Error"] = "Mapping configuration not found.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            var updates = request?.Updates ?? new List<MappingUpdateDto>();
            var deleteTargets = updates
                .Where(update => update.Delete && !string.IsNullOrWhiteSpace(update.TargetField))
                .Select(update => update.TargetField.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var deleted = 0;
            if (deleteTargets.Count > 0)
            {
                deleted = finalMapping.Mappings.RemoveAll(mapping => deleteTargets.Contains(mapping.TargetField));

                if (deleted > 0)
                {
                    var valueMappingsDocument = await LoadValueMappingsDocumentAsync(jobId);
                    var valueMappingsDeleted = valueMappingsDocument.Fields.RemoveAll(field => deleteTargets.Contains(field.TargetField));
                    if (valueMappingsDeleted > 0)
                    {
                        valueMappingsDocument.UpdatedAt = DateTime.UtcNow;
                        await _artifactPersistence.PersistArtifactAsync(
                            jobId,
                            valueMappingsDocument,
                            Domain.Enums.ArtifactType.ValueMappings,
                            "value_mappings.json");
                    }
                }
            }

            const string unresolvedPlaceholder = "[UNRESOLVED]";
            var sourceFieldDatasets = await BuildSourceFieldDatasetsLookupAsync(jobId);
            int updated = 0;
            foreach (var update in updates.Where(update => !update.Delete))
            {
                var mapping = finalMapping.Mappings.FirstOrDefault(m => m.TargetField.Equals(update.TargetField, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                    continue;

                var sourceField = update.SourceField?.Trim();
                var isUnresolvedSelection = string.IsNullOrWhiteSpace(sourceField)
                    || sourceField.Equals(unresolvedPlaceholder, StringComparison.OrdinalIgnoreCase);

                if (isUnresolvedSelection)
                {
                    var wasChanged = !string.Equals(mapping.SourceField, unresolvedPlaceholder, StringComparison.OrdinalIgnoreCase)
                        || mapping.Status != Domain.Enums.MappingStatus.UNRESOLVED
                        || !string.Equals(mapping.MatchSource, "Unresolved", StringComparison.OrdinalIgnoreCase)
                        || mapping.Confidence != 0;

                    mapping.SourceField = unresolvedPlaceholder;
                    mapping.SourceDataset = null;
                    mapping.Status = Domain.Enums.MappingStatus.UNRESOLVED;
                    mapping.MatchSource = "Unresolved";
                    mapping.Confidence = 0;

                    if (wasChanged)
                        updated++;

                    continue;
                }

                if (!string.Equals(mapping.SourceField, sourceField, StringComparison.OrdinalIgnoreCase)
                    || mapping.Status == Domain.Enums.MappingStatus.UNRESOLVED)
                {
                    mapping.SourceField = sourceField!;
                    mapping.SourceDataset = ResolveSourceDataset(sourceField!, mapping.SourceDataset, sourceFieldDatasets);
                    if (mapping.Status == Domain.Enums.MappingStatus.UNRESOLVED)
                    {
                        mapping.Status = Domain.Enums.MappingStatus.CONFIRMED;
                        mapping.MatchSource = "Manual Confirmation";
                    }
                    updated++;
                }
                else if (string.IsNullOrWhiteSpace(mapping.SourceDataset))
                {
                    var resolvedDataset = ResolveSourceDataset(sourceField!, mapping.SourceDataset, sourceFieldDatasets);
                    if (!string.IsNullOrWhiteSpace(resolvedDataset))
                    {
                        mapping.SourceDataset = resolvedDataset;
                        updated++;
                    }
                }

                // Persist additional source mappings
                var newAdditional = (update.AdditionalSources ?? new())
                    .Where(a => !string.IsNullOrWhiteSpace(a.SourceField))
                    .Select(a => new Domain.Models.AdditionalSourceMapping
                    {
                        SourceField = a.SourceField.Trim(),
                        MergeRule = string.IsNullOrWhiteSpace(a.MergeRule) ? "FALLBACK" : a.MergeRule.Trim().ToUpperInvariant()
                    })
                    .ToList();
                mapping.AdditionalSources = newAdditional;
            }

            await _artifactPersistence.PersistArtifactAsync(
                jobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");

            _logger.LogInformation("Mappings manually updated. JobId={JobId} UpdatedCount={UpdatedCount} DeletedCount={DeletedCount}", jobId, updated, deleted);

            if (updated == 0 && deleted == 0)
            {
                TempData["Success"] = "No mapping changes were detected.";
            }
            else
            {
                TempData["Success"] = $"Mappings saved ({updated} updated, {deleted} deleted). Click \"Generate Output File\" to produce the .dat file.";
            }

            return RedirectToAction(nameof(Mappings), new { jobId });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadStructuralMapping(string jobId)
        {
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<Domain.Models.FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
            {
                TempData["Error"] = "Mapping configuration not found.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Structural Mapping");

            // Header
            var headers = new[] { "Target Field", "Merge Rule", "Source Field", "Dataset", "Status", "Confidence" };
            for (int c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
                ws.Cell(1, c + 1).Style.Font.Bold = true;
                ws.Cell(1, c + 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
            }

            // Instructions sheet
            var info = wb.Worksheets.Add("Instructions");
            info.Cell(1, 1).Value = "How to edit this mapping file";
            info.Cell(1, 1).Style.Font.Bold = true;
            info.Cell(2, 1).Value = "Each row is one source-to-target link.";
            info.Cell(3, 1).Value = "Merge Rule: PRIMARY (first/canonical), FALLBACK (used when PRIMARY is blank), CONCAT (values joined with space).";
            info.Cell(4, 1).Value = "Each target field must have exactly one PRIMARY row.";
            info.Cell(5, 1).Value = "To map multiple sources to one target, add extra rows with the same Target Field and a different Merge Rule.";
            info.Cell(6, 1).Value = "Do not add or remove columns. Upload back via 'Upload Edited Mapping'.";
            info.Column(1).Width = 90;

            // Merge rule validation list
            var rulesSheet = wb.Worksheets.Add("_Lists");
            rulesSheet.Cell(1, 1).Value = "PRIMARY";
            rulesSheet.Cell(2, 1).Value = "FALLBACK";
            rulesSheet.Cell(3, 1).Value = "CONCAT";
            rulesSheet.Visibility = ClosedXML.Excel.XLWorksheetVisibility.Hidden;

            int row = 2;
            foreach (var m in finalMapping.Mappings.OrderBy(x => x.TargetField, StringComparer.OrdinalIgnoreCase))
            {
                void WriteRow(string targetField, string mergeRule, string sourceField, string? dataset, string status, double confidence)
                {
                    ws.Cell(row, 1).Value = targetField;
                    ws.Cell(row, 2).Value = mergeRule;
                    ws.Cell(row, 3).Value = sourceField;
                    ws.Cell(row, 4).Value = dataset ?? "";
                    ws.Cell(row, 5).Value = status;
                    ws.Cell(row, 6).Value = confidence;
                    row++;
                }

                WriteRow(m.TargetField, "PRIMARY", m.SourceField, m.SourceDataset, m.Status.ToString(), m.Confidence);
                foreach (var add in m.AdditionalSources)
                    WriteRow(m.TargetField, add.MergeRule, add.SourceField, null, m.Status.ToString(), m.Confidence);
            }

            // Apply merge-rule dropdown validation to all data rows in column B
            if (row > 2)
                ws.Range(ws.Cell(2, 2), ws.Cell(row - 1, 2))
                  .CreateDataValidation()
                  .List(rulesSheet.Range("A1:A3"), true);

            ws.Columns().AdjustToContents();

            using var ms = new System.IO.MemoryStream();
            wb.SaveAs(ms);
            return File(ms.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"mapping_{jobId}_{DateTime.UtcNow:yyyyMMdd}.xlsx");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportStructuralMapping(ImportStructuralMappingRequest request)
        {
            if (request.MappingFile == null || request.MappingFile.Length == 0)
            {
                TempData["Error"] = "Please select a mapping Excel file to upload.";
                return RedirectToAction(nameof(Mappings), new { jobId = request.JobId });
            }

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<Domain.Models.FinalMappingConfig>(
                request.JobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
            {
                TempData["Error"] = "Mapping configuration not found.";
                return RedirectToAction(nameof(Mappings), new { jobId = request.JobId });
            }

            using var stream = request.MappingFile.OpenReadStream();
            using var wb = new ClosedXML.Excel.XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault(s => !s.Name.StartsWith("_") && s.Name != "Instructions");
            if (ws == null)
            {
                TempData["Error"] = "Could not find the mapping sheet in the uploaded file.";
                return RedirectToAction(nameof(Mappings), new { jobId = request.JobId });
            }

            // Parse rows (skip header row 1)
            var rows = ws.RowsUsed().Skip(1)
                .Select(r => new
                {
                    TargetField = r.Cell(1).GetString().Trim(),
                    MergeRule   = r.Cell(2).GetString().Trim().ToUpperInvariant(),
                    SourceField = r.Cell(3).GetString().Trim()
                })
                .Where(r => !string.IsNullOrWhiteSpace(r.TargetField) && !string.IsNullOrWhiteSpace(r.SourceField))
                .ToList();

            int updated = 0;
            foreach (var group in rows.GroupBy(r => r.TargetField, StringComparer.OrdinalIgnoreCase))
            {
                var mapping = finalMapping.Mappings.FirstOrDefault(m =>
                    m.TargetField.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                if (mapping == null) continue;

                var primary = group.FirstOrDefault(r => r.MergeRule == "PRIMARY") ?? group.First();
                mapping.SourceField = primary.SourceField;
                if (mapping.Status == Domain.Enums.MappingStatus.UNRESOLVED && !string.IsNullOrWhiteSpace(primary.SourceField))
                {
                    mapping.Status = Domain.Enums.MappingStatus.CONFIRMED;
                    mapping.MatchSource = "Manual Confirmation";
                }

                mapping.AdditionalSources = group
                    .Where(r => r != primary)
                    .Select(r => new Domain.Models.AdditionalSourceMapping
                    {
                        SourceField = r.SourceField,
                        MergeRule = r.MergeRule is "FALLBACK" or "CONCAT" ? r.MergeRule : "FALLBACK"
                    })
                    .ToList();

                updated++;
            }

            await _artifactPersistence.PersistArtifactAsync(
                request.JobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");

            TempData["Success"] = $"Mapping imported — {updated} target field(s) updated.";
            return RedirectToAction(nameof(Mappings), new { jobId = request.JobId });
        }

        private async Task<Dictionary<string, HashSet<string>>> BuildSourceFieldDatasetsLookupAsync(string jobId)
        {
            var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var manifest = await _artifactPersistence.LoadArtifactAsync<DatasetManifest>(
                jobId, Domain.Enums.ArtifactType.DatasetManifest);

            if (manifest == null)
                return lookup;

            foreach (var dataset in manifest.Datasets.Where(d => d.DatasetRole == Domain.Enums.DatasetRole.SOURCE))
            {
                var profile = await _artifactPersistence.LoadArtifactByNameAsync<SourceSchemaProfile>(
                    jobId, $"source_schema_profile_{dataset.DatasetId}.json");

                if (profile?.Fields == null)
                    continue;

                foreach (var field in profile.Fields)
                {
                    if (string.IsNullOrWhiteSpace(field.FieldName))
                        continue;

                    if (!lookup.TryGetValue(field.FieldName, out var datasetsForField))
                    {
                        datasetsForField = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        lookup[field.FieldName] = datasetsForField;
                    }

                    datasetsForField.Add(dataset.DatasetId);
                }
            }

            return lookup;
        }

        private static string? ResolveSourceDataset(
            string sourceField,
            string? existingSourceDataset,
            IReadOnlyDictionary<string, HashSet<string>> sourceFieldDatasets)
        {
            if (string.IsNullOrWhiteSpace(sourceField))
                return existingSourceDataset;

            if (!sourceFieldDatasets.TryGetValue(sourceField, out var candidates) || candidates.Count == 0)
                return existingSourceDataset;

            if (!string.IsNullOrWhiteSpace(existingSourceDataset) && candidates.Contains(existingSourceDataset))
                return existingSourceDataset;

            return candidates.Count == 1 ? candidates.First() : existingSourceDataset;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveValueMappings(string jobId, ValueMappingEditorRequest request)
        {
            return await PersistValueMappingsAsync(jobId, request, saveAsTemplate: false);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveValueMappingTemplate(string jobId, ValueMappingEditorRequest request)
        {
            return await PersistValueMappingsAsync(jobId, request, saveAsTemplate: true);
        }

        [HttpGet]
        public async Task<IActionResult> LoadValueMappingTemplate(string jobId, string templateName)
        {
            var templatePath = await ResolveTemplatePathAsync(jobId, templateName);
            if (templatePath == null || !System.IO.File.Exists(templatePath))
                return NotFound();

            var json = await System.IO.File.ReadAllTextAsync(templatePath);
            return Content(json, "application/json");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ImportValueMappingsFromExcel(string jobId, string targetField, IFormFile? excelFile, string? sourceField = null)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(targetField))
                return BadRequest(new { error = "jobId and targetField are required." });

            if (excelFile == null || excelFile.Length == 0)
                return BadRequest(new { error = "Excel file is required." });

            var extension = Path.GetExtension(excelFile.FileName).ToLowerInvariant();
            if (extension != ".xlsx" && extension != ".xls")
                return BadRequest(new { error = "Only .xlsx or .xls files are supported." });

            using var stream = excelFile.OpenReadStream();
            return ImportValueMappingsFromExcelStream(jobId, targetField, sourceField, stream, excelFile.FileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportValueMappingsFromUploadedExcel(string jobId, string targetField, string? sourceField = null)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(targetField))
                return BadRequest(new { error = "jobId and targetField are required." });

            var manifest = await _artifactPersistence.LoadArtifactAsync<DatasetManifest>(
                jobId, Domain.Enums.ArtifactType.DatasetManifest);

            var path = manifest?.ValueMappingsExcelFilePath;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return BadRequest(new { error = "No uploaded value mapping Excel file found for this job." });

            await using var stream = System.IO.File.OpenRead(path);
            return ImportValueMappingsFromExcelStream(jobId, targetField, sourceField, stream, Path.GetFileName(path));
        }

        private IActionResult ImportValueMappingsFromExcelStream(
            string jobId,
            string targetField,
            string? sourceField,
            Stream stream,
            string sourceFileName)
        {

            var importedEntries = new List<ValueMappingEntryDto>();
            var discoveredDescriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                using var reader = ExcelReaderFactory.CreateReader(stream);

                do
                {
                    if (!reader.Read())
                        continue;

                    var headerMap = BuildHeaderIndexMap(reader);

                    var descIdx = ResolveColumnIndex(headerMap, "Code description", "CodeDescription", "Description", "Target Field", "Field", "Field Name", "FieldName");
                    var codeIdx = ResolveColumnIndex(headerMap, "Ext Code", "External Code", "Code", "Source Value", "Source Code", "Source", "SourceValue", "From");
                    var shortIdx = ResolveColumnIndex(headerMap, "Short description", "Short Description", "Short Desc");
                    var longIdx = ResolveColumnIndex(headerMap, "Long description", "Long Description", "Long Desc", "Target Value", "TargetValue", "To", "Mapped Value", "MappedValue");

                    // Fallback: if no "description/field" column but we have source+target columns,
                    // treat ALL rows as belonging to the requested target field (simple 2-column format)
                    bool simpleFormat = false;
                    if (descIdx < 0 && codeIdx >= 0 && longIdx >= 0)
                    {
                        simpleFormat = true;
                    }
                    else if (descIdx < 0 && codeIdx < 0)
                    {
                        // Try simple 2-column layout: first column = source, second column = target
                        if (reader.FieldCount >= 2)
                        {
                            codeIdx = 0;
                            longIdx = 1;
                            simpleFormat = true;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var rowsForTarget = new List<ValueMappingEntryDto>();
                    // Parallel fallback: collect rows treating "Code description" as the source value
                    // (used when the Excel is a code reference table, not a field-grouped mapping sheet)
                    var descAsSourceFallback = new List<ValueMappingEntryDto>();

                    while (reader.Read())
                    {
                        var extCode = ReadCellString(reader, codeIdx);
                        var shortDescription = shortIdx >= 0 ? ReadCellString(reader, shortIdx) : string.Empty;
                        var longDescription = longIdx >= 0 ? ReadCellString(reader, longIdx) : string.Empty;

                        if (simpleFormat)
                        {
                            // Simple format: all rows belong to the requested target field
                            if (string.IsNullOrWhiteSpace(extCode))
                                continue;

                            var mappedValue = !string.IsNullOrWhiteSpace(longDescription)
                                ? longDescription.Trim()
                                : !string.IsNullOrWhiteSpace(shortDescription)
                                    ? shortDescription.Trim()
                                    : string.Empty;

                            if (string.IsNullOrWhiteSpace(mappedValue))
                                continue;

                            rowsForTarget.Add(new ValueMappingEntryDto
                            {
                                SourceValue = extCode.Trim(),
                                TargetValue = mappedValue,
                                IsEnabled = true,
                                Notes = $"Imported from Excel ({sourceFileName})"
                            });
                        }
                        else
                        {
                            // Standard format: filter by "Code description" column matching targetField
                            var rowTargetDescription = ReadCellString(reader, descIdx);

                            if (!string.IsNullOrWhiteSpace(rowTargetDescription))
                                discoveredDescriptions.Add(rowTargetDescription.Trim());

                            if (string.IsNullOrWhiteSpace(rowTargetDescription) || string.IsNullOrWhiteSpace(extCode))
                                continue;

                            // Collect fallback: treat Code description as source value, Long description as target value.
                            // This handles reference tables where Code description is the human-readable source label,
                            // not the target field name (e.g., "Austria" label / "AUT" code / "AUT" target).
                            var fallbackTarget = !string.IsNullOrWhiteSpace(longDescription)
                                ? longDescription.Trim()
                                : !string.IsNullOrWhiteSpace(shortDescription)
                                    ? shortDescription.Trim()
                                    : extCode.Trim();
                            descAsSourceFallback.Add(new ValueMappingEntryDto
                            {
                                SourceValue = rowTargetDescription.Trim(),
                                TargetValue = fallbackTarget,
                                IsEnabled = true,
                                Notes = $"Imported from Excel ({sourceFileName}) [label-as-source]"
                            });

                            if (!rowTargetDescription.Trim().Equals(targetField.Trim(), StringComparison.OrdinalIgnoreCase))
                                continue;

                            var targetValue = !string.IsNullOrWhiteSpace(longDescription)
                                ? longDescription.Trim()
                                : !string.IsNullOrWhiteSpace(shortDescription)
                                    ? shortDescription.Trim()
                                    : string.Empty;

                            if (string.IsNullOrWhiteSpace(targetValue))
                                continue;

                            rowsForTarget.Add(new ValueMappingEntryDto
                            {
                                SourceValue = extCode.Trim(),
                                TargetValue = targetValue,
                                IsEnabled = true,
                                Notes = $"Imported from Excel ({sourceFileName})"
                            });
                        }
                    }

                    // If standard format found no matches but the fallback (Code description = source value) has rows,
                    // use the fallback. This handles reference Excels where Code description is a label, not a field name.
                    if (!rowsForTarget.Any() && !simpleFormat && descAsSourceFallback.Any())
                        rowsForTarget.AddRange(descAsSourceFallback);

                    importedEntries.AddRange(rowsForTarget);

                } while (reader.NextResult());

                var deduped = importedEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.SourceValue))
                    .GroupBy(e => e.SourceValue, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();

                if (!deduped.Any())
                {
                    var errorMsg = discoveredDescriptions.Any()
                        ? $"No rows matched target field '{targetField}'. The Excel uses a 'Code description' column — ensure it contains '{targetField}'."
                        : $"No value mapping entries found in the Excel file. Expected format: Source Value column + Target Value column (or Code description + Ext Code + Long description).";

                    return BadRequest(new
                    {
                        error = errorMsg,
                        availableCodeDescriptions = discoveredDescriptions.OrderBy(x => x).Take(50)
                    });
                }

                _logger.LogInformation(
                    "Imported value mappings from Excel. JobId={JobId} TargetField={TargetField} Rules={Count} File={File}",
                    jobId, targetField, deduped.Count, sourceFileName);

                return Ok(new
                {
                    targetField,
                    sourceField,
                    importedCount = deduped.Count,
                    entries = deduped
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed importing value mappings from Excel. JobId={JobId} TargetField={TargetField} File={File}",
                    jobId, targetField, sourceFileName);
                return StatusCode(500, new { error = "Excel import failed. Please validate file structure and try again." });
            }
        }

        private async Task<IActionResult> PersistValueMappingsAsync(string jobId, ValueMappingEditorRequest request, bool saveAsTemplate)
        {
            if (string.IsNullOrWhiteSpace(request.TargetField))
            {
                TempData["Error"] = "Target field was not provided.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            if (finalMapping == null)
            {
                TempData["Error"] = "Mapping configuration not found.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            var normalizedEntries = request.Entries
                .Where(entry => entry.IsEnabled && (!string.IsNullOrWhiteSpace(entry.SourceValue) || !string.IsNullOrWhiteSpace(entry.TargetValue)))
                .Select(entry => new ValueMappingEntry
                {
                    SourceValue = entry.SourceValue.Trim(),
                    TargetValue = entry.TargetValue.Trim(),
                    IsEnabled = entry.IsEnabled,
                    Notes = entry.Notes?.Trim()
                })
                .ToList();

            var valueMappingsDocument = await LoadValueMappingsDocumentAsync(jobId);
            var existingField = valueMappingsDocument.Fields.FirstOrDefault(field => field.TargetField.Equals(request.TargetField, StringComparison.OrdinalIgnoreCase));
            if (existingField == null)
            {
                existingField = new ValueMappingFieldDocument { TargetField = request.TargetField };
                valueMappingsDocument.Fields.Add(existingField);
            }

            existingField.SourceField = request.SourceField;
            existingField.TemplateName = request.TemplateName;
            existingField.Entries = normalizedEntries;
            valueMappingsDocument.JobId = jobId;
            valueMappingsDocument.UpdatedAt = DateTime.UtcNow;

            ApplyValueMappingToFinalConfig(finalMapping, request.TargetField, request.SourceField, normalizedEntries);

            await _artifactPersistence.PersistArtifactAsync(jobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");
            await _artifactPersistence.PersistArtifactAsync(jobId, valueMappingsDocument, Domain.Enums.ArtifactType.ValueMappings, "value_mappings.json");

            if (saveAsTemplate)
            {
                var templateName = string.IsNullOrWhiteSpace(request.TemplateName)
                    ? request.TargetField
                    : request.TemplateName.Trim();

                var templatePath = await ResolveTemplatePathAsync(jobId, templateName, createDirectory: true);
                if (templatePath == null)
                {
                    TempData["Error"] = "Template path could not be resolved.";
                    return RedirectToAction(nameof(Mappings), new { jobId });
                }

                var templateDocument = new ValueMappingsDocumentDto
                {
                    JobId = jobId,
                    UpdatedAt = DateTime.UtcNow,
                    Fields = new List<ValueMappingFieldDto>
                    {
                        new()
                        {
                            TargetField = request.TargetField,
                            SourceField = request.SourceField,
                            TemplateName = templateName,
                            Entries = request.Entries
                                .Select(entry => new ValueMappingEntryDto
                                {
                                    SourceValue = entry.SourceValue,
                                    TargetValue = entry.TargetValue,
                                    IsEnabled = entry.IsEnabled,
                                    Notes = entry.Notes
                                })
                                .ToList()
                        }
                    }
                };

                await System.IO.File.WriteAllTextAsync(
                    templatePath,
                    JsonSerializer.Serialize(templateDocument, new JsonSerializerOptions { WriteIndented = true }));

                TempData["Success"] = $"Value mapping template '{templateName}' saved.";
            }
            else
            {
                TempData["Success"] = $"Value mappings saved for {request.TargetField}.";
            }

            return RedirectToAction(nameof(Mappings), new { jobId });
        }

        private static void ApplyValueMappingToFinalConfig(
            FinalMappingConfig finalMapping,
            string targetField,
            string? sourceField,
            List<ValueMappingEntry> entries)
        {
            var mapping = finalMapping.Mappings.FirstOrDefault(item => item.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
                return;

            if (!string.IsNullOrWhiteSpace(sourceField) && string.IsNullOrWhiteSpace(mapping.SourceField))
                mapping.SourceField = sourceField.Trim();

            mapping.Transformations ??= new List<TransformationRule>();
            mapping.Transformations.RemoveAll(rule => string.Equals(rule.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));

            if (entries.Any())
            {
                mapping.Transformations.Insert(0, new TransformationRule
                {
                    Operation = "VALUE_MAPPING",
                    Rules = entries
                        .Where(entry => entry.IsEnabled && !string.IsNullOrWhiteSpace(entry.SourceValue))
                        .ToDictionary(entry => entry.SourceValue, entry => entry.TargetValue, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        private async Task<ValueMappingsDocument> LoadValueMappingsDocumentAsync(string jobId)
        {
            var document = await _artifactPersistence.LoadArtifactAsync<ValueMappingsDocument>(jobId, Domain.Enums.ArtifactType.ValueMappings);
            return document ?? new ValueMappingsDocument { JobId = jobId };
        }

        private async Task<List<ValueMappingTemplateInfoDto>> LoadValueMappingTemplateInfosAsync(string jobId)
        {
            var artifactsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "artifacts");
            var folder = Path.Combine(artifactsPath, "value_mapping_templates");
            if (!Directory.Exists(folder))
                return new List<ValueMappingTemplateInfoDto>();

            return Directory.GetFiles(folder, "*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new ValueMappingTemplateInfoDto
                {
                    FileName = file.Name,
                    TemplateName = Path.GetFileNameWithoutExtension(file.Name),
                    LastModifiedUtc = file.LastWriteTimeUtc
                })
                .ToList();
        }

        private async Task<string?> ResolveTemplatePathAsync(string jobId, string templateName, bool createDirectory = false)
        {
            var artifactsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "artifacts");
            var templateFolder = Path.Combine(artifactsPath, "value_mapping_templates");
            if (createDirectory)
                Directory.CreateDirectory(templateFolder);

            if (string.IsNullOrWhiteSpace(templateName))
                return Path.Combine(templateFolder, "__placeholder__.json");

            var safeName = new string(templateName.Trim().Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            return Path.Combine(templateFolder, $"{safeName}.json");
        }

        private static Dictionary<string, int> BuildHeaderIndexMap(IExcelDataReader reader)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var raw = reader.GetValue(i)?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var normalized = NormalizeHeader(raw);
                if (!map.ContainsKey(normalized))
                    map[normalized] = i;
            }
            return map;
        }

        private static int ResolveColumnIndex(Dictionary<string, int> headerMap, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (headerMap.TryGetValue(NormalizeHeader(candidate), out var idx))
                    return idx;
            }

            return -1;
        }

        private static string ReadCellString(IExcelDataReader reader, int index)
        {
            if (index < 0 || index >= reader.FieldCount)
                return string.Empty;
            return reader.GetValue(index)?.ToString()?.Trim() ?? string.Empty;
        }

        private static string NormalizeHeader(string header)
        {
            var chars = header
                .Trim()
                .Where(ch => char.IsLetterOrDigit(ch))
                .Select(char.ToLowerInvariant)
                .ToArray();
            return new string(chars);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult GenerateFile(string jobId)
        {
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestratorService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<ReportsController>>();
                try
                {
                    await orchestrator.ReplayWorkflowAsync(jobId, Domain.Enums.WorkflowStep.TransformationExecution);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GenerateFile replay failed. JobId={JobId}", jobId);
                }
            });

            _logger.LogInformation("GenerateFile triggered. JobId={JobId}", jobId);
            TempData["Success"] = "Output file generation started! Steps 11–14 are now running. Track progress on the Workflow Details page.";
            return RedirectToAction("Details", "Workflow", new { jobId });
        }

        // ═══════════════════════════════════════════════════════════════
        // MAPPING WORKBENCH — Structural Mapping Actions
        // ═══════════════════════════════════════════════════════════════

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMapping(string jobId, string targetField)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(targetField))
                return BadRequest(new { error = "jobId and targetField are required." });

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);
            if (finalMapping == null)
                return NotFound(new { error = "Mapping configuration not found." });

            var removed = finalMapping.Mappings.RemoveAll(m =>
                m.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                await _artifactPersistence.PersistArtifactAsync(
                    jobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");

                var vmDoc = await LoadValueMappingsDocumentAsync(jobId);
                var vmRemoved = vmDoc.Fields.RemoveAll(f =>
                    f.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
                if (vmRemoved > 0)
                {
                    vmDoc.UpdatedAt = DateTime.UtcNow;
                    await _artifactPersistence.PersistArtifactAsync(
                        jobId, vmDoc, Domain.Enums.ArtifactType.ValueMappings, "value_mappings.json");
                }

                _logger.LogInformation("Mapping deleted. JobId={JobId} TargetField={Field}", jobId, targetField);
            }

            return Ok(new { deleted = removed > 0, targetField });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReAiMatch(string jobId, string targetField)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(targetField))
                return BadRequest(new { error = "jobId and targetField are required." });

            _logger.LogInformation("ReAiMatch requested. JobId={JobId} TargetField={Field}", jobId, targetField);

            // Fire off a re-mapping in background scoped to just this field
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<ReportsController>>();
                var artifactSvc = scope.ServiceProvider.GetRequiredService<IArtifactPersistenceService>();
                try
                {
                    var aiService = scope.ServiceProvider.GetRequiredService<IAIMappingInferenceService>();
                    var finalMapping = await artifactSvc.LoadArtifactAsync<FinalMappingConfig>(
                        jobId, Domain.Enums.ArtifactType.FinalMappingConfig);
                    var candidates = await artifactSvc.LoadArtifactAsync<MappingCandidatesDocument>(
                        jobId, Domain.Enums.ArtifactType.MappingCandidates);
                    var targetProfile = await artifactSvc.LoadArtifactAsync<TargetMetadataProfile>(
                        jobId, Domain.Enums.ArtifactType.TargetMetadataProfile);
                    if (finalMapping == null || candidates == null || targetProfile == null) return;

                    // Filter candidates to just this target field and run AI inference
                    var singleFieldCandidates = new MappingCandidatesDocument
                    {
                        JobId = jobId,
                        Mappings = candidates.Mappings
                            .Where(m => m.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase))
                            .ToList()
                    };

                    if (!singleFieldCandidates.Mappings.Any()) return;

                    var aiResponse = await aiService.InferMappingsAsync(
                        jobId, singleFieldCandidates, targetProfile);

                    // Merge AI result into final mapping
                    foreach (var aiMapping in aiResponse.AiMappings
                        .Where(m => m.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase)))
                    {
                        var existing = finalMapping.Mappings
                            .FirstOrDefault(m => m.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.SourceField = aiMapping.SourceField ?? existing.SourceField;
                            existing.Confidence = aiMapping.Confidence;
                            existing.Status = Domain.Enums.MappingStatus.AI_MATCHED;
                            existing.MatchSource = "AI Inference";
                        }
                    }

                    await artifactSvc.PersistArtifactAsync(
                        jobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");

                    logger.LogInformation("ReAiMatch completed. JobId={JobId} TargetField={Field}", jobId, targetField);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ReAiMatch failed. JobId={JobId} TargetField={Field}", jobId, targetField);
                }
            });

            return Ok(new { status = "Re-AI match started in background", targetField });
        }

        // ═══════════════════════════════════════════════════════════════
        // MAPPING WORKBENCH — Value Mapping Tab Actions
        // ═══════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetValueMappingCandidates(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            var doc = await _artifactPersistence.LoadArtifactAsync<ValueMappingCandidatesDocument>(
                jobId, Domain.Enums.ArtifactType.ValueMappingCandidates);

            if (doc == null && _valueMappingDiscovery != null)
                doc = await _valueMappingDiscovery.DiscoverAsync(jobId);

            doc ??= new ValueMappingCandidatesDocument { JobId = jobId };

            // Enrich with configured rule counts from final_mapping_config
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            var dtos = doc.Candidates.Select(c =>
            {
                var existingRules = finalMapping?.Mappings
                    .FirstOrDefault(m => m.TargetField.Equals(c.TargetField, StringComparison.OrdinalIgnoreCase))
                    ?.Transformations
                    .FirstOrDefault(t => string.Equals(t.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));

                var configuredCount = existingRules?.Rules?.Count ?? 0;
                var coverage = c.DistinctValueCount > 0
                    ? Math.Round((double)configuredCount / c.DistinctValueCount * 100, 1)
                    : 0.0;

                return new DataReconciliation.Application.DTOs.ValueMappingCandidateDto
                {
                    SourceField = c.SourceField,
                    SourceDataset = c.SourceDataset,
                    TargetField = c.TargetField,
                    DistinctSourceValues = c.DistinctSourceValues,
                    DistinctValueCount = c.DistinctValueCount,
                    Status = configuredCount >= c.DistinctValueCount && c.DistinctValueCount > 0 ? "Complete"
                        : configuredCount > 0 ? "Partial"
                        : c.Status,
                    SuggestedMappings = c.SuggestedMappings.Select(r => new DataReconciliation.Application.DTOs.ValueMappingCandidateRuleDto
                    {
                        SourceValue = r.SourceValue,
                        TargetValue = r.TargetValue,
                        Confidence = r.Confidence,
                        IsAiSuggested = r.IsAiSuggested
                    }).ToList(),
                    AiConfidence = c.AiConfidence,
                    ConfiguredRuleCount = configuredCount,
                    CoveragePercent = coverage
                };
            }).ToList();

            return Json(new
            {
                candidates = dtos,
                kpis = new
                {
                    total = dtos.Count,
                    complete = dtos.Count(d => d.Status == "Complete"),
                    partial = dtos.Count(d => d.Status == "Partial"),
                    pending = dtos.Count(d => d.Status == "Pending"),
                    averageCoverage = dtos.Any() ? Math.Round(dtos.Average(d => d.CoveragePercent), 1) : 0.0
                }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DiscoverValueMappings(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                TempData["Error"] = "Job ID is required.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            if (_valueMappingDiscovery == null)
            {
                TempData["Error"] = "Value mapping discovery service is not available.";
                return RedirectToAction(nameof(Mappings), new { jobId });
            }

            var doc = await _valueMappingDiscovery.DiscoverAsync(jobId);
            _logger.LogInformation("Value mapping discovery triggered via UI. JobId={JobId} Candidates={Count}", jobId, doc.Candidates.Count);
            TempData["Success"] = $"Discovered {doc.Candidates.Count} value mapping candidate(s). Switch to the Value Mapping tab to review.";
            return RedirectToAction(nameof(Mappings), new { jobId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateValueMappingTemplate(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            // Ensure candidates are discovered first
            var doc = await _artifactPersistence.LoadArtifactAsync<ValueMappingCandidatesDocument>(
                jobId, Domain.Enums.ArtifactType.ValueMappingCandidates);

            if (doc == null && _valueMappingDiscovery != null)
                doc = await _valueMappingDiscovery.DiscoverAsync(jobId);

            if (doc == null || !doc.Candidates.Any())
                return Ok(new { status = "No candidates found.", candidatesUpdated = 0 });

            // Load target metadata for descriptions
            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, Domain.Enums.ArtifactType.TargetMetadataProfile);

            int updated = 0;
            if (_valueMappingAgent != null)
            {
                // Process candidates that have no AI suggestions yet
                foreach (var candidate in doc.Candidates.Where(c => !c.SuggestedMappings.Any(r => r.IsAiSuggested)))
                {
                    var targetDesc = targetProfile?.Fields
                        .FirstOrDefault(f => f.FieldName.Equals(candidate.TargetField, StringComparison.OrdinalIgnoreCase))
                        ?.Description;

                    var aiResult = await _valueMappingAgent.SuggestMappingsAsync(
                        jobId,
                        candidate.SourceField,
                        candidate.TargetField,
                        candidate.DistinctSourceValues,
                        targetDescription: targetDesc);

                    if (aiResult.SuggestedMappings.Any())
                    {
                        candidate.SuggestedMappings = aiResult.SuggestedMappings;
                        candidate.AiConfidence = aiResult.AiConfidence;
                        updated++;
                    }
                }

                await _artifactPersistence.PersistArtifactAsync(
                    jobId, doc, Domain.Enums.ArtifactType.ValueMappingCandidates, "value_mapping_candidates.json");
            }

            _logger.LogInformation(
                "Value mapping template generated. JobId={JobId} Candidates={Total} AiUpdated={Updated}",
                jobId, doc.Candidates.Count, updated);

            return Ok(new
            {
                status = "Generated",
                candidatesUpdated = updated,
                totalCandidates = doc.Candidates.Count
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCandidateMappings(
            [FromQuery] string jobId,
            [FromBody] DataReconciliation.Application.DTOs.SaveCandidateMappingsRequest request)
        {
            if (string.IsNullOrWhiteSpace(jobId) || request == null)
                return BadRequest(new { error = "jobId and request body are required." });

            // Update value_mapping_candidates.json status
            var doc = await _artifactPersistence.LoadArtifactAsync<ValueMappingCandidatesDocument>(
                jobId, Domain.Enums.ArtifactType.ValueMappingCandidates);

            if (doc != null)
            {
                var candidate = doc.Candidates.FirstOrDefault(c =>
                    c.TargetField.Equals(request.TargetField, StringComparison.OrdinalIgnoreCase));
                if (candidate != null)
                {
                    candidate.SuggestedMappings = request.Mappings.Select(m => new ValueMappingCandidateRule
                    {
                        SourceValue = m.SourceValue,
                        TargetValue = m.TargetValue,
                        Confidence = m.Confidence,
                        IsAiSuggested = m.IsAiSuggested
                    }).ToList();
                    candidate.Status = "Reviewed";
                    candidate.ReviewedAt = DateTime.UtcNow;
                    await _artifactPersistence.PersistArtifactAsync(
                        jobId, doc, Domain.Enums.ArtifactType.ValueMappingCandidates, "value_mapping_candidates.json");
                }
            }

            // Apply to final_mapping_config VALUE_MAPPING transformation
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);
            if (finalMapping != null)
            {
                var mapping = finalMapping.Mappings.FirstOrDefault(m =>
                    m.TargetField.Equals(request.TargetField, StringComparison.OrdinalIgnoreCase));
                if (mapping != null)
                {
                    if (!string.IsNullOrWhiteSpace(request.SourceField) && string.IsNullOrWhiteSpace(mapping.SourceField))
                        mapping.SourceField = request.SourceField;

                    mapping.Transformations ??= new List<TransformationRule>();
                    mapping.Transformations.RemoveAll(t =>
                        string.Equals(t.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));

                    var rules = request.Mappings
                        .Where(m => !string.IsNullOrWhiteSpace(m.SourceValue))
                        .ToDictionary(m => m.SourceValue, m => m.TargetValue, StringComparer.OrdinalIgnoreCase);

                    if (rules.Any())
                    {
                        mapping.Transformations.Insert(0, new TransformationRule
                        {
                            Operation = "VALUE_MAPPING",
                            Rules = rules
                        });
                    }

                    await _artifactPersistence.PersistArtifactAsync(
                        jobId, finalMapping, Domain.Enums.ArtifactType.FinalMappingConfig, "final_mapping_config.json");
                }
            }

            _logger.LogInformation(
                "Candidate mappings saved. JobId={JobId} TargetField={Field} Rules={Count}",
                jobId, request.TargetField, request.Mappings.Count);

            return Ok(new { saved = request.Mappings.Count, targetField = request.TargetField });
        }

        // ═══════════════════════════════════════════════════════════════
        // MAPPING WORKBENCH — Transformation Preview
        // ═══════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> PreviewTransformations(string jobId, int count = 10)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);
            if (finalMapping == null)
                return Ok(new { rows = new List<object>() });

            var canonicalRecords = await _artifactPersistence.LoadArtifactByNameAsync<List<CanonicalRecord>>(
                jobId, "canonical_records.json");
            if (canonicalRecords == null || !canonicalRecords.Any())
                return Ok(new { rows = new List<object>(), message = "Canonical records not yet generated." });

            var previewCount = Math.Min(Math.Max(count, 1), 50);
            var rows = new List<DataReconciliation.Application.DTOs.TransformationPreviewRowDto>();
            foreach (var record in canonicalRecords.Take(previewCount))
            {
                foreach (var mapping in finalMapping.Mappings
                    .Where(m => !string.IsNullOrWhiteSpace(m.SourceField) && m.SourceField != "[UNRESOLVED]")
                    .Where(m => m.Transformations.Any(t => !string.Equals(t.Operation, "FIXED_WIDTH_FORMATTING", StringComparison.OrdinalIgnoreCase))))
                {
                    var qualKey = string.IsNullOrEmpty(mapping.SourceDataset)
                        ? mapping.SourceField
                        : $"{mapping.SourceDataset}.{mapping.SourceField}";

                    string? rawVal = null;
                    if (record.Fields.TryGetValue(qualKey, out var qv)) rawVal = qv?.ToString();
                    else if (record.Fields.TryGetValue(mapping.SourceField, out var sv)) rawVal = sv?.ToString();

                    var before = rawVal ?? string.Empty;
                    var after = before;
                    string? transformType = null;

                    // Apply value mapping preview
                    var vmRule = mapping.Transformations.FirstOrDefault(t =>
                        string.Equals(t.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));
                    if (vmRule?.Rules != null && vmRule.Rules.TryGetValue(before.Trim(), out var mapped))
                    {
                        after = mapped;
                        transformType = "VALUE_MAPPING";
                    }

                    // Apply date formatting preview
                    var dateRule = mapping.Transformations.FirstOrDefault(t =>
                        string.Equals(t.Operation, "DATE_FORMATTING", StringComparison.OrdinalIgnoreCase));
                    if (dateRule != null && DateTime.TryParse(before, out var dt))
                    {
                        var fmt = dateRule.Format ?? "yyyyMMdd";
                        after = dt.ToString(fmt.Replace("YYYY", "yyyy").Replace("DD", "dd"));
                        transformType = "DATE_FORMATTING";
                    }

                    if (before != after || transformType != null)
                    {
                        rows.Add(new DataReconciliation.Application.DTOs.TransformationPreviewRowDto
                        {
                            RowIndex = record.RowIndex,
                            TargetField = mapping.TargetField,
                            SourceField = mapping.SourceField,
                            BeforeValue = before,
                            AfterValue = after,
                            Changed = before != after,
                            TransformationType = transformType
                        });
                    }
                }
            }

            return Ok(new { rows, totalRows = rows.Count });
        }

        // ═══════════════════════════════════════════════════════════════
        // ENHANCEMENT 1 — Delta File Upload
        // ═══════════════════════════════════════════════════════════════

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeltaUploadSource(string jobId, IFormFile? file, string? datasetId = null)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { error = "A file is required." });

            if (_deltaFileUpload == null)
                return StatusCode(500, new { error = "Delta file upload service not available." });

            var id = datasetId ?? Path.GetFileNameWithoutExtension(file.FileName);
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var type = extension switch
            {
                ".csv" => Domain.Enums.DatasetType.CSV,
                ".xlsx" or ".xls" => Domain.Enums.DatasetType.EXCEL,
                ".json" => Domain.Enums.DatasetType.JSON,
                _ => Domain.Enums.DatasetType.CSV
            };

            try
            {
                var result = await _deltaFileUpload.ProcessDeltaUploadAsync(
                    jobId, file.OpenReadStream(), file.FileName, id,
                    Domain.Enums.DatasetRole.SOURCE, type);

                _logger.LogInformation(
                    "Delta source file uploaded. JobId={JobId} File={File} RowsAdded={Rows} MappingsUpdated={Mappings}",
                    jobId, file.FileName, result.RowsAdded, result.MappingsUpdated);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delta source upload failed. JobId={JobId} File={File}", jobId, file.FileName);
                return StatusCode(500, new { error = $"Delta upload failed: {ex.Message}" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeltaUploadTargetSchema(string jobId, IFormFile? file)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { error = "A file is required." });

            if (_deltaFileUpload == null)
                return StatusCode(500, new { error = "Delta file upload service not available." });

            var id = "target_schema";
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var type = extension switch
            {
                ".xlsx" or ".xls" => Domain.Enums.DatasetType.EXCEL,
                ".csv" => Domain.Enums.DatasetType.CSV,
                _ => Domain.Enums.DatasetType.CSV
            };

            try
            {
                var result = await _deltaFileUpload.ProcessDeltaUploadAsync(
                    jobId, file.OpenReadStream(), file.FileName, id,
                    Domain.Enums.DatasetRole.TARGET_SCHEMA, type);

                _logger.LogInformation(
                    "Delta target schema uploaded. JobId={JobId} File={File}",
                    jobId, file.FileName);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delta target schema upload failed. JobId={JobId} File={File}", jobId, file.FileName);
                return StatusCode(500, new { error = $"Delta upload failed: {ex.Message}" });
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ENHANCEMENT 2 — Reconciliation Configuration
        // ═══════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetReconciliationConfig(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            if (_reconciliationConfig == null)
                return StatusCode(500, new { error = "Reconciliation config service not available." });

            var config = await _reconciliationConfig.LoadConfigAsync(jobId);
            if (config == null)
                config = await _reconciliationConfig.SuggestReconciliationFieldsAsync(jobId);

            return Json(new
            {
                jobId = config.JobId,
                configuredAt = config.ConfiguredAt,
                reconciliationKeys = config.ReconciliationKeys,
                fields = config.Fields.Select(f => new
                {
                    fieldName = f.FieldName,
                    priority = f.Priority,
                    isMandatory = f.IsMandatory,
                    reason = f.Reason,
                    confidence = f.Confidence,
                    validationType = f.ValidationType ?? "COUNT"
                })
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveReconciliationConfig([FromQuery] string jobId, [FromBody] Application.DTOs.SaveReconciliationConfigRequest request)
        {
            if (string.IsNullOrWhiteSpace(jobId) || request == null)
                return BadRequest(new { error = "jobId and request are required." });

            if (_reconciliationConfig == null)
                return StatusCode(500, new { error = "Reconciliation config service not available." });

            var config = new Domain.Models.ReconciliationConfig
            {
                JobId = jobId,
                Fields = request.Fields.Select(f => new Domain.Models.ReconciliationFieldConfig
                {
                    FieldName = f.FieldName,
                    Priority = f.Priority,
                    IsMandatory = f.IsMandatory,
                    Reason = f.Reason,
                    Confidence = f.Confidence,
                    ValidationType = f.ValidationType ?? "COUNT"
                }).ToList()
            };

            await _reconciliationConfig.SaveConfigAsync(jobId, config);

            _logger.LogInformation("Reconciliation config saved. JobId={JobId} Fields={Count}", jobId, config.Fields.Count);

            return Ok(new { saved = config.Fields.Count, reconciliationKeys = config.ReconciliationKeys });
        }

        [HttpGet]
        public async Task<IActionResult> GetReconciliationDashboard(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            if (_reconciliationConfig == null)
                return StatusCode(500, new { error = "Reconciliation config service not available." });

            var dashboard = await _reconciliationConfig.GetDashboardAsync(jobId);
            return Json(dashboard);
        }

        // ═══════════════════════════════════════════════════════════════
        // ENHANCEMENT 3 — Transformation Override
        // ═══════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetTransformationOverrides(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            if (_transformationOverride == null)
                return StatusCode(500, new { error = "Transformation override service not available." });

            var overrides = await _transformationOverride.GetOverridesAsync(jobId);

            return Json(new
            {
                overrides,
                catalog = Application.Services.TransformationOverrideService.TransformationCatalog
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTransformationOverrides([FromQuery] string jobId, [FromBody] Application.DTOs.SaveTransformationOverridesRequest request)
        {
            if (string.IsNullOrWhiteSpace(jobId) || request == null)
                return BadRequest(new { error = "jobId and request are required." });

            if (_transformationOverride == null)
                return StatusCode(500, new { error = "Transformation override service not available." });

            await _transformationOverride.SaveOverridesAsync(jobId, request.Overrides);
            await _transformationOverride.ApplyOverridesToFinalMappingAsync(jobId);

            _logger.LogInformation(
                "Transformation overrides saved and applied. JobId={JobId} Count={Count}",
                jobId, request.Overrides.Count(o => !string.IsNullOrWhiteSpace(o.OverrideTransformation)));

            return Ok(new
            {
                saved = request.Overrides.Count,
                overridden = request.Overrides.Count(o => !string.IsNullOrWhiteSpace(o.OverrideTransformation))
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // ENHANCEMENT 4 — Enhanced Value Mapping Candidates (with exclusion info)
        // ═══════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetEnhancedValueMappingCandidates(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            var doc = await _artifactPersistence.LoadArtifactAsync<Domain.Models.ValueMappingCandidatesDocument>(
                jobId, Domain.Enums.ArtifactType.ValueMappingCandidates);

            if (doc == null && _valueMappingDiscovery != null)
                doc = await _valueMappingDiscovery.DiscoverAsync(jobId);

            doc ??= new Domain.Models.ValueMappingCandidatesDocument { JobId = jobId };

            // Also build exclusion info for all mapped fields
            var finalMapping = await _artifactPersistence.LoadArtifactAsync<Domain.Models.FinalMappingConfig>(
                jobId, Domain.Enums.ArtifactType.FinalMappingConfig);

            var threshold = 20; // Will match the service's configured threshold
            var allFields = new List<Application.DTOs.ValueMappingCandidateEnhancedDto>();

            if (finalMapping != null)
            {
                foreach (var mapping in finalMapping.Mappings
                    .Where(m => !string.IsNullOrWhiteSpace(m.SourceField) && m.SourceField != "[UNRESOLVED]"))
                {
                    var candidate = doc.Candidates.FirstOrDefault(c =>
                        c.TargetField.Equals(mapping.TargetField, StringComparison.OrdinalIgnoreCase));

                    var isIdentifier = ValueMappingDiscoveryService.IsIdentifierField(mapping.SourceField)
                        || ValueMappingDiscoveryService.IsIdentifierField(mapping.TargetField);

                    if (candidate != null)
                    {
                        var existingRules = mapping.Transformations
                            .FirstOrDefault(t => string.Equals(t.Operation, "VALUE_MAPPING", StringComparison.OrdinalIgnoreCase));
                        var configuredCount = existingRules?.Rules?.Count ?? 0;
                        var coverage = candidate.DistinctValueCount > 0
                            ? Math.Round((double)configuredCount / candidate.DistinctValueCount * 100, 1)
                            : 0.0;

                        allFields.Add(new Application.DTOs.ValueMappingCandidateEnhancedDto
                        {
                            SourceField = candidate.SourceField,
                            SourceDataset = candidate.SourceDataset,
                            TargetField = candidate.TargetField,
                            DistinctSourceValues = candidate.DistinctSourceValues,
                            DistinctValueCount = candidate.DistinctValueCount,
                            Status = configuredCount >= candidate.DistinctValueCount && candidate.DistinctValueCount > 0 ? "Complete"
                                : configuredCount > 0 ? "Partial"
                                : candidate.Status,
                            SuggestedMappings = candidate.SuggestedMappings.Select(r => new Application.DTOs.ValueMappingCandidateRuleDto
                            {
                                SourceValue = r.SourceValue,
                                TargetValue = r.TargetValue,
                                Confidence = r.Confidence,
                                IsAiSuggested = r.IsAiSuggested
                            }).ToList(),
                            AiConfidence = candidate.AiConfidence,
                            ConfiguredRuleCount = configuredCount,
                            CoveragePercent = coverage,
                            IsCandidate = true,
                            CandidateReason = "Low Cardinality",
                            ExcludedReason = null
                        });
                    }
                    else if (isIdentifier)
                    {
                        allFields.Add(new Application.DTOs.ValueMappingCandidateEnhancedDto
                        {
                            SourceField = mapping.SourceField,
                            SourceDataset = mapping.SourceDataset,
                            TargetField = mapping.TargetField,
                            DistinctValueCount = 0,
                            Status = "Excluded",
                            IsCandidate = false,
                            CandidateReason = string.Empty,
                            ExcludedReason = "Identifier Field"
                        });
                    }
                }
            }

            return Json(new
            {
                candidates = allFields.Where(f => f.IsCandidate).ToList(),
                excluded = allFields.Where(f => !f.IsCandidate).ToList(),
                threshold,
                kpis = new
                {
                    total = allFields.Count(f => f.IsCandidate),
                    complete = allFields.Count(f => f.IsCandidate && f.Status == "Complete"),
                    partial = allFields.Count(f => f.IsCandidate && f.Status == "Partial"),
                    pending = allFields.Count(f => f.IsCandidate && f.Status == "Pending"),
                    excluded = allFields.Count(f => !f.IsCandidate),
                    averageCoverage = allFields.Where(f => f.IsCandidate).Any()
                        ? Math.Round(allFields.Where(f => f.IsCandidate).Average(f => f.CoveragePercent), 1) : 0.0
                }
            });
        }

}

}
