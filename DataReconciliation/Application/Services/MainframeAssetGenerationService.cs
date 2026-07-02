using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Transformations;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    /// <summary>
    /// Mainframe Asset Generation Agent.
    /// Reads existing job artifacts and produces:
    ///   - COBOL copybook (.cpy)
    ///   - COBOL skeleton program (.cbl)   *** AI-Generated Starter – Developer Review Required ***
    ///   - JCL skeleton (.jcl)             *** Skeleton Only – Developer Review Required ***
    ///   - Technical specification (.txt)
    ///   - Sample target records (.dat)
    /// This is an additive module. It does NOT modify existing workflow artifacts.
    /// </summary>
    public class MainframeAssetGenerationService : IMainframeAssetGenerationService
    {
        private readonly ILogger<MainframeAssetGenerationService> _logger;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IFileIngestionService _fileIngestionService;
        private readonly IMainframeAiAgentService _aiAgent;

        public MainframeAssetGenerationService(
            ILogger<MainframeAssetGenerationService> logger,
            IArtifactPersistenceService artifactPersistence,
            IFileIngestionService fileIngestionService,
            IMainframeAiAgentService aiAgent)
        {
            _logger = logger;
            _artifactPersistence = artifactPersistence;
            _fileIngestionService = fileIngestionService;
            _aiAgent = aiAgent;
        }

        // ─── Public Entry Point ───────────────────────────────────────────────

        public async Task<MainframeAssetGenerationResultDto> GenerateAsync(
            string jobId,
            MainframeAssetRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation(
                "Mainframe asset generation started. JobId={JobId} Record={RecordName} App={AppName} " +
                "Copybook={Copybook} Cobol={Cobol} JCL={JCL} Spec={Spec} Samples={Samples}",
                jobId, request.RecordName, request.ApplicationName,
                request.GenerateCopybook, request.GenerateCobolSkeleton,
                request.GenerateJclSkeleton, request.GenerateTechnicalSpec,
                request.GenerateSampleRecords);

            // ── Load input artifacts ──────────────────────────────────────────
            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(
                jobId, ArtifactType.TargetMetadataProfile)
                ?? throw new InvalidOperationException(
                    $"Target metadata profile not found for job '{jobId}'. " +
                    "Run the Target Schema Generator step first.");

            var finalMapping = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(
                jobId, ArtifactType.FinalMappingConfig);

            var valueMappings = await _artifactPersistence.LoadArtifactAsync<ValueMappingsDocument>(
                jobId, ArtifactType.ValueMappings);

            // ── Build canonical field list ────────────────────────────────────
            var mappingLookup = finalMapping?.Mappings
                .GroupBy(m => m.TargetField, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, FinalMapping>(StringComparer.OrdinalIgnoreCase);

            int position = 1;
            var fields = targetProfile.Fields
                .OrderBy(f => f.ColumnOrder)
                .Select(f =>
                {
                    var len = f.FieldLength ?? InferDefaultLength(f);
                    var dto = new MainframeAssetFieldDto
                    {
                        ColumnOrder = f.ColumnOrder,
                        FieldName = f.FieldName,
                        CobolFieldName = ToCobolName(f.FieldName),
                        DataType = f.Datatype,
                        Length = len,
                        StartPosition = position,
                        PictureClause = BuildPic(f),
                        Format = f.Format,
                        Rule = f.Rule,
                        Description = f.Description,
                        IsRequired = f.IsRequired,
                        SourceField = mappingLookup.TryGetValue(f.FieldName, out var m) ? m.SourceField : null
                    };
                    position += len;
                    return dto;
                })
                .ToList();

            var safeRecord = Sanitize(request.RecordName, "MAINFRAME_RECORD");
            var safeApp = Sanitize(request.ApplicationName, "MAINFRAME_APP");
            var safePgm = Sanitize(request.ProgramName, "CUSTMSTR");
            var safeJob = Sanitize(request.JobName, "CUSTLOAD");

            var outputFolder = await GetAssetFolderAsync(jobId);
            Directory.CreateDirectory(outputFolder);
            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            var result = new MainframeAssetGenerationResultDto
            {
                JobId = jobId,
                RecordName = safeRecord,
                ApplicationName = safeApp,
                ProgramName = safePgm,
                JobName = safeJob,
                GeneratedAt = DateTime.UtcNow,
                TotalRecordLength = position - 1,
                Fields = fields
            };

            // ── Pre-load transformation rules for AI context ──────────────────
            // transformation_rules.json is written by TransformationExecutionService;
            // may not exist if the transformation step hasn't run yet (non-fatal).
            var trArtifact = await _artifactPersistence.LoadArtifactByNameAsync<TransformationRulesArtifact>(
                jobId, "transformation_rules.json");
            var trRulesList = trArtifact?.Rules?.Select(r => new Dictionary<string, object?>
            {
                { "sourceField", r.SourceField },
                { "targetField", r.TargetField },
                { "operation",   r.Operation   },
                { "parameters",  r.Parameters  },
                { "confidence",  r.Confidence  },
            }).ToList<Dictionary<string, object?>>();

            // ── Pre-launch AI generation tasks in parallel ────────────────────
            // COBOL and JCL AI calls are independent — run simultaneously to
            // eliminate sequential wait (each call can take 10-30 s on Azure OpenAI).
            var cobolAiTask = (request.UseAiMode && request.GenerateCobolSkeleton)
                ? _aiAgent.RunAgentAsync(BuildAiRequest(
                    jobId, "generate_cobol", safePgm, safeRecord, safeJob,
                    position - 1, fields, finalMapping, valueMappings, trRulesList))
                : null;
            var jclAiTask = (request.UseAiMode && request.GenerateJclSkeleton)
                ? _aiAgent.RunAgentAsync(BuildAiRequest(
                    jobId, "generate_jcl", safePgm, safeRecord, safeJob,
                    position - 1, fields, finalMapping, valueMappings, trRulesList))
                : null;

            if (cobolAiTask != null && jclAiTask != null)
            {
                _logger.LogInformation(
                    "AI Mode: awaiting COBOL + JCL generation in parallel. JobId={JobId}", jobId);
                await Task.WhenAll(cobolAiTask, jclAiTask);
            }
            else if (cobolAiTask != null) await cobolAiTask;
            else if (jclAiTask  != null) await jclAiTask;

            // ── Generate requested assets ─────────────────────────────────────
            if (request.GenerateCopybook)
            {
                var fn = $"{safeRecord.ToLowerInvariant()}_copybook_{ts}.cpy";
                await File.WriteAllTextAsync(
                    Path.Combine(outputFolder, fn),
                    BuildCopybook(safeRecord, safeApp, fields),
                    Encoding.UTF8);
                result.CopybookFileName = fn;
                _logger.LogInformation("Copybook written. JobId={JobId} File={File}", jobId, fn);
            }

            if (request.GenerateCobolSkeleton)
            {
                var fn = $"{safePgm.ToLowerInvariant()}_transform_{ts}.cbl";
                string cobolContent;
                if (request.UseAiMode)
                {
                    var aiResult = await cobolAiTask!;
                    if (aiResult == null || string.IsNullOrWhiteSpace(aiResult.GeneratedCode))
                        throw new InvalidOperationException(
                            "AI service did not return a COBOL program. " +
                            "Please ensure the Python AI service is running, then try again. " +
                            "Alternatively, switch AI Mode off to use static generation.");
                    cobolContent = aiResult.GeneratedCode;
                    result.CobolAiGenerated = true;
                    _logger.LogInformation(
                        "AI COBOL program received. JobId={JobId} Confidence={Confidence}",
                        jobId, aiResult.Confidence);
                }
                else
                {
                    cobolContent = BuildCobolSkeleton(safeRecord, safeApp, safePgm, fields, valueMappings, mappingLookup);
                }
                await File.WriteAllTextAsync(Path.Combine(outputFolder, fn), cobolContent, Encoding.UTF8);
                result.CobolSkeletonFileName = fn;
                _logger.LogInformation(
                    "COBOL written. JobId={JobId} File={File} AiMode={AiMode}",
                    jobId, fn, request.UseAiMode);
            }

            if (request.GenerateJclSkeleton)
            {
                var fn = $"{safeJob.ToLowerInvariant()}_load_{ts}.jcl";
                string jclContent;
                if (request.UseAiMode)
                {
                    var aiResult = await jclAiTask!;
                    if (aiResult == null || string.IsNullOrWhiteSpace(aiResult.GeneratedCode))
                        throw new InvalidOperationException(
                            "AI service did not return JCL. " +
                            "Please ensure the Python AI service is running, then try again. " +
                            "Alternatively, switch AI Mode off to use static generation.");
                    jclContent = aiResult.GeneratedCode;
                    result.JclAiGenerated = true;
                    _logger.LogInformation(
                        "AI JCL received. JobId={JobId} Confidence={Confidence}",
                        jobId, aiResult.Confidence);
                }
                else
                {
                    jclContent = BuildJcl(safeJob, safePgm, safeRecord, position - 1);
                }
                await File.WriteAllTextAsync(Path.Combine(outputFolder, fn), jclContent, Encoding.UTF8);
                result.JclSkeletonFileName = fn;
                _logger.LogInformation(
                    "JCL written. JobId={JobId} File={File} AiMode={AiMode}",
                    jobId, fn, request.UseAiMode);
            }

            if (request.GenerateTechnicalSpec)
            {
                var fn = $"mainframe_asset_specification_{ts}.txt";
                await File.WriteAllTextAsync(
                    Path.Combine(outputFolder, fn),
                    BuildTechnicalSpec(safeRecord, safeApp, safePgm, fields, valueMappings, jobId),
                    Encoding.UTF8);
                result.TechnicalSpecFileName = fn;
                _logger.LogInformation("Technical specification written. JobId={JobId} File={File}", jobId, fn);
            }

            if (request.GenerateSampleRecords)
            {
                var fn = $"sample_target_records_{ts}.dat";
                await File.WriteAllTextAsync(
                    Path.Combine(outputFolder, fn),
                    BuildSampleRecords(fields, request.SampleRecordCount),
                    Encoding.UTF8);
                result.SampleRecordsFileName = fn;
                _logger.LogInformation("Sample records written. JobId={JobId} File={File} Count={Count}",
                    jobId, fn, request.SampleRecordCount);
            }

            sw.Stop();
            _logger.LogInformation(
                "Mainframe asset generation complete. JobId={JobId} Duration={Duration}ms " +
                "RecordLength={RecordLength} Fields={Fields}",
                jobId, sw.ElapsedMilliseconds, result.TotalRecordLength, fields.Count);

            return result;
        }

        public async Task<string?> ResolveAssetPathAsync(string jobId, string fileName)
        {
            var folder = await GetAssetFolderAsync(jobId);
            var safe = Path.GetFileName(fileName);
            var full = Path.GetFullPath(Path.Combine(folder, safe));
            return File.Exists(full) ? full : null;
        }

        // ─── Folder ───────────────────────────────────────────────────────────

        private async Task<string> GetAssetFolderAsync(string jobId)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            return Path.GetFullPath(Path.Combine(reportsPath, "mainframe-assets"));
        }

        // ─── AI Request Builder ───────────────────────────────────────────────

        private static MainframeAiAgentRequest BuildAiRequest(
            string jobId,
            string promptType,
            string programName,
            string recordName,
            string jobName,
            int totalRecordLength,
            IReadOnlyList<MainframeAssetFieldDto> fields,
            FinalMappingConfig? finalMapping,
            ValueMappingsDocument? valueMappings,
            List<Dictionary<string, object?>>? transformationRules = null)
        {
            var fieldDetails = fields.Select(f => new Dictionary<string, object?>
            {
                { "fieldName",      f.FieldName      },
                { "cobolFieldName", f.CobolFieldName  },
                { "dataType",       f.DataType        },
                { "startPosition",  f.StartPosition   },
                { "length",         f.Length          },
                { "pictureClause",  f.PictureClause   },
                { "sourceField",    f.SourceField     },
                { "isRequired",     f.IsRequired      },
                { "format",         f.Format          },
                { "rule",           f.Rule            },
            }).ToList();

            var fieldMappings = finalMapping?.Mappings.Select(m => new Dictionary<string, object?>
            {
                { "targetField",     m.TargetField   },
                { "sourceField",     m.SourceField   },
                { "sourceDataset",   m.SourceDataset },
                { "confidence",      m.Confidence    },
                { "transformations", m.Transformations
                    .Select(t => new Dictionary<string, object?>
                    {
                        { "operation",   t.Operation    },
                        { "rules",       t.Rules        },
                        { "format",      t.Format       },
                        { "fixedWidth",  t.FixedWidth   },
                        { "maskPattern", t.MaskPattern  },
                    })
                    .ToList<object?>() },
            }).ToList();

            // Distinct source datasets — tells the AI how many input files exist
            // and which source fields belong to each one.
            var sourceDatasets = finalMapping?.Mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.SourceDataset))
                .GroupBy(m => m.SourceDataset!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Dictionary<string, object?>
                {
                    { "datasetId",   g.Key },
                    { "fields",      g.Select(m => m.SourceField).Distinct().ToList() },
                    { "fieldCount",  g.Count() },
                })
                .ToList<Dictionary<string, object?>>();

            Dictionary<string, object?>? vmDict = null;
            if (valueMappings?.Fields?.Count > 0)
            {
                vmDict = valueMappings.Fields.ToDictionary(
                    f => f.TargetField,
                    f => (object?)f.Entries
                        .Where(e => e.IsEnabled)
                        .Select(e => new { e.SourceValue, e.TargetValue })
                        .ToList());
            }

            return new MainframeAiAgentRequest
            {
                JobId               = jobId,
                PromptType          = promptType,
                ProgramName         = programName,
                RecordName          = recordName,
                JobName             = jobName,
                TotalRecordLength   = totalRecordLength,
                FieldDetails        = fieldDetails,
                FieldMappings       = fieldMappings,
                ValueMappings       = vmDict,
                SourceDatasets      = sourceDatasets,
                TransformationRules = transformationRules,
                RequestId           = Guid.NewGuid().ToString(),
            };
        }

        // ─── COPYBOOK ─────────────────────────────────────────────────────────

        private static string BuildCopybook(
            string recordName,
            string appName,
            IReadOnlyList<MainframeAssetFieldDto> fields)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"      *================================================================");
            sb.AppendLine($"      * COPYBOOK  : {recordName.ToUpperInvariant()}");
            sb.AppendLine($"      * APPLICATION: {appName}");
            sb.AppendLine($"      * GENERATED : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"      * TOTAL RECORD LENGTH: {fields.Sum(f => f.Length ?? 0)}");
            sb.AppendLine($"      *================================================================");
            sb.AppendLine($"       01  {ToCobolName(recordName)}.");
            foreach (var f in fields)
            {
                var comment = string.IsNullOrWhiteSpace(f.Description) ? string.Empty
                    : $" *> {f.Description}";
                sb.AppendLine($"           05  {f.CobolFieldName,-34} {f.PictureClause}.{comment}");
            }
            return sb.ToString();
        }

        // ─── COBOL SKELETON ───────────────────────────────────────────────────

        private static string BuildCobolSkeleton(
            string recordName,
            string appName,
            string programName,
            IReadOnlyList<MainframeAssetFieldDto> fields,
            ValueMappingsDocument? valueMappings,
            Dictionary<string, FinalMapping>? mappingLookup = null)
        {
            var cobolRecord = ToCobolName(recordName);
            var cobolInput = $"INPUT-{cobolRecord}";
            var sb = new StringBuilder();

            sb.AppendLine("      *================================================================");
            sb.AppendLine($"      * PROGRAM    : {programName.ToUpperInvariant()}");
            sb.AppendLine($"      * APPLICATION: {appName}");
            sb.AppendLine($"      * PURPOSE    : Transform source data to {recordName} target layout");
            sb.AppendLine($"      * GENERATED  : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("      *");
            sb.AppendLine("      * *** AI-GENERATED SKELETON — DEVELOPER REVIEW REQUIRED ***");
            sb.AppendLine("      * *** NOT PRODUCTION READY — VERIFY ALL LOGIC BEFORE USE ***");
            sb.AppendLine("      *================================================================");

            // IDENTIFICATION
            sb.AppendLine($"       IDENTIFICATION DIVISION.");
            sb.AppendLine($"       PROGRAM-ID. {programName.ToUpperInvariant()}.");
            sb.AppendLine($"       AUTHOR. MAINFRAME-ASSET-GENERATION-AGENT.");
            sb.AppendLine($"       DATE-WRITTEN. {DateTime.UtcNow:yyyy-MM-dd}.");
            sb.AppendLine();

            // ENVIRONMENT
            sb.AppendLine("       ENVIRONMENT DIVISION.");
            sb.AppendLine("       CONFIGURATION SECTION.");
            sb.AppendLine("       INPUT-OUTPUT SECTION.");
            sb.AppendLine("       FILE-CONTROL.");
            sb.AppendLine($"           SELECT INPUT-FILE  ASSIGN TO '{cobolInput}'");
            sb.AppendLine("               ORGANIZATION IS SEQUENTIAL");
            sb.AppendLine("               ACCESS MODE  IS SEQUENTIAL");
            sb.AppendLine("               FILE STATUS  IS WS-INPUT-STATUS.");
            sb.AppendLine($"           SELECT OUTPUT-FILE ASSIGN TO '{cobolRecord}'");
            sb.AppendLine("               ORGANIZATION IS SEQUENTIAL");
            sb.AppendLine("               ACCESS MODE  IS SEQUENTIAL");
            sb.AppendLine("               FILE STATUS  IS WS-OUTPUT-STATUS.");
            sb.AppendLine();

            // DATA DIVISION
            sb.AppendLine("       DATA DIVISION.");
            sb.AppendLine("       FILE SECTION.");
            sb.AppendLine("       FD  INPUT-FILE.");
            sb.AppendLine($"       01  INPUT-RECORD     PIC X({fields.Sum(f => f.Length ?? 0)}).");
            sb.AppendLine("       FD  OUTPUT-FILE.");
            sb.AppendLine($"       01  OUTPUT-RECORD    PIC X({fields.Sum(f => f.Length ?? 0)}).");
            sb.AppendLine();

            // WORKING-STORAGE
            sb.AppendLine("       WORKING-STORAGE SECTION.");
            sb.AppendLine("       01  WS-EOF-FLAG          PIC X(1) VALUE 'N'.");
            sb.AppendLine("           88  WS-EOF           VALUE 'Y'.");
            sb.AppendLine("       01  WS-RECORD-COUNT      PIC 9(9) VALUE ZERO.");
            sb.AppendLine("       01  WS-ERROR-COUNT       PIC 9(9) VALUE ZERO.");
            sb.AppendLine("       01  WS-ERROR-FLAG        PIC X(1) VALUE 'N'.");
            sb.AppendLine("           88  WS-ERROR         VALUE 'Y'.");
            sb.AppendLine("       01  WS-INPUT-STATUS      PIC X(2) VALUE '00'.");
            sb.AppendLine("           88  WS-INPUT-OK      VALUE '00'.");
            sb.AppendLine("       01  WS-OUTPUT-STATUS     PIC X(2) VALUE '00'.");
            sb.AppendLine("           88  WS-OUTPUT-OK     VALUE '00'.");
            sb.AppendLine();
            sb.AppendLine($"       01  {cobolRecord}.");
            foreach (var f in fields)
                sb.AppendLine($"           05  {f.CobolFieldName,-34} {f.PictureClause}.");
            sb.AppendLine();
            sb.AppendLine("      *--- Source input fields (developer maps to INPUT-RECORD) ---");
            foreach (var f in fields
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceField))
                .GroupBy(x => x.SourceField, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()))
            {
                var srcPic = BuildSourcePic(f);
                sb.AppendLine($"       01  WS-SRC-{ToCobolName(f.SourceField!)}.");
                sb.AppendLine($"           05  WS-{ToCobolName(f.SourceField!)} {srcPic}.");
            }
            sb.AppendLine();
            sb.AppendLine("      *--- Work areas for transformation operations ---");
            sb.AppendLine("       01  WS-TEMP-FIELD        PIC X(256) VALUE SPACES.");
            sb.AppendLine("       01  WS-NUMERIC-TEMP      PIC S9(15)V9(6) COMP-3.");
            sb.AppendLine("       01  WS-STR-TEMP          PIC X(50)  VALUE SPACES.");
            sb.AppendLine();

            // PROCEDURE DIVISION
            sb.AppendLine("       PROCEDURE DIVISION.");
            sb.AppendLine();
            sb.AppendLine("       0000-MAIN.");
            sb.AppendLine("           PERFORM 1000-INIT");
            sb.AppendLine("           PERFORM 2000-PROCESS UNTIL WS-EOF");
            sb.AppendLine("           PERFORM 9000-TERMINATE");
            sb.AppendLine("           STOP RUN.");
            sb.AppendLine();
            sb.AppendLine("       1000-INIT.");
            sb.AppendLine("           OPEN INPUT  INPUT-FILE");
            sb.AppendLine("                OUTPUT OUTPUT-FILE");
            sb.AppendLine("           READ INPUT-FILE");
            sb.AppendLine("               AT END SET WS-EOF TO TRUE");
            sb.AppendLine("           END-READ.");
            sb.AppendLine();
            sb.AppendLine("       2000-PROCESS.");
            sb.AppendLine("           ADD 1 TO WS-RECORD-COUNT");
            sb.AppendLine("           PERFORM 3000-TRANSFORM");
            sb.AppendLine($"           WRITE OUTPUT-RECORD FROM {cobolRecord}");
            sb.AppendLine("           READ INPUT-FILE");
            sb.AppendLine("               AT END SET WS-EOF TO TRUE");
            sb.AppendLine("           END-READ.");
            sb.AppendLine();
            sb.AppendLine("       3000-TRANSFORM.");

            // ── INPUT-RECORD parsing ──────────────────────────────────────────
            // Build unique source field list in declaration order, assign
            // sequential fixed-width positions from their estimated lengths.
            var uniqueSrcFields = fields
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceField))
                .GroupBy(x => x.SourceField, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (uniqueSrcFields.Count > 0)
            {
                // Compute sequential source positions
                int srcPos = 1;
                var srcLayout = uniqueSrcFields.Select(f =>
                {
                    var len = f.Length ?? (IsNumericPic(f.PictureClause) ? 10 : 30);
                    var entry = (pos: srcPos, len, src: f.SourceField!, ws: ToCobolName(f.SourceField!));
                    srcPos += len;
                    return entry;
                }).ToList();

                sb.AppendLine("      *--- PARSE INPUT-RECORD INTO SOURCE FIELDS ---");
                sb.AppendLine("      *    !!! DEVELOPER ACTION REQUIRED !!!         ");
                sb.AppendLine("      *    Positions below are ESTIMATED from target  ");
                sb.AppendLine("      *    field lengths. Verify against your actual  ");
                sb.AppendLine("      *    source fixed-width layout before compiling.");
                sb.AppendLine("      *    If source is delimited (CSV/TSV), replace  ");
                sb.AppendLine("      *    reference modification with UNSTRING logic. ");
                sb.AppendLine("      *    Pos   Len  Source Field           WS Field");
                sb.AppendLine("      *    " + new string('-', 56));
                foreach (var (pos, len, src, ws) in srcLayout)
                    sb.AppendLine($"      *    {pos,-6}{len,-5}{src,-23}WS-{ws}");
                sb.AppendLine($"      *    Total estimated source record length: {srcPos - 1}");
                sb.AppendLine();
                foreach (var (pos, len, src, ws) in srcLayout)
                    sb.AppendLine($"           MOVE INPUT-RECORD({pos}:{len})  TO WS-{ws}.");
                sb.AppendLine();
            }

            sb.AppendLine("      *--- FIELD MAPPING AND TRANSFORMATION ---");

            foreach (var f in fields)
            {
                var mapping = mappingLookup?.GetValueOrDefault(f.FieldName);
                var tr = mapping?.Transformations?.FirstOrDefault();
                var op = (tr?.Operation ?? "DIRECT").ToUpperInvariant();
                var aiP = tr?.AIParameters;
                var src = string.IsNullOrWhiteSpace(f.SourceField) ? null : ToCobolName(f.SourceField!);

                sb.AppendLine($"      *    Target: {f.CobolFieldName,-30} Source: {f.SourceField ?? "(none)"}  Op: {tr?.Operation ?? "DIRECT"}");
                if (!string.IsNullOrWhiteSpace(f.Rule))
                    sb.AppendLine($"      *    Rule  : {f.Rule}");
                if (!string.IsNullOrWhiteSpace(f.Format))
                    sb.AppendLine($"      *    Format: {f.Format}");

                if (src == null)
                {
                    if (op == "DEFAULT_VALUE" && (tr?.DefaultValue ?? AiParam(aiP, "value")) is { } dv0)
                        sb.AppendLine($"           MOVE '{CobolLit(dv0)}' TO {f.CobolFieldName}.");
                    else
                    {
                        sb.AppendLine($"           MOVE SPACES TO {f.CobolFieldName}.");
                        sb.AppendLine($"      *    TODO: Provide source for {f.CobolFieldName}");
                    }
                }
                else
                {
                    switch (op)
                    {
                        case "DATE_FORMAT":
                            var dfOut = AiParam(aiP, "outputFormat", "output_format") ?? tr?.Format ?? "yyyyMMdd";
                            sb.AppendLine($"      *    Date → {dfOut}  (verify CONVERT-DATE-TIME args for your runtime)");
                            sb.AppendLine($"           MOVE FUNCTION CONVERT-DATE-TIME(WS-{src}, 'I-YMD', 'O-YMD')");
                            sb.AppendLine($"               TO {f.CobolFieldName}.");
                            break;

                        case "TRUNCATE":
                            var trMax = AiParam(aiP, "maxLength", "max_length") ?? "?";
                            sb.AppendLine($"      *    Truncate to {trMax} chars");
                            if (int.TryParse(trMax, out var trLen) && trLen > 0)
                                sb.AppendLine($"           MOVE WS-{src}(1:{trLen}) TO {f.CobolFieldName}.");
                            else
                            {
                                sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                                sb.AppendLine($"      *    TODO: Replace with reference modification WS-{src}(1:maxLength)");
                            }
                            break;

                        case "PAD_LEFT":
                            var plChar = AiParam(aiP, "padChar", "pad_character") ?? "0";
                            var plLen = AiParam(aiP, "totalLength", "total_length", "width") ?? "?";
                            sb.AppendLine($"      *    Pad left with '{plChar}' to total length {plLen}");
                            sb.AppendLine($"           MOVE SPACES TO WS-TEMP-FIELD.");
                            sb.AppendLine($"           STRING WS-{src} DELIMITED SIZE INTO WS-TEMP-FIELD.");
                            sb.AppendLine($"           MOVE FUNCTION REVERSE(FUNCTION TRIM(");
                            sb.AppendLine($"               FUNCTION REVERSE(WS-TEMP-FIELD), LEADING))");
                            sb.AppendLine($"               TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: Verify padding logic against your COBOL runtime version");
                            break;

                        case "PAD_RIGHT":
                            var prChar = AiParam(aiP, "padChar", "pad_character") ?? " ";
                            var prLen = AiParam(aiP, "totalLength", "total_length", "width") ?? "?";
                            sb.AppendLine($"      *    Pad right with '{prChar}' to total length {prLen}");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            break;

                        case "UPPERCASE":
                            sb.AppendLine($"           MOVE FUNCTION UPPER-CASE(WS-{src})");
                            sb.AppendLine($"               TO {f.CobolFieldName}.");
                            break;

                        case "LOWERCASE":
                            sb.AppendLine($"           MOVE FUNCTION LOWER-CASE(WS-{src})");
                            sb.AppendLine($"               TO {f.CobolFieldName}.");
                            break;

                        case "PROPERCASE":
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: PROPERCASE — no native COBOL function; use INSPECT or custom sub-program");
                            break;

                        case "MASK":
                            var msVS = AiParam(aiP, "visibleStart") ?? "2";
                            var msVE = AiParam(aiP, "visibleEnd") ?? "2";
                            var msMC = AiParam(aiP, "maskChar") ?? "*";
                            sb.AppendLine($"      *    Mask: keep first {msVS}, last {msVE} chars; rest replaced with '{msMC}'");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"           INSPECT {f.CobolFieldName}");
                            sb.AppendLine($"               CONVERTING 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'");
                            sb.AppendLine($"                       TO '{new string(msMC[0], 36)}'.");
                            sb.AppendLine($"      *    TODO: Preserve first {msVS} and last {msVE} chars with reference modification");
                            break;

                        case "CONCAT":
                            var concatFlds = AiParam(aiP, "fields") ?? src;
                            var concatParts = (concatFlds ?? src!).Split(',',
                                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            sb.AppendLine($"      *    Concatenate: {concatFlds}");
                            sb.AppendLine($"           INITIALIZE WS-TEMP-FIELD.");
                            sb.AppendLine($"           STRING");
                            foreach (var cp in concatParts)
                                sb.AppendLine($"               WS-{ToCobolName(cp)} DELIMITED SPACE");
                            sb.AppendLine($"               INTO {f.CobolFieldName}");
                            sb.AppendLine($"           END-STRING.");
                            break;

                        case "SPLIT":
                            var spDelim = AiParam(aiP, "delimiter") ?? "/";
                            var spIdx = AiParam(aiP, "index") ?? "0";
                            sb.AppendLine($"      *    Split by '{spDelim}', take part index {spIdx}");
                            sb.AppendLine($"           UNSTRING WS-{src} DELIMITED BY '{spDelim}'");
                            sb.AppendLine($"               INTO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: Add additional INTO targets if multiple parts needed");
                            break;

                        case "DEFAULT_VALUE":
                            var dvVal = CobolLit(tr?.DefaultValue ?? AiParam(aiP, "value") ?? string.Empty);
                            sb.AppendLine($"      *    Default: '{dvVal}' when source is spaces");
                            sb.AppendLine($"           IF WS-{src} = SPACES");
                            sb.AppendLine($"               MOVE '{dvVal}' TO {f.CobolFieldName}");
                            sb.AppendLine($"           ELSE");
                            sb.AppendLine($"               MOVE WS-{src} TO {f.CobolFieldName}");
                            sb.AppendLine($"           END-IF.");
                            break;

                        case "NULL_REPLACEMENT":
                            var nrVal = CobolLit(AiParam(aiP, "value") ?? "0");
                            sb.AppendLine($"      *    Null replacement: use '{nrVal}' when source is spaces");
                            sb.AppendLine($"           IF WS-{src} = SPACES");
                            sb.AppendLine($"               MOVE '{nrVal}' TO {f.CobolFieldName}");
                            sb.AppendLine($"           ELSE");
                            sb.AppendLine($"               MOVE WS-{src} TO {f.CobolFieldName}");
                            sb.AppendLine($"           END-IF.");
                            break;

                        case "DECIMAL_FORMAT":
                            var dfDp = AiParam(aiP, "decimalPlaces", "decimal_places") ?? "2";
                            sb.AppendLine($"      *    Decimal format: {dfDp} decimal places");
                            sb.AppendLine($"           MOVE FUNCTION NUMVAL(WS-{src}) TO WS-NUMERIC-TEMP.");
                            sb.AppendLine($"           MOVE WS-NUMERIC-TEMP TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: Ensure {f.CobolFieldName} PICTURE has V9({dfDp}) for decimal alignment");
                            break;

                        case "CURRENCY_NORMALIZATION":
                            var cnDp = AiParam(aiP, "decimalPlaces") ?? "2";
                            sb.AppendLine($"      *    Currency normalization ({cnDp} decimal places)");
                            sb.AppendLine($"           MOVE FUNCTION NUMVAL-C(WS-{src}) TO WS-NUMERIC-TEMP.");
                            sb.AppendLine($"           MOVE WS-NUMERIC-TEMP TO {f.CobolFieldName}.");
                            break;

                        case "STRING_TO_NUMERIC":
                            sb.AppendLine($"      *    String to numeric");
                            sb.AppendLine($"           MOVE FUNCTION NUMVAL(WS-{src}) TO {f.CobolFieldName}.");
                            break;

                        case "NUMERIC_TO_STRING":
                            sb.AppendLine($"      *    Numeric to string");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            break;

                        case "REMOVE_SPECIAL_CHARACTERS":
                            sb.AppendLine($"      *    Remove special characters");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"           INSPECT {f.CobolFieldName}");
                            sb.AppendLine($"               CONVERTING '!@#$%^&*()-+=[]|;:,.<>?' TO SPACES.");
                            break;

                        case "BOOLEAN_MAPPING":
                            var bmTrue = CobolLit(AiParam(aiP, "trueOutput") ?? "1");
                            var bmFalse = CobolLit(AiParam(aiP, "falseOutput") ?? "0");
                            sb.AppendLine($"      *    Boolean: true → '{bmTrue}', false → '{bmFalse}'");
                            sb.AppendLine($"           EVALUATE WS-{src}");
                            sb.AppendLine($"               WHEN 'Y' WHEN 'YES' WHEN 'TRUE' WHEN '1'");
                            sb.AppendLine($"                   MOVE '{bmTrue}' TO {f.CobolFieldName}");
                            sb.AppendLine($"               WHEN OTHER");
                            sb.AppendLine($"                   MOVE '{bmFalse}' TO {f.CobolFieldName}");
                            sb.AppendLine($"           END-EVALUATE.");
                            break;

                        case "FIXED_WIDTH_FORMAT":
                            var fwW = AiParam(aiP, "width") ?? tr?.FixedWidth?.ToString() ?? "?";
                            var fwA = AiParam(aiP, "alignment") ?? "LEFT";
                            sb.AppendLine($"      *    Fixed-width {fwW} chars, {fwA}-aligned");
                            sb.AppendLine($"           MOVE SPACES TO {f.CobolFieldName}.");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: Verify alignment — use reference modification for RIGHT alignment");
                            break;

                        case "VALUE_MAPPING":
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    Value mapping applied in EVALUATE section below");
                            break;

                        case "JULIAN_TO_DATE":
                            var jOutFmt = AiParam(aiP, "outputFormat") ?? "yyyyMMdd";
                            sb.AppendLine($"      *    Julian YYDDD/YYYYDDD → Gregorian ({jOutFmt})");
                            sb.AppendLine($"           PERFORM 3200-JULIAN-CONV-{f.CobolFieldName[..Math.Min(14, f.CobolFieldName.Length)]}.");
                            sb.AppendLine($"      *    TODO: Code Julian-to-Gregorian paragraph using date arithmetic");
                            break;

                        case "DATE_TO_JULIAN":
                            sb.AppendLine($"      *    Gregorian → Julian YYYYDDD");
                            sb.AppendLine($"           PERFORM 3200-TO-JULIAN-{f.CobolFieldName[..Math.Min(14, f.CobolFieldName.Length)]}.");
                            sb.AppendLine($"      *    TODO: Code Gregorian-to-Julian paragraph using COMPUTE with day-of-year");
                            break;

                        case "UNPACK_COMP3":
                            var c3Dp = AiParam(aiP, "impliedDecimalPlaces") ?? "0";
                            sb.AppendLine($"      *    COMP-3 packed decimal decode ({c3Dp} implied decimal places)");
                            sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                            sb.AppendLine($"      *    TODO: Apply UNPACK via custom sub-program or use DISPLAY on COMP-3 field directly");
                            break;

                        case "DECIMAL_SHIFT":
                            var dsPlaces = AiParam(aiP, "impliedDecimalPlaces", "decimalPlaces") ?? "2";
                            sb.AppendLine($"      *    Implied decimal: divide by 10^{dsPlaces} (e.g. 12345 → 123.45)");
                            sb.AppendLine($"           COMPUTE {f.CobolFieldName} = WS-{src} / {Math.Pow(10, int.TryParse(dsPlaces, out var dsp) ? dsp : 2):F0}.");
                            break;

                        case "CONDITIONAL_VALUE":
                            var cvCond = CobolLit(AiParam(aiP, "condition") ?? "?");
                            var cvTrue = CobolLit(AiParam(aiP, "trueValue") ?? "Y");
                            var cvFalse = CobolLit(AiParam(aiP, "falseValue") ?? "N");
                            sb.AppendLine($"      *    Conditional: if = '{cvCond}' then '{cvTrue}' else '{cvFalse}'");
                            sb.AppendLine($"           EVALUATE WS-{src}");
                            sb.AppendLine($"               WHEN '{cvCond}'");
                            sb.AppendLine($"                   MOVE '{cvTrue}' TO {f.CobolFieldName}");
                            sb.AppendLine($"               WHEN OTHER");
                            sb.AppendLine($"                   MOVE '{cvFalse}' TO {f.CobolFieldName}");
                            sb.AppendLine($"           END-EVALUATE.");
                            break;

                        case "SUBSTRING":
                            var ssStart = AiParam(aiP, "start") ?? "1";
                            var ssLen = AiParam(aiP, "length") ?? "?";
                            sb.AppendLine($"      *    Substring: start={ssStart}, length={ssLen}");
                            if (int.TryParse(ssStart, out var ssS) && int.TryParse(ssLen, out var ssL))
                                sb.AppendLine($"           MOVE WS-{src}({ssS}:{ssL}) TO {f.CobolFieldName}.");
                            else
                            {
                                sb.AppendLine($"           MOVE WS-{src} TO {f.CobolFieldName}.");
                                sb.AppendLine($"      *    TODO: Replace with reference modification WS-{src}(start:length)");
                            }
                            break;

                        default:
                            sb.AppendLine($"           MOVE WS-{src}");
                            sb.AppendLine($"               TO {f.CobolFieldName}.");
                            break;
                    }
                }
                sb.AppendLine();
            }

            // Value mapping EVALUATE blocks (replaces per-entry IF chains)
            if (valueMappings?.Fields?.Count > 0)
            {
                sb.AppendLine("      *--- VALUE MAPPING RULES (generated from value_mappings.json) ---");
                foreach (var vmField in valueMappings.Fields)
                {
                    var targetCobol = ToCobolName(vmField.TargetField);
                    var sourceCobol = !string.IsNullOrWhiteSpace(vmField.SourceField)
                        ? $"WS-{ToCobolName(vmField.SourceField)}" : "(unknown-source)";
                    var enabled = vmField.Entries.Where(e => e.IsEnabled).ToList();
                    if (enabled.Count == 0) continue;

                    sb.AppendLine($"           EVALUATE {sourceCobol}");
                    foreach (var entry in enabled)
                    {
                        sb.AppendLine($"               WHEN '{CobolLit(entry.SourceValue)}'");
                        sb.AppendLine($"                   MOVE '{CobolLit(entry.TargetValue)}' TO {targetCobol}");
                    }
                    sb.AppendLine($"               WHEN OTHER");
                    sb.AppendLine($"                   CONTINUE");
                    sb.AppendLine($"           END-EVALUATE.");
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("       9000-TERMINATE.");
            sb.AppendLine("           CLOSE INPUT-FILE OUTPUT-FILE.");
            sb.AppendLine($"           DISPLAY 'PROGRAM {programName.ToUpperInvariant()} COMPLETE - RECORDS: '");
            sb.AppendLine("               WS-RECORD-COUNT.");
            sb.AppendLine();
            sb.AppendLine($"       END PROGRAM {programName.ToUpperInvariant()}.");

            return sb.ToString();
        }

        // ─── JCL SKELETON ─────────────────────────────────────────────────────

        private static string BuildJcl(string jobName, string programName, string recordName, int recordLength)
        {
            // JCL jobname: max 8 chars. pgm: max 8 chars.
            var jn  = jobName.ToUpperInvariant().Length > 8
                ? jobName.ToUpperInvariant()[..8]  : jobName.ToUpperInvariant();
            var pgm = programName.ToUpperInvariant().Length > 8
                ? programName.ToUpperInvariant()[..8] : programName.ToUpperInvariant();
            // Step name: max 8 chars — use first 6 chars of jn + "00"
            var stepName = (jn.Length > 6 ? jn[..6] : jn) + "00";

            var sb = new StringBuilder();
            sb.AppendLine("//*================================================================");
            sb.AppendLine($"//* JCL       : {jn}");
            sb.AppendLine($"//* PROGRAM   : {pgm}");
            sb.AppendLine($"//* RECORD    : {recordName.ToUpperInvariant()}");
            sb.AppendLine($"//* LRECL     : {recordLength}");
            sb.AppendLine($"//* GENERATED : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("//*");
            sb.AppendLine("//* *** SKELETON ONLY — DEVELOPER REVIEW REQUIRED ***");
            sb.AppendLine("//* *** UPDATE DSNAMES, CLASSES, ACCOUNTING BEFORE USE ***");
            sb.AppendLine("//*================================================================");
            // Correct JCL format: //jobname<sp>JOB<sp>parameters  (jobname max 8 chars)
            sb.AppendLine($"//{jn} JOB (ACCT,PROJ),'MAINFRAME LOAD',");
            sb.AppendLine("//          CLASS=A,MSGCLASS=X,MSGLEVEL=(1,1),");
            sb.AppendLine("//          NOTIFY=&SYSUID");
            sb.AppendLine("//*");
            sb.AppendLine("//JOBLIB   DD DISP=SHR,DSN=YOUR.LOAD.LIBRARY");
            sb.AppendLine("//*");
            sb.AppendLine($"//{stepName} EXEC PGM={pgm}");
            sb.AppendLine($"//* STEP: Run {pgm} transformation program");
            sb.AppendLine("//*");
            sb.AppendLine("//SYSOUT   DD SYSOUT=*");
            sb.AppendLine("//SYSPRINT DD SYSOUT=*");
            sb.AppendLine("//*");
            sb.AppendLine("//INPUTDD  DD DISP=SHR,");
            sb.AppendLine("//            DSN=YOUR.INPUT.DATASET,");
            sb.AppendLine($"//            RECFM=FB,LRECL={recordLength},BLKSIZE=0");
            sb.AppendLine("//*");
            sb.AppendLine("//OUTPUTDD DD DISP=(NEW,CATLG,DELETE),");
            sb.AppendLine("//            DSN=YOUR.OUTPUT.DATASET,");
            sb.AppendLine($"//            RECFM=FB,LRECL={recordLength},BLKSIZE=0,");
            sb.AppendLine("//            SPACE=(TRK,(10,5),RLSE)");
            sb.AppendLine("//*");
            sb.AppendLine("//* NOTE: Copybook is a compile-time COPY member — no runtime DD required.");
            sb.AppendLine($"//*       Ensure {ToCobolName(recordName)[..Math.Min(8, ToCobolName(recordName).Length)]} exists in your SYSLIB concatenation at compile time.");
            sb.AppendLine("//*");
            sb.AppendLine("//SYSIN    DD *");
            sb.AppendLine("/*");

            return sb.ToString();
        }

        // ─── TECHNICAL SPECIFICATION ──────────────────────────────────────────

        private static string BuildTechnicalSpec(
            string recordName,
            string appName,
            string programName,
            IReadOnlyList<MainframeAssetFieldDto> fields,
            ValueMappingsDocument? valueMappings,
            string jobId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================================================");
            sb.AppendLine("  MAINFRAME TECHNICAL SPECIFICATION");
            sb.AppendLine("========================================================================");
            sb.AppendLine($"  RECORD NAME  : {recordName.ToUpperInvariant()}");
            sb.AppendLine($"  APPLICATION  : {appName}");
            sb.AppendLine($"  PROGRAM      : {programName.ToUpperInvariant()}");
            sb.AppendLine($"  JOB ID       : {jobId}");
            sb.AppendLine($"  GENERATED    : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"  TOTAL LENGTH : {fields.Sum(f => f.Length ?? 0)} bytes");
            sb.AppendLine($"  FIELD COUNT  : {fields.Count}");
            sb.AppendLine("========================================================================");
            sb.AppendLine();

            // ── Field Layout Table
            sb.AppendLine("------------------------------------------------------------------------");
            sb.AppendLine("  FIELD LAYOUT");
            sb.AppendLine("------------------------------------------------------------------------");
            sb.AppendLine($"  {"#",4}  {"Field Name",-28} {"COBOL Name",-30} {"Type",-10} {"Len",5} {"Pos",5}  {"PIC Clause",-20}  {"Src Field",-24}  Req");
            sb.AppendLine("  " + new string('-', 140));
            foreach (var f in fields)
            {
                sb.AppendLine(
                    $"  {f.ColumnOrder + 1,4}  {f.FieldName,-28} {f.CobolFieldName,-30} {f.DataType,-10} " +
                    $"{f.Length?.ToString() ?? "-",5} {f.StartPosition,5}  {f.PictureClause,-20}  " +
                    $"{(f.SourceField ?? "-"),-24}  {(f.IsRequired ? "Y" : "N")}");
            }
            sb.AppendLine();

            // ── Transformation Rules
            var fieldsWithRules = fields.Where(f =>
                !string.IsNullOrWhiteSpace(f.Rule) || !string.IsNullOrWhiteSpace(f.Format)).ToList();
            if (fieldsWithRules.Any())
            {
                sb.AppendLine("------------------------------------------------------------------------");
                sb.AppendLine("  TRANSFORMATION RULES");
                sb.AppendLine("------------------------------------------------------------------------");
                foreach (var f in fieldsWithRules)
                {
                    sb.AppendLine($"  Field : {f.FieldName}");
                    if (!string.IsNullOrWhiteSpace(f.Rule))
                        sb.AppendLine($"    Rule   : {f.Rule}");
                    if (!string.IsNullOrWhiteSpace(f.Format))
                        sb.AppendLine($"    Format : {f.Format}");
                    sb.AppendLine();
                }
            }

            // ── Value Mapping Rules
            if (valueMappings?.Fields?.Count > 0)
            {
                sb.AppendLine("------------------------------------------------------------------------");
                sb.AppendLine("  VALUE MAPPING RULES");
                sb.AppendLine("------------------------------------------------------------------------");
                foreach (var vmField in valueMappings.Fields)
                {
                    sb.AppendLine($"  Target Field : {vmField.TargetField}");
                    sb.AppendLine($"  Source Field : {vmField.SourceField ?? "(unknown)"}");
                    sb.AppendLine($"  {"Source Value",-24}  {"Target Value",-24}  Enabled  Notes");
                    sb.AppendLine("  " + new string('-', 80));
                    foreach (var e in vmField.Entries)
                        sb.AppendLine($"  {e.SourceValue,-24}  {e.TargetValue,-24}  {(e.IsEnabled ? "Y" : "N"),-7}  {e.Notes ?? ""}");
                    sb.AppendLine();
                }
            }

            // ── Sample Records note
            sb.AppendLine("------------------------------------------------------------------------");
            sb.AppendLine("  SAMPLE TARGET RECORDS");
            sb.AppendLine("------------------------------------------------------------------------");
            sb.AppendLine("  See companion sample_target_records_*.dat file for sample output.");
            sb.AppendLine();
            sb.AppendLine("========================================================================");
            sb.AppendLine("  END OF SPECIFICATION");
            sb.AppendLine("========================================================================");

            return sb.ToString();
        }

        // ─── SAMPLE TARGET RECORDS ────────────────────────────────────────────

        private static string BuildSampleRecords(
            IReadOnlyList<MainframeAssetFieldDto> fields,
            int count)
        {
            var sb = new StringBuilder();
            int total = fields.Sum(f => f.Length ?? 0);
            var rng = new Random(42);

            sb.AppendLine($"*--- SAMPLE TARGET RECORDS — Record Length {total} ----------------");
            sb.AppendLine($"*--- Count: {count} — Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ----");

            for (int i = 1; i <= count; i++)
            {
                var rec = new StringBuilder();
                foreach (var f in fields)
                {
                    var len = f.Length ?? 10;
                    var value = GenerateSampleValue(f, i, rng);
                    // Pad or truncate to exact field length
                    rec.Append(IsNumericPic(f.PictureClause)
                        ? value.PadLeft(len, '0')[..Math.Min(value.Length, len)]
                        : value.PadRight(len)[..len]);
                }
                sb.AppendLine(rec.ToString());
            }
            return sb.ToString();
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string BuildPic(TargetFieldMetadata field)
        {
            var fn = field.FieldName.ToUpperInvariant();
            var len = field.FieldLength ?? InferDefaultLength(field);

            if (IsIdentifierLike(fn)) return $"PIC X({len})";
            if (IsBoolean(field.Datatype)) return "PIC X(1)";
            if (IsDate(field.Datatype) || IsDateFormat(field.Format)) return "PIC X(8)";
            if (IsInteger(field.Datatype)) return $"PIC 9({len})";
            if (IsDecimal(field.Datatype)) return $"PIC 9({Math.Max(len - 2, 1)})V99";
            return $"PIC X({len})";
        }

        private static string BuildSourcePic(MainframeAssetFieldDto f)
        {
            if (IsNumericPic(f.PictureClause)) return $"PIC 9({f.Length ?? 10})";
            return $"PIC X({f.Length ?? 30})";
        }

        private static string GenerateSampleValue(MainframeAssetFieldDto f, int row, Random rng)
        {
            var fn = f.FieldName.ToUpperInvariant();
            if (IsIdentifierLike(fn)) return $"ID{row:D8}";
            if (IsBoolean(f.DataType)) return row % 2 == 0 ? "Y" : "N";
            if (IsDate(f.DataType)) return DateTime.UtcNow.AddDays(-rng.Next(0, 3650)).ToString("yyyyMMdd");
            if (IsDecimal(f.DataType))
            {
                var v = (rng.NextDouble() * 9999.99);
                return $"{(int)v:D10}{((int)(v * 100 % 100)):D2}";
            }
            if (IsInteger(f.DataType)) return rng.Next(1, 999999).ToString();
            return $"SAMPLE{row:D3}";
        }

        private static bool IsNumericPic(string pic) =>
            pic.Contains("PIC 9") || pic.Contains("PIC S9") || pic.Contains("V");

        private static int InferDefaultLength(TargetFieldMetadata f)
        {
            if (IsDate(f.Datatype) || IsDateFormat(f.Format)) return 8;
            if (IsBoolean(f.Datatype)) return 1;
            if (IsInteger(f.Datatype)) return 10;
            if (IsDecimal(f.Datatype)) return 12;
            return 30;
        }

        private static bool IsIdentifierLike(string fn) =>
            new[] { "ACCT", "ACCOUNT", "KONTO", "ID", "NO", "NUM", "CODE", "REF", "SEQ", "KEY" }
            .Any(t => fn.Contains(t, StringComparison.OrdinalIgnoreCase));

        private static bool IsBoolean(string dt) =>
            dt.Contains("BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            dt.Contains("BOOL", StringComparison.OrdinalIgnoreCase);

        private static bool IsDate(string dt) =>
            dt.Contains("DATE", StringComparison.OrdinalIgnoreCase) ||
            dt.Contains("TIME", StringComparison.OrdinalIgnoreCase);

        private static bool IsDateFormat(string? fmt) =>
            !string.IsNullOrWhiteSpace(fmt) &&
            Regex.IsMatch(fmt, "(YYYY|yyyy|YY|yy).*(MM|mm)|(?:MM|mm).*(YYYY|yyyy|YY|yy)", RegexOptions.IgnoreCase);

        private static bool IsInteger(string dt) =>
            dt.Contains("INTEGER", StringComparison.OrdinalIgnoreCase) ||
            dt.Contains("INT", StringComparison.OrdinalIgnoreCase) ||
            dt.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase);

        // NUMERIC is treated as integer (whole-number), not decimal — use DECIMAL explicitly for V99.
        private static bool IsDecimal(string dt) =>
            dt.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase);

        // GnuCOBOL / IBM-dialect reserved words that can appear as business field names.
        // Any name that collides gets a -FLD suffix to avoid compile errors.
        private static readonly HashSet<string> _cobolReservedWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "STATUS", "DATE", "TYPE", "CODE", "VALUE", "ADDRESS", "COUNT", "LENGTH",
            "ERROR", "FILE", "RECORD", "FUNCTION", "NUMBER", "TIME", "DAY", "YEAR",
            "LINE", "COLUMN", "SIZE", "LABEL", "SECTION", "DIVISION", "AREA",
            "BLOCK", "OCCURS", "DEPENDING", "INDEXED", "ASCENDING", "DESCENDING",
            "KEY", "USAGE", "DISPLAY", "BINARY", "SIGN", "LEADING", "TRAILING",
            "SEPARATE", "MOVE", "SET", "RETURN", "END", "WHEN", "ALL", "NOT",
            "AND", "OR", "IF", "ELSE", "TRUE", "FALSE", "SPACE", "SPACES",
            "ZERO", "ZEROS", "ZEROES", "ON", "OFF", "STOP", "RUN", "GO", "TO",
            "BY", "PERFORM", "UNTIL", "VARYING", "AFTER", "BEFORE", "FROM",
            "INTO", "GIVING", "UPON", "WITH", "THROUGH", "THRU", "CONTINUE",
            "EXIT", "EVALUATE", "ALSO", "ANY", "OTHER", "INSPECT", "STRING",
            "UNSTRING", "WRITE", "READ", "OPEN", "CLOSE", "SORT", "MERGE",
            "SEARCH", "CALL", "COMPUTE", "INITIALIZE", "REWRITE", "DELETE",
            "START", "RELEASE", "NAME", "REDEFINES", "COMP", "DATA", "PROGRAM"
        };

        private static string ToCobolName(string name)
        {
            var n = Regex.Replace(name.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "-");
            n = Regex.Replace(n, "-+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(n)) n = "FIELD";
            if (char.IsDigit(n[0])) n = $"F-{n}";
            if (_cobolReservedWords.Contains(n)) n = $"{n}-FLD";
            return n;
        }

        private static string Sanitize(string value, string fallback)
        {
            var n = Regex.Replace(value?.Trim() ?? string.Empty, @"[^A-Za-z0-9_\-]+", "_");
            n = Regex.Replace(n, "_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(n) ? fallback : n;
        }

        // Escape single-quote in COBOL string literals by doubling it.
        private static string CobolLit(string value) => value.Replace("'", "''");

        // Look up a key from AIParameters dict, trying multiple name variants.
        private static string? AiParam(Dictionary<string, string>? dict, params string[] keys)
        {
            if (dict == null) return null;
            foreach (var k in keys)
                if (dict.TryGetValue(k, out var v) && v != null) return v;
            return null;
        }
    }
}
