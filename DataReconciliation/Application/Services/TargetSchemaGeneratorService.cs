using ClosedXML.Excel;
using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using ExcelDataReader;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class TargetSchemaGeneratorService : ITargetSchemaGeneratorService
    {
        private const string DatasetId = "SCHEMA_GENERATOR_SOURCE";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        private readonly ILogger<TargetSchemaGeneratorService> _logger;
        private readonly IFileIngestionService _fileIngestionService;
        private readonly ISourceSchemaProfilingService _sourceSchemaProfilingService;
        private readonly ISemanticSchemaEnrichmentService _semanticSchemaEnrichmentService;
        private readonly ITargetMetadataExtractionService _targetMetadataExtractionService;
        private readonly IArtifactPersistenceService _artifactPersistenceService;
        private readonly IAISettingsService _aiSettingsService;

        public TargetSchemaGeneratorService(
            ILogger<TargetSchemaGeneratorService> logger,
            IFileIngestionService fileIngestionService,
            ISourceSchemaProfilingService sourceSchemaProfilingService,
            ISemanticSchemaEnrichmentService semanticSchemaEnrichmentService,
            ITargetMetadataExtractionService targetMetadataExtractionService,
            IArtifactPersistenceService artifactPersistenceService,
            IAISettingsService aiSettingsService)
        {
            _logger = logger;
            _fileIngestionService = fileIngestionService;
            _sourceSchemaProfilingService = sourceSchemaProfilingService;
            _semanticSchemaEnrichmentService = semanticSchemaEnrichmentService;
            _targetMetadataExtractionService = targetMetadataExtractionService;
            _artifactPersistenceService = artifactPersistenceService;
            _aiSettingsService = aiSettingsService;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public async Task<TargetSchemaGenerationResultDto> GenerateAsync(
            string jobId,
            string schemaName,
            string sourceFileName,
            string sourceFilePath,
            bool useAiEnrichment = true)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation(
                "Starting target schema generation. JobId={JobId} SchemaName={SchemaName} SourceFile={SourceFile}",
                jobId,
                schemaName,
                sourceFileName);

            var sourceProfile = await BuildSourceProfileAsync(jobId, sourceFilePath, sourceFileName);
            await _sourceSchemaProfilingService.PersistProfileAsync(jobId, sourceProfile);

            SemanticSchemaProfile? semanticProfile = null;
            if (useAiEnrichment)
            {
                semanticProfile = await _semanticSchemaEnrichmentService.EnrichSchemaAsync(jobId, sourceProfile);
            }

            var semanticLookup = semanticProfile?
                .EnrichedFields
                .ToDictionary(field => field.FieldName, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, SemanticFieldEnrichment>(StringComparer.OrdinalIgnoreCase);

            var fields = sourceProfile.Fields
                .OrderBy(field => field.ColumnIndex)
                .Select(field => BuildFieldSuggestion(field, semanticLookup, useAiEnrichment))
                .ToList();

            var targetMetadataProfile = new TargetMetadataProfile
            {
                JobId = jobId,
                TargetDataset = $"{SanitizeFileName(schemaName)}.xlsx",
                ExtractedAt = DateTime.UtcNow,
                Fields = fields.Select(field => new TargetFieldMetadata
                {
                    FieldName = field.TargetField,
                    Datatype = field.Datatype,
                    Format = field.Format,
                    Rule = string.IsNullOrWhiteSpace(field.AllowedValues) ? null : $"Allowed values: {field.AllowedValues}",
                    Description = field.Description,
                    IsRequired = !field.Nullable,
                    FieldLength = field.FieldLength,
                    ColumnOrder = field.ColumnOrder
                }).ToList()
            };

            await _targetMetadataExtractionService.PersistMetadataProfileAsync(jobId, targetMetadataProfile);

            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            var workbookFileName = $"target_schema_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";
            var workbookPath = Path.Combine(reportsPath, workbookFileName);
            await CreateWorkbookAsync(workbookPath, schemaName, new[] { sourceFileName }, fields, useAiEnrichment);

            var result = new TargetSchemaGenerationResultDto
            {
                JobId = jobId,
                SchemaName = schemaName,
                SourceFileName = sourceFileName,
                SourceFileNames = new List<string> { sourceFileName },
                GeneratedAt = DateTime.UtcNow,
                UsedAiEnrichment = useAiEnrichment,
                WorkbookFileName = workbookFileName,
                Fields = fields
            };

            await _artifactPersistenceService.PersistArtifactAsync(
                jobId,
                result,
                ArtifactType.WorkflowLog,
                "target_schema_generation_result.json");

            stopwatch.Stop();
            _logger.LogInformation(
                "Target schema generation completed. JobId={JobId} FieldCount={FieldCount} Duration={Duration}ms Workbook={Workbook}",
                jobId,
                result.Fields.Count,
                stopwatch.ElapsedMilliseconds,
                workbookFileName);

            return result;
        }

        public async Task<string?> ResolveWorkbookPathAsync(string jobId, string fileName)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            var safeFileName = Path.GetFileName(fileName);
            var fullPath = Path.GetFullPath(Path.Combine(reportsPath, safeFileName));
            return File.Exists(fullPath) ? fullPath : null;
        }

        public async Task<TargetSchemaGenerationResultDto> SaveEditsAsync(SaveSchemaEditsRequest request)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(request.JobId, "reports");
            var workbookFileName = $"target_schema_{DateTime.UtcNow:yyyyMMddHHmmss}_edited.xlsx";
            var workbookPath = Path.Combine(reportsPath, workbookFileName);

            var saveFileNames = request.SourceFileNames.Count > 0
                ? request.SourceFileNames
                : new List<string> { request.SourceFileName };
            await CreateWorkbookAsync(workbookPath, request.SchemaName, saveFileNames,
                request.Fields, request.UsedAiEnrichment);

            var result = new TargetSchemaGenerationResultDto
            {
                JobId = request.JobId,
                SchemaName = request.SchemaName,
                SourceFileName = request.SourceFileName,
                SourceFileNames = saveFileNames,
                IsMultiSource = request.IsMultiSource,
                GeneratedAt = DateTime.UtcNow,
                UsedAiEnrichment = request.UsedAiEnrichment,
                WorkbookFileName = workbookFileName,
                Fields = request.Fields
            };

            await _artifactPersistenceService.PersistArtifactAsync(
                request.JobId, result, ArtifactType.WorkflowLog, "target_schema_generation_result.json");

            _logger.LogInformation(
                "Target schema edits saved. JobId={JobId} FieldCount={FieldCount} Workbook={Workbook}",
                request.JobId, request.Fields.Count, workbookFileName);

            return result;
        }

        // ── GenerateMultiSourceAsync ───────────────────────────────────────────────

        public async Task<TargetSchemaGenerationResultDto> GenerateMultiSourceAsync(
            string jobId,
            string schemaName,
            IReadOnlyList<(string FileName, string FilePath)> sourceFiles,
            bool useAiEnrichment = true)
        {
            _logger.LogInformation(
                "Starting multi-source schema generation. JobId={JobId} Files={Count}",
                jobId, sourceFiles.Count);

            // Profile each source file independently
            var profiles = new List<(string FileName, SourceSchemaProfile Profile)>();
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                var (fileName, filePath) = sourceFiles[i];
                var profile = await BuildSourceProfileAsync(jobId, filePath, fileName);
                await _sourceSchemaProfilingService.PersistProfileAsync(jobId, profile);
                profiles.Add((fileName, profile));
            }

            List<TargetSchemaFieldSuggestionDto> fields;

            if (useAiEnrichment)
            {
                fields = await CallPythonMultiSourceAsync(jobId, schemaName, profiles)
                         ?? BuildHeuristicMultiSourceFields(profiles);
            }
            else
            {
                fields = BuildHeuristicMultiSourceFields(profiles);
            }

            var fileNames = sourceFiles.Select(sf => sf.FileName).ToList();
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            var workbookFileName = $"target_schema_{DateTime.UtcNow:yyyyMMddHHmmss}_multi.xlsx";
            var workbookPath = Path.Combine(reportsPath, workbookFileName);
            await CreateWorkbookAsync(workbookPath, schemaName, fileNames, fields, useAiEnrichment);

            var result = new TargetSchemaGenerationResultDto
            {
                JobId = jobId,
                SchemaName = schemaName,
                SourceFileName = fileNames.FirstOrDefault() ?? string.Empty,
                SourceFileNames = fileNames,
                IsMultiSource = true,
                GeneratedAt = DateTime.UtcNow,
                UsedAiEnrichment = useAiEnrichment,
                WorkbookFileName = workbookFileName,
                Fields = fields
            };

            await _artifactPersistenceService.PersistArtifactAsync(
                jobId, result, ArtifactType.WorkflowLog, "target_schema_generation_result.json");

            _logger.LogInformation(
                "Multi-source schema generation completed. JobId={JobId} Fields={Count} Workbook={Workbook}",
                jobId, fields.Count, workbookFileName);

            return result;
        }

        private async Task<List<TargetSchemaFieldSuggestionDto>?> CallPythonMultiSourceAsync(
            string jobId,
            string schemaName,
            List<(string FileName, SourceSchemaProfile Profile)> profiles)
        {
            try
            {
                var settings = _aiSettingsService.Load();
                var pythonUrl = settings.PythonServiceUrl.TrimEnd('/');

                var requestPayload = new
                {
                    schema_name = schemaName,
                    source_files = profiles.Select((p, i) => new
                    {
                        file_name = p.FileName,
                        file_index = i,
                        fields = p.Profile.Fields.Select(f => new
                        {
                            field_name = f.FieldName,
                            inferred_type = MapDatatype(f.Datatype),
                            max_length = f.MaxLength,
                            nullable = f.Nullable,
                            sample_values = f.SampleValues.Take(3).ToList()
                        }).ToList()
                    }).ToList()
                };

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                var json = JsonSerializer.Serialize(requestPayload, _jsonOpts);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.PostAsync($"{pythonUrl}/api/multi-source-schema", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                var fields = new List<TargetSchemaFieldSuggestionDto>();
                int colOrder = 0;
                foreach (var fieldEl in root.GetProperty("fields").EnumerateArray())
                {
                    var mappings = new List<SourceFieldMappingDto>();
                    if (fieldEl.TryGetProperty("source_mappings", out var mappingsEl))
                    {
                        foreach (var m in mappingsEl.EnumerateArray())
                        {
                            mappings.Add(new SourceFieldMappingDto
                            {
                                SourceFileName  = m.TryGetProperty("source_file_name", out var sfn) ? sfn.GetString() ?? "" : "",
                                SourceFileIndex = m.TryGetProperty("source_file_index", out var sfi) ? sfi.GetInt32() : 0,
                                SourceField     = m.TryGetProperty("source_field", out var sf)       ? sf.GetString()  ?? "" : "",
                                MergeRule       = m.TryGetProperty("merge_rule", out var mr)         ? mr.GetString()  ?? "PRIMARY" : "PRIMARY",
                            });
                        }
                    }

                    fields.Add(new TargetSchemaFieldSuggestionDto
                    {
                        ColumnOrder     = colOrder++,
                        SourceField     = mappings.FirstOrDefault()?.SourceField ?? "",
                        SourceMappings  = mappings,
                        TargetField     = fieldEl.TryGetProperty("target_field", out var tf)   ? tf.GetString()  ?? "" : "",
                        Datatype        = fieldEl.TryGetProperty("datatype", out var dt)        ? dt.GetString()  ?? "STRING" : "STRING",
                        FieldLength     = fieldEl.TryGetProperty("field_length", out var fl) && fl.ValueKind != JsonValueKind.Null ? fl.GetInt32() : null,
                        Nullable        = fieldEl.TryGetProperty("nullable", out var nu)       ? nu.GetBoolean() : true,
                        Description     = fieldEl.TryGetProperty("description", out var desc)  ? desc.GetString() : null,
                        BusinessCategory= fieldEl.TryGetProperty("business_category", out var bc) ? bc.GetString() : null,
                        Confidence      = fieldEl.TryGetProperty("confidence", out var conf)   ? conf.GetDouble() : 0.80,
                        GenerationMethod = "AI MULTI-SOURCE",
                    });
                }

                _logger.LogInformation(
                    "Python multi-source call succeeded. JobId={JobId} Fields={Count}", jobId, fields.Count);
                return fields;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Python multi-source call failed for JobId={JobId}. Falling back to heuristic merge.", jobId);
                return null;
            }
        }

        private static List<TargetSchemaFieldSuggestionDto> BuildHeuristicMultiSourceFields(
            List<(string FileName, SourceSchemaProfile Profile)> profiles)
        {
            // Group source fields by normalized name; first file = PRIMARY, rest = FALLBACK
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);  // norm → colOrder
            var fields = new List<TargetSchemaFieldSuggestionDto>();

            for (int fileIdx = 0; fileIdx < profiles.Count; fileIdx++)
            {
                var (fileName, profile) = profiles[fileIdx];
                foreach (var srcField in profile.Fields.OrderBy(f => f.ColumnIndex))
                {
                    var normKey = Regex.Replace(srcField.FieldName.ToUpperInvariant(), "[^A-Z0-9]", "_");
                    var mergeRule = seen.ContainsKey(normKey) ? "FALLBACK" : "PRIMARY";

                    if (mergeRule == "PRIMARY")
                    {
                        var colOrder = fields.Count;
                        seen[normKey] = colOrder;
                        fields.Add(new TargetSchemaFieldSuggestionDto
                        {
                            ColumnOrder  = colOrder,
                            SourceField  = srcField.FieldName,
                            TargetField  = ToTargetFieldName(srcField.FieldName, colOrder),
                            Datatype     = MapDatatype(srcField.Datatype),
                            FieldLength  = srcField.MaxLength > 0 ? srcField.MaxLength : null,
                            Nullable     = srcField.Nullable,
                            SampleValue  = srcField.SampleValues.FirstOrDefault(),
                            Description  = srcField.PossibleMeaning,
                            Confidence   = 0.75,
                            GenerationMethod = "PROFILED MULTI-SOURCE",
                            SourceMappings = new List<SourceFieldMappingDto>
                            {
                                new() { SourceFileName = fileName, SourceFileIndex = fileIdx, SourceField = srcField.FieldName, MergeRule = "PRIMARY" }
                            }
                        });
                    }
                    else
                    {
                        // Append FALLBACK mapping to the existing target field
                        var existingIdx = seen[normKey];
                        fields[existingIdx].SourceMappings.Add(new SourceFieldMappingDto
                        {
                            SourceFileName  = fileName,
                            SourceFileIndex = fileIdx,
                            SourceField     = srcField.FieldName,
                            MergeRule       = "FALLBACK"
                        });
                        // Widen FieldLength if this source has a longer value
                        if (srcField.MaxLength > (fields[existingIdx].FieldLength ?? 0))
                            fields[existingIdx].FieldLength = srcField.MaxLength;
                    }
                }
            }

            return fields;
        }

        // ── DownloadMappingAsync ───────────────────────────────────────────────────

        public async Task<(byte[] Bytes, string FileName)> DownloadMappingAsync(string jobId)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            var jsonPath = Path.Combine(reportsPath, "target_schema_generation_result.json");

            TargetSchemaGenerationResultDto? result = null;
            if (File.Exists(jsonPath))
            {
                var raw = await File.ReadAllTextAsync(jsonPath);
                result = JsonSerializer.Deserialize<TargetSchemaGenerationResultDto>(raw, _jsonOpts);
            }

            result ??= new TargetSchemaGenerationResultDto { JobId = jobId, SchemaName = "Schema" };

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Mapping");

            // Header row
            var headers = new[] { "Target Field", "Source File", "Source Field", "Merge Rule",
                                   "Datatype", "Field Length", "Nullable", "Format", "Allowed Values", "Description" };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
            ws.Range(1, 1, 1, headers.Length).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;

            // Validation for Merge Rule column (D)
            var mergeValidation = ws.Column(4).SetDataValidation();
            mergeValidation.List("\"PRIMARY,FALLBACK,CONCAT\"");

            int row = 2;
            foreach (var field in result.Fields.OrderBy(f => f.ColumnOrder))
            {
                var mappings = field.SourceMappings.Count > 0
                    ? field.SourceMappings
                    : new List<SourceFieldMappingDto> { new() { SourceFileName = result.SourceFileName, SourceField = field.SourceField, MergeRule = "PRIMARY" } };

                foreach (var m in mappings)
                {
                    ws.Cell(row, 1).Value = field.TargetField;
                    ws.Cell(row, 2).Value = m.SourceFileName;
                    ws.Cell(row, 3).Value = m.SourceField;
                    ws.Cell(row, 4).Value = m.MergeRule;
                    ws.Cell(row, 5).Value = field.Datatype;
                    ws.Cell(row, 6).Value = field.FieldLength;
                    ws.Cell(row, 7).Value = field.Nullable ? "Y" : "N";
                    ws.Cell(row, 8).Value = field.Format;
                    ws.Cell(row, 9).Value = field.AllowedValues;
                    ws.Cell(row, 10).Value = field.Description;
                    row++;
                }
            }

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // Instructions sheet
            var ins = wb.Worksheets.Add("Instructions");
            ins.Cell(1, 1).Value = "How to edit this mapping file";
            ins.Cell(1, 1).Style.Font.Bold = true;
            ins.Cell(2, 1).Value = "• Each row is one source-to-target field mapping.";
            ins.Cell(3, 1).Value = "• For a target field with multiple source inputs, add one row per source — all rows must have the same Target Field value.";
            ins.Cell(4, 1).Value = "• Merge Rule: PRIMARY = canonical source, FALLBACK = used when PRIMARY is null, CONCAT = values are concatenated.";
            ins.Cell(5, 1).Value = "• You may edit: Source File, Source Field, Merge Rule, Datatype, Field Length, Nullable, Format, Allowed Values, Description.";
            ins.Cell(6, 1).Value = "• Do NOT rename the 'Mapping' sheet or remove columns.";
            ins.Column(1).Width = 90;

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();
            var fileName = $"mapping_{SanitizeFileName(result.SchemaName)}_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";
            return (bytes, fileName);
        }

        // ── ImportMappingAsync ────────────────────────────────────────────────────

        public async Task<TargetSchemaGenerationResultDto> ImportMappingAsync(
            string jobId,
            string schemaName,
            Stream mappingStream)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var reader = ExcelReaderFactory.CreateReader(mappingStream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
            });

            DataTable? table = null;
            foreach (DataTable dt in dataSet.Tables)
            {
                if (dt.TableName.Equals("Mapping", StringComparison.OrdinalIgnoreCase))
                { table = dt; break; }
            }
            table ??= dataSet.Tables.Count > 0 ? dataSet.Tables[0] : null;
            if (table == null)
                throw new InvalidOperationException("Could not find the 'Mapping' sheet in the uploaded file.");

            // Group rows by Target Field (preserving first-occurrence order)
            var grouped = new Dictionary<string, (TargetSchemaFieldSuggestionDto Field, List<SourceFieldMappingDto> Mappings)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            string Cell(DataRow r, string col)
            {
                if (!table.Columns.Contains(col)) return string.Empty;
                return r.IsNull(col) ? string.Empty : (r[col]?.ToString() ?? string.Empty).Trim();
            }

            foreach (DataRow row in table.Rows)
            {
                var targetField = Cell(row, "Target Field");
                if (string.IsNullOrWhiteSpace(targetField)) continue;

                var mapping = new SourceFieldMappingDto
                {
                    SourceFileName  = Cell(row, "Source File"),
                    SourceField     = Cell(row, "Source Field"),
                    MergeRule       = Cell(row, "Merge Rule").ToUpperInvariant() is "FALLBACK" or "CONCAT" ? Cell(row, "Merge Rule").ToUpperInvariant() : "PRIMARY",
                };

                if (!grouped.ContainsKey(targetField))
                {
                    order.Add(targetField);
                    grouped[targetField] = (new TargetSchemaFieldSuggestionDto
                    {
                        TargetField     = targetField,
                        SourceField     = mapping.SourceField,
                        Datatype        = Cell(row, "Datatype") is { Length: > 0 } dt ? dt.ToUpperInvariant() : "STRING",
                        FieldLength     = int.TryParse(Cell(row, "Field Length"), out var fl) ? fl : null,
                        Nullable        = Cell(row, "Nullable").Equals("Y", StringComparison.OrdinalIgnoreCase),
                        Format          = Cell(row, "Format"),
                        AllowedValues   = Cell(row, "Allowed Values"),
                        Description     = Cell(row, "Description"),
                        Confidence      = 0.90,
                        GenerationMethod = "IMPORTED",
                    }, new List<SourceFieldMappingDto>());
                }

                grouped[targetField].Mappings.Add(mapping);
            }

            var fields = order.Select((tf, i) =>
            {
                var (f, mappings) = grouped[tf];
                f.ColumnOrder     = i;
                f.SourceMappings  = mappings;
                f.SourceField     = mappings.FirstOrDefault()?.SourceField ?? f.SourceField;
                return f;
            }).ToList();

            // Load prior result for metadata, or build minimal one
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            TargetSchemaGenerationResultDto? prior = null;
            var priorPath = Path.Combine(reportsPath, "target_schema_generation_result.json");
            if (File.Exists(priorPath))
            {
                var raw = await File.ReadAllTextAsync(priorPath);
                prior = JsonSerializer.Deserialize<TargetSchemaGenerationResultDto>(raw, _jsonOpts);
            }

            var effectiveSchemaName  = !string.IsNullOrWhiteSpace(schemaName)  ? schemaName  : (prior?.SchemaName ?? "Imported Schema");
            var sourceFileNames      = prior?.SourceFileNames.Count > 0 ? prior.SourceFileNames : new List<string> { prior?.SourceFileName ?? "imported" };

            var workbookFileName = $"target_schema_{DateTime.UtcNow:yyyyMMddHHmmss}_imported.xlsx";
            var workbookPath = Path.Combine(reportsPath, workbookFileName);
            await CreateWorkbookAsync(workbookPath, effectiveSchemaName, sourceFileNames, fields, prior?.UsedAiEnrichment ?? false);

            var result = new TargetSchemaGenerationResultDto
            {
                JobId            = jobId,
                SchemaName       = effectiveSchemaName,
                SourceFileName   = sourceFileNames.FirstOrDefault() ?? string.Empty,
                SourceFileNames  = sourceFileNames,
                IsMultiSource    = prior?.IsMultiSource ?? sourceFileNames.Count > 1,
                GeneratedAt      = DateTime.UtcNow,
                UsedAiEnrichment = prior?.UsedAiEnrichment ?? false,
                WorkbookFileName = workbookFileName,
                Fields           = fields
            };

            await _artifactPersistenceService.PersistArtifactAsync(
                jobId, result, ArtifactType.WorkflowLog, "target_schema_generation_result.json");

            _logger.LogInformation(
                "Mapping import completed. JobId={JobId} Fields={Count} Workbook={Workbook}",
                jobId, fields.Count, workbookFileName);

            return result;
        }

        private async Task<SourceSchemaProfile> BuildSourceProfileAsync(string jobId, string sourceFilePath, string sourceFileName)
        {
            var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            return extension switch
            {
                ".csv" or ".txt" => await _sourceSchemaProfilingService.ProfileSourceFileAsync(jobId, DatasetId, sourceFilePath),
                ".xlsx" or ".xls" => await ProfileExcelAsync(jobId, sourceFilePath, sourceFileName),
                _ => throw new NotSupportedException($"Unsupported source file extension '{extension}' for target schema generation.")
            };
        }

        private async Task<SourceSchemaProfile> ProfileExcelAsync(string jobId, string sourceFilePath, string sourceFileName)
        {
            var profile = new SourceSchemaProfile
            {
                JobId = jobId,
                Dataset = sourceFileName,
                DatasetId = DatasetId,
                ProfiledAt = DateTime.UtcNow
            };

            using var stream = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
            });

            if (dataSet.Tables.Count == 0)
            {
                return profile;
            }

            var table = dataSet.Tables[0];
            for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var header = NormalizeHeader(table.Columns[columnIndex].ColumnName, columnIndex);
                var values = new List<string>();

                foreach (DataRow row in table.Rows)
                {
                    if (values.Count >= 1000)
                    {
                        break;
                    }

                    var value = row.IsNull(columnIndex) ? string.Empty : row[columnIndex]?.ToString() ?? string.Empty;
                    values.Add(value);
                }

                var nonEmptyValues = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                profile.Fields.Add(new SourceFieldProfile
                {
                    FieldName = header,
                    ColumnIndex = columnIndex,
                    Nullable = values.Any(string.IsNullOrWhiteSpace),
                    MaxLength = values.Count > 0 ? values.Max(value => value.Length) : 0,
                    IsUnique = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == nonEmptyValues.Count && nonEmptyValues.Count > 0,
                    SampleValues = nonEmptyValues.Take(5).ToList(),
                    Datatype = InferDatatype(nonEmptyValues),
                    FieldPattern = InferPattern(nonEmptyValues),
                    PossibleMeaning = InferPossibleMeaning(header)
                });
            }

            return await Task.FromResult(profile);
        }

        private TargetSchemaFieldSuggestionDto BuildFieldSuggestion(
            SourceFieldProfile sourceField,
            IReadOnlyDictionary<string, SemanticFieldEnrichment> semanticLookup,
            bool useAiEnrichment)
        {
            semanticLookup.TryGetValue(sourceField.FieldName, out var enrichment);
            var allowedValues = InferAllowedValues(sourceField);
            var format = sourceField.FieldPattern;
            var description = BuildDescription(sourceField, enrichment, allowedValues);
            var confidence = CalculateConfidence(sourceField, enrichment, allowedValues, useAiEnrichment);

            return new TargetSchemaFieldSuggestionDto
            {
                ColumnOrder = sourceField.ColumnIndex,
                SourceField = sourceField.FieldName,
                TargetField = ToTargetFieldName(sourceField.FieldName, sourceField.ColumnIndex),
                Datatype = MapDatatype(sourceField.Datatype),
                FieldLength = sourceField.MaxLength > 0 ? sourceField.MaxLength : null,
                Nullable = sourceField.Nullable,
                SampleValue = sourceField.SampleValues.FirstOrDefault(),
                Format = format,
                AllowedValues = allowedValues,
                Description = description,
                BusinessCategory = enrichment?.BusinessCategory,
                Confidence = confidence,
                GenerationMethod = enrichment?.WasAIEnriched == true
                    ? "PROFILE + AI ENRICHMENT"
                    : "PROFILED FROM SOURCE"
            };
        }

        private async Task CreateWorkbookAsync(
            string workbookPath,
            string schemaName,
            IReadOnlyList<string> sourceFileNames,
            IReadOnlyCollection<TargetSchemaFieldSuggestionDto> fields,
            bool usedAiEnrichment)
        {
            using var workbook = new XLWorkbook();

            // ── Target Schema sheet (index 0) — must be first so ExcelDataReader Tables[0] reads it ──
            var schemaSheet = workbook.Worksheets.Add("Target Schema");
            // ── Core columns (cols 1-5) match TargetMetadataExtractionService lookup headers ──
            // ── Extra info columns (cols 6-10) are informational only ────────────────────────
            var headers = new[]
            {
                "TARGET FIELDS",   // col 1  — looked up by TargetMetadataExtractionService
                "Data Type",       // col 2
                "Value/Format",    // col 3
                "Rule",            // col 4
                "Description",     // col 5
                "Source Field",    // col 6  — informational
                "Length",          // col 7  — informational
                "Nullable",        // col 8  — informational
                "Sample Value",    // col 9  — informational
                "Generation Method" // col 10 — informational
            };

            for (int index = 0; index < headers.Length; index++)
            {
                schemaSheet.Cell(1, index + 1).Value = headers[index];
            }

            schemaSheet.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
            schemaSheet.Range(1, 1, 1, headers.Length).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;

            var rowIndex = 2;
            foreach (var field in fields)
            {
                // Core columns
                var datatypeDisplay = field.FieldLength.HasValue && field.Datatype == "STRING"
                    ? $"STRING({field.FieldLength})"
                    : field.Datatype;
                schemaSheet.Cell(rowIndex, 1).Value = field.TargetField;
                schemaSheet.Cell(rowIndex, 2).Value = datatypeDisplay;
                schemaSheet.Cell(rowIndex, 3).Value = field.Format ?? field.AllowedValues;
                schemaSheet.Cell(rowIndex, 4).Value = field.AllowedValues != null
                    ? $"Allowed values: {field.AllowedValues}"
                    : null;
                schemaSheet.Cell(rowIndex, 5).Value = field.Description;
                // Informational columns
                schemaSheet.Cell(rowIndex, 6).Value = field.SourceField;
                schemaSheet.Cell(rowIndex, 7).Value = field.FieldLength;
                schemaSheet.Cell(rowIndex, 8).Value = field.Nullable ? "Y" : "N";
                schemaSheet.Cell(rowIndex, 9).Value = field.SampleValue;
                schemaSheet.Cell(rowIndex, 10).Value = field.GenerationMethod;
                rowIndex++;
            }

            schemaSheet.SheetView.FreezeRows(1);
            schemaSheet.Columns().AdjustToContents();

            // ── Summary sheet (index 1) ───────────────────────────────────────────────────────
            var summarySheet = workbook.Worksheets.Add("Summary");
            summarySheet.Cell(1, 1).Value = "Schema Name";
            summarySheet.Cell(1, 2).Value = schemaName;
            summarySheet.Cell(2, 1).Value = "Source File(s)";
            summarySheet.Cell(2, 2).Value = string.Join(", ", sourceFileNames);
            summarySheet.Cell(3, 1).Value = "Generated At (UTC)";
            summarySheet.Cell(3, 2).Value = DateTime.UtcNow;
            summarySheet.Cell(4, 1).Value = "AI Enrichment";
            summarySheet.Cell(4, 2).Value = usedAiEnrichment ? "Enabled" : "Disabled";
            summarySheet.Cell(5, 1).Value = "Field Count";
            summarySheet.Cell(5, 2).Value = fields.Count;
            summarySheet.Range(1, 1, 5, 1).Style.Font.Bold = true;
            summarySheet.Columns().AdjustToContents();

            await Task.Run(() => workbook.SaveAs(workbookPath));
        }

        private static string NormalizeHeader(string? header, int columnIndex)
        {
            return string.IsNullOrWhiteSpace(header)
                ? $"COLUMN_{columnIndex + 1}"
                : header.Trim();
        }

        private static string MapDatatype(string datatype)
        {
            return datatype.ToLowerInvariant() switch
            {
                "integer" => "INTEGER",
                "decimal" => "DECIMAL",
                "datetime" => "DATETIME",
                "boolean" => "BOOLEAN",
                _ => "STRING"
            };
        }

        private static string ToTargetFieldName(string sourceField, int columnIndex)
        {
            var normalized = Regex.Replace(sourceField.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "_");
            normalized = Regex.Replace(normalized, "_+", "_").Trim('_');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = $"FIELD_{columnIndex + 1}";
            }

            if (char.IsDigit(normalized[0]))
            {
                normalized = $"F_{normalized}";
            }

            return normalized;
        }

        private static string? InferAllowedValues(SourceFieldProfile sourceField)
        {
            if (sourceField.Datatype.Equals("boolean", StringComparison.OrdinalIgnoreCase))
            {
                return "true, false";
            }

            var distinctValues = sourceField.SampleValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!distinctValues.Any())
            {
                return null;
            }

            if (distinctValues.Count <= 5 && !sourceField.IsUnique && sourceField.Datatype.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(", ", distinctValues);
            }

            return null;
        }

        private static string BuildDescription(
            SourceFieldProfile sourceField,
            SemanticFieldEnrichment? enrichment,
            string? allowedValues)
        {
            var description = enrichment?.InterpretedDescription
                ?? enrichment?.SemanticMeaning
                ?? sourceField.PossibleMeaning
                ?? sourceField.FieldName;

            if (!string.IsNullOrWhiteSpace(allowedValues))
            {
                description = $"{description}. Suggested allowed values: {allowedValues}.";
            }

            return description;
        }

        private static double CalculateConfidence(
            SourceFieldProfile sourceField,
            SemanticFieldEnrichment? enrichment,
            string? allowedValues,
            bool useAiEnrichment)
        {
            var confidence = 0.72;

            if (!string.IsNullOrWhiteSpace(sourceField.PossibleMeaning))
            {
                confidence += 0.08;
            }

            if (!string.IsNullOrWhiteSpace(sourceField.FieldPattern))
            {
                confidence += 0.06;
            }

            if (!string.IsNullOrWhiteSpace(allowedValues))
            {
                confidence += 0.04;
            }

            if (useAiEnrichment && enrichment != null)
            {
                confidence = Math.Max(confidence, enrichment.ConfidenceScore);
            }

            return Math.Min(0.99, Math.Round(confidence, 2));
        }

        private static readonly Regex[] _datePatternRegexes = new[]
        {
            new Regex(@"^\d{4}-\d{2}-\d{2}$"),     // YYYY-MM-DD
            new Regex(@"^\d{2}/\d{2}/\d{4}$"),     // DD/MM/YYYY or MM/DD/YYYY
            new Regex(@"^\d{2}-\d{2}-\d{4}$"),     // DD-MM-YYYY or MM-DD-YYYY
            new Regex(@"^\d{4}/\d{2}/\d{2}$"),     // YYYY/MM/DD
            new Regex(@"^\d{8}$"),                  // YYYYMMDD
        };

        private static string InferDatatype(List<string> values)
        {
            if (!values.Any())
            {
                return "string";
            }

            if (values.All(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                return "integer";
            }

            if (values.All(value => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
            {
                return "decimal";
            }

            // Explicit date pattern matching before DateTime.TryParse (InvariantCulture misses DD/MM/YYYY)
            if (values.All(value => _datePatternRegexes.Any(r => r.IsMatch(value))))
            {
                return "datetime";
            }

            if (values.All(value => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            {
                return "datetime";
            }

            if (values.All(value => value is "true" or "false" or "1" or "0" || value.Equals("Y", StringComparison.OrdinalIgnoreCase) || value.Equals("N", StringComparison.OrdinalIgnoreCase)))
            {
                return "boolean";
            }

            return "string";
        }

        private static string? InferPattern(List<string> values)
        {
            if (!values.Any())
            {
                return null;
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")))
            {
                return "YYYY-MM-DD";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{2}/\d{2}/\d{4}$")))
            {
                return "DD/MM/YYYY";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{2}-\d{2}-\d{4}$")))
            {
                return "DD-MM-YYYY";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{4}/\d{2}/\d{2}$")))
            {
                return "YYYY/MM/DD";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{8}$")))
            {
                return "YYYYMMDD";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d+\.\d{2}$")))
            {
                return "Decimal(2dp)";
            }

            if (values.All(value => Regex.IsMatch(value, @"^\d{10}$")))
            {
                return "Numeric(10)";
            }

            return null;
        }

        private static string InferPossibleMeaning(string fieldName)
        {
            var expansions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ACCT"] = "Account",
                ["ACCT_NO"] = "Account Number",
                ["ADDR"] = "Address",
                ["AMT"] = "Amount",
                ["BAL"] = "Balance",
                ["CD"] = "Code",
                ["CUST"] = "Customer",
                ["DESC"] = "Description",
                ["DT"] = "Date",
                ["ID"] = "Identifier",
                ["INT"] = "Interest",
                ["NM"] = "Name",
                ["NO"] = "Number",
                ["PMT"] = "Payment",
                ["QTY"] = "Quantity",
                ["STAT"] = "Status"
            };

            if (expansions.TryGetValue(fieldName, out var exactMatch))
            {
                return exactMatch;
            }

            var parts = fieldName
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => expansions.TryGetValue(part, out var expansion)
                    ? expansion
                    : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());

            return string.Join(" ", parts);
        }

        private static string SanitizeFileName(string schemaName)
        {
            var invalidCharacters = Path.GetInvalidFileNameChars();
            var sanitized = new string(schemaName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "generated_target_schema" : sanitized;
        }
    }
}