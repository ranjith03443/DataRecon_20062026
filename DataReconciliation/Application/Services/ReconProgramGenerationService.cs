using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class ReconProgramGenerationService : IReconProgramGenerationService
    {
        private readonly ILogger<ReconProgramGenerationService> _logger;
        private readonly IArtifactPersistenceService _artifactService;
        private readonly IFileIngestionService _fileService;
        private readonly IMainframeAiAgentService _aiAgent;

        public ReconProgramGenerationService(
            ILogger<ReconProgramGenerationService> logger,
            IArtifactPersistenceService artifactService,
            IFileIngestionService fileService,
            IMainframeAiAgentService aiAgent)
        {
            _logger = logger;
            _artifactService = artifactService;
            _fileService = fileService;
            _aiAgent = aiAgent;
        }

        public async Task<ReconProgramResultDto> GenerateAsync(string jobId, ReconProgramRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting recon program generation. JobId={JobId}", jobId);

            var reconConfig  = await _artifactService.LoadArtifactAsync<ReconciliationConfig>(jobId, ArtifactType.ReconciliationConfig);
            var reconResult  = await _artifactService.LoadArtifactAsync<ReconciliationResult>(jobId, ArtifactType.ReconciliationResult);
            var targetProfile = await _artifactService.LoadArtifactAsync<TargetMetadataProfile>(jobId, ArtifactType.TargetMetadataProfile);
            var finalMapping  = await _artifactService.LoadArtifactAsync<FinalMappingConfig>(jobId, ArtifactType.FinalMappingConfig);

            if (reconConfig == null || reconResult == null || targetProfile == null)
                throw new InvalidOperationException(
                    "Required artifacts missing. Ensure Steps 10 (Reconciliation Config) and 13 (Reconciliation) have completed before generating a Recon Program.");

            var programName = SanitizeCobolName(request.ProgramName, 8, "RECONPGM");
            var jobName     = SanitizeCobolName(request.JobName, 8, "RECONJOB");
            var timestamp   = DateTime.UtcNow;

            // ── Build ordered field positions from target profile ──────────────
            var orderedFields = targetProfile.Fields.OrderBy(f => f.ColumnOrder).ToList();
            int pos = 1;
            var fieldPositions = new Dictionary<string, (int Start, int Length)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in orderedFields)
            {
                int len = FieldWidth(f);
                fieldPositions[f.FieldName] = (pos, len);
                pos += len;
            }
            int totalRecordLength = pos - 1;

            // ── Build recon checks from config ────────────────────────────────
            var checks = new List<ReconCheckDto>();

            foreach (var field in reconConfig.Fields.OrderBy(f => f.Priority))
            {
                if (!fieldPositions.TryGetValue(field.FieldName, out var fp)) continue;

                var reconType  = (field.ValidationType ?? "COUNT").ToUpperInvariant();
                var cobolVar   = ToCobolName(field.FieldName);
                var sourceField = finalMapping?.Mappings
                    .FirstOrDefault(m => m.TargetField.Equals(field.FieldName, StringComparison.OrdinalIgnoreCase))
                    ?.SourceField;

                string expectedValue = "0";
                if (reconType == "SUM")
                {
                    var val = reconResult.FieldTotals.TryGetValue(field.FieldName, out var tv) ? tv : 0m;
                    expectedValue = val.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    var cnt = reconResult.TargetFieldCounts.TryGetValue(field.FieldName, out var tc) ? tc : 0;
                    expectedValue = cnt.ToString();
                }

                checks.Add(new ReconCheckDto
                {
                    FieldName     = field.FieldName,
                    ReconType     = reconType,
                    CobolVarName  = cobolVar,
                    PicClause     = $"PIC X({fp.Length})",
                    StartPosition = fp.Start,
                    Length        = fp.Length,
                    ExpectedValue = expectedValue,
                    SourceField   = sourceField
                });
            }

            var result = new ReconProgramResultDto
            {
                JobId               = jobId,
                ProgramName         = programName,
                JobName             = jobName,
                GeneratedAt         = timestamp,
                TotalExpectedRecords = reconResult.TotalTargetRecords,
                TotalRecordLength   = totalRecordLength,
                Checks              = checks
            };

            // ── Write output files ────────────────────────────────────────────
            var folder = await GetReconFolderAsync(jobId);
            var ts     = timestamp.ToString("yyyyMMddHHmmss");

            // Pre-launch AI tasks in parallel — recon COBOL and JCL are independent
            var reconCobolAiTask = (request.UseAiMode && request.GenerateCobol)
                ? _aiAgent.RunAgentAsync(BuildAiReconRequest(jobId, programName, jobName,
                    totalRecordLength, reconResult.TotalTargetRecords, checks, "generate_recon_cobol"))
                : null;
            var reconJclAiTask = (request.UseAiMode && request.GenerateJcl)
                ? _aiAgent.RunAgentAsync(BuildAiReconRequest(jobId, programName, jobName,
                    totalRecordLength, reconResult.TotalTargetRecords, checks, "generate_recon_jcl"))
                : null;

            if (reconCobolAiTask != null && reconJclAiTask != null)
            {
                _logger.LogInformation(
                    "AI Mode: awaiting recon COBOL + JCL generation in parallel. JobId={JobId}", jobId);
                await Task.WhenAll(reconCobolAiTask, reconJclAiTask);
            }
            else if (reconCobolAiTask != null) await reconCobolAiTask;
            else if (reconJclAiTask  != null) await reconJclAiTask;

            if (request.GenerateCobol)
            {
                string cobolContent;
                if (request.UseAiMode)
                {
                    var aiResp = await reconCobolAiTask!;
                    if (string.IsNullOrWhiteSpace(aiResp?.GeneratedCode))
                        throw new InvalidOperationException(
                            "AI Mode is on but the AI service did not return a COBOL program. " +
                            "Please switch AI Mode off and try again, or check that the Python AI service is running.");
                    cobolContent = aiResp.GeneratedCode!;
                    result.CobolAiGenerated = true;
                    _logger.LogInformation("AI COBOL generated. JobId={JobId} Confidence={Conf}", jobId, aiResp.Confidence);
                }
                else
                {
                    cobolContent = BuildCobol(programName, jobId, totalRecordLength, reconResult.TotalTargetRecords, checks, timestamp);
                }
                var cobolFile = Path.Combine(folder, $"{programName}_recon_{ts}.cbl");
                await File.WriteAllTextAsync(cobolFile, cobolContent, Encoding.UTF8);
                result.CobolFileName = Path.GetFileName(cobolFile);
                _logger.LogInformation("COBOL written. JobId={JobId} File={File}", jobId, result.CobolFileName);
            }

            if (request.GenerateJcl)
            {
                string jclContent;
                if (request.UseAiMode)
                {
                    var aiResp = await reconJclAiTask!;
                    if (string.IsNullOrWhiteSpace(aiResp?.GeneratedCode))
                        throw new InvalidOperationException(
                            "AI Mode is on but the AI service did not return a JCL job. " +
                            "Please switch AI Mode off and try again, or check that the Python AI service is running.");
                    jclContent = aiResp.GeneratedCode!;
                    result.JclAiGenerated = true;
                    _logger.LogInformation("AI JCL generated. JobId={JobId} Confidence={Conf}", jobId, aiResp.Confidence);
                }
                else
                {
                    jclContent = BuildJcl(programName, jobName, jobId, totalRecordLength, timestamp);
                }
                var jclFile = Path.Combine(folder, $"{jobName}_recon_{ts}.jcl");
                await File.WriteAllTextAsync(jclFile, jclContent, Encoding.UTF8);
                result.JclFileName = Path.GetFileName(jclFile);
                _logger.LogInformation("JCL written. JobId={JobId} File={File}", jobId, result.JclFileName);
            }

            if (request.GenerateSpec)
            {
                var specFile = Path.Combine(folder, $"recon_program_spec_{ts}.txt");
                await File.WriteAllTextAsync(specFile, BuildSpec(programName, jobName, jobId, totalRecordLength, reconResult.TotalTargetRecords, checks, timestamp), Encoding.UTF8);
                result.SpecFileName = Path.GetFileName(specFile);
                _logger.LogInformation("Spec written. JobId={JobId} File={File}", jobId, result.SpecFileName);
            }

            sw.Stop();
            _logger.LogInformation("Recon program generation completed. JobId={JobId} Checks={Count} Duration={Duration}ms",
                jobId, checks.Count, sw.ElapsedMilliseconds);

            return result;
        }

        public async Task<string?> ResolveProgramPathAsync(string jobId, string fileName)
        {
            var folder   = await GetReconFolderAsync(jobId);
            var safeName = Path.GetFileName(fileName);
            var full     = Path.GetFullPath(Path.Combine(folder, safeName));
            return File.Exists(full) ? full : null;
        }

        // ── AI request builder ────────────────────────────────────────────────

        private static MainframeAiAgentRequest BuildAiReconRequest(
            string jobId,
            string programName,
            string jobName,
            int totalRecordLength,
            int expectedRecordCount,
            List<ReconCheckDto> checks,
            string promptType)
        {
            var reconCheckDicts = checks.Select(c => new Dictionary<string, object?>
            {
                { "fieldName",     c.FieldName     },
                { "reconType",     c.ReconType     },
                { "cobolVarName",  c.CobolVarName  },
                { "startPosition", c.StartPosition },
                { "length",        c.Length        },
                { "expectedValue", c.ExpectedValue },
                { "sourceField",   c.SourceField   },
            }).ToList();

            return new MainframeAiAgentRequest
            {
                JobId               = jobId,
                PromptType          = promptType,
                ProgramName         = programName,
                JobName             = jobName,
                TotalRecordLength   = totalRecordLength,
                ExpectedRecordCount = expectedRecordCount,
                ReconChecks         = reconCheckDicts,
            };
        }

        // ── COBOL generation ─────────────────────────────────────────────────

        private static string BuildCobol(
            string programName,
            string jobId,
            int totalRecordLength,
            int expectedRecords,
            List<ReconCheckDto> checks,
            DateTime timestamp)
        {
            var sb = new StringBuilder();
            var date = timestamp.ToString("yyyy-MM-dd");

            sb.AppendLine($"      *================================================================");
            sb.AppendLine($"      * PROGRAM:  {programName}");
            sb.AppendLine($"      * AUTHOR:   DATARECON-SYSTEM");
            sb.AppendLine($"      * WRITTEN:  {date}");
            sb.AppendLine($"      * SOURCE:   DataReconciliation Job {jobId}");
            sb.AppendLine($"      * PURPOSE:  Reconciliation verification - reads the target");
            sb.AppendLine($"      *           fixed-width .dat file and verifies counts/sums");
            sb.AppendLine($"      *           match the workflow reconciliation_result.json.");
            sb.AppendLine($"      * *** DEVELOPER REVIEW REQUIRED: Update DD dataset names in JCL ***");
            sb.AppendLine($"      *================================================================");
            sb.AppendLine( "       IDENTIFICATION DIVISION.");
            sb.AppendLine($"       PROGRAM-ID.    {programName}.");
            sb.AppendLine( "       AUTHOR.        DATARECON-SYSTEM.");
            sb.AppendLine($"       DATE-WRITTEN.  {date}.");
            sb.AppendLine( "      *================================================================");
            sb.AppendLine( "       ENVIRONMENT DIVISION.");
            sb.AppendLine( "       CONFIGURATION SECTION.");
            sb.AppendLine( "       INPUT-OUTPUT SECTION.");
            sb.AppendLine( "       FILE-CONTROL.");
            sb.AppendLine( "           SELECT TARGET-FILE ASSIGN TO TARGET");
            sb.AppendLine( "               ORGANIZATION IS SEQUENTIAL");
            sb.AppendLine( "               ACCESS MODE  IS SEQUENTIAL");
            sb.AppendLine( "               FILE STATUS  IS WS-TARGET-STATUS.");
            sb.AppendLine( "           SELECT REPT-FILE   ASSIGN TO RPTFILE");
            sb.AppendLine( "               ORGANIZATION IS SEQUENTIAL");
            sb.AppendLine( "               ACCESS MODE  IS SEQUENTIAL");
            sb.AppendLine( "               FILE STATUS  IS WS-REPT-STATUS.");
            sb.AppendLine( "      *================================================================");
            sb.AppendLine( "       DATA DIVISION.");
            sb.AppendLine( "       FILE SECTION.");
            sb.AppendLine( "       FD  TARGET-FILE");
            sb.AppendLine( "           RECORDING MODE IS F");
            sb.AppendLine( "           BLOCK CONTAINS 0 RECORDS");
            sb.AppendLine($"           RECORD CONTAINS {totalRecordLength} CHARACTERS.");
            sb.AppendLine($"       01  TARGET-RECORD         PIC X({totalRecordLength}).");
            sb.AppendLine( "       FD  REPT-FILE");
            sb.AppendLine( "           RECORDING MODE IS F");
            sb.AppendLine( "           BLOCK CONTAINS 0 RECORDS");
            sb.AppendLine( "           RECORD CONTAINS 132 CHARACTERS.");
            sb.AppendLine( "       01  REPT-RECORD           PIC X(132).");
            sb.AppendLine( "      *================================================================");
            sb.AppendLine( "       WORKING-STORAGE SECTION.");
            sb.AppendLine( "       01  WS-FILE-STATUS.");
            sb.AppendLine( "           05  WS-TARGET-STATUS  PIC X(2) VALUE SPACES.");
            sb.AppendLine( "           05  WS-REPT-STATUS    PIC X(2) VALUE SPACES.");
            sb.AppendLine( "       01  WS-EOF                PIC X    VALUE 'N'.");
            sb.AppendLine( "           88  END-OF-FILE       VALUE 'Y'.");
            sb.AppendLine( "       01  WS-TOTAL-RECS         PIC 9(9)  COMP-3 VALUE 0.");
            sb.AppendLine( "       01  WS-PASS-CNT           PIC 9(4)  COMP-3 VALUE 0.");
            sb.AppendLine( "       01  WS-FAIL-CNT           PIC 9(4)  COMP-3 VALUE 0.");
            sb.AppendLine( "      *");
            sb.AppendLine( "      * Expected values from reconciliation_result.json");
            sb.AppendLine( "      *");
            sb.AppendLine($"       01  WS-EXP-TOTAL-RECS     PIC 9(9)     VALUE {expectedRecords}.");

            foreach (var c in checks)
            {
                if (c.ReconType == "SUM")
                    sb.AppendLine($"       01  WS-EXP-{Pad(c.CobolVarName, 20)} PIC 9(15)V99 VALUE {c.ExpectedValue}.");
                else
                    sb.AppendLine($"       01  WS-EXP-{Pad(c.CobolVarName + "-CNT", 20)} PIC 9(9)     VALUE {c.ExpectedValue}.");
            }

            sb.AppendLine( "      *");
            sb.AppendLine( "      * Accumulators");
            sb.AppendLine( "      *");

            foreach (var c in checks)
            {
                if (c.ReconType == "SUM")
                    sb.AppendLine($"       01  WS-ACT-{Pad(c.CobolVarName, 20)} PIC 9(15)V99 COMP-3 VALUE ZERO.");
                else
                    sb.AppendLine($"       01  WS-CNT-{Pad(c.CobolVarName, 20)} PIC 9(9)     COMP-3 VALUE ZERO.");
            }

            sb.AppendLine( "      *");
            sb.AppendLine( "      * Field extract buffers");
            sb.AppendLine( "      *");

            foreach (var c in checks)
                sb.AppendLine($"       01  WS-EXT-{Pad(c.CobolVarName, 20)} PIC X({c.Length}) VALUE SPACES.");

            sb.AppendLine( "      *");
            sb.AppendLine( "      * Report line (132 chars)");
            sb.AppendLine( "      *");
            sb.AppendLine( "       01  WS-REPT-LINE.");
            sb.AppendLine( "           05  WS-RL-FIELD       PIC X(20) VALUE SPACES.");
            sb.AppendLine( "           05  FILLER            PIC X(2)  VALUE SPACES.");
            sb.AppendLine( "           05  WS-RL-TYPE        PIC X(6)  VALUE SPACES.");
            sb.AppendLine( "           05  FILLER            PIC X(2)  VALUE SPACES.");
            sb.AppendLine( "           05  WS-RL-EXPECTED    PIC X(20) VALUE SPACES.");
            sb.AppendLine( "           05  FILLER            PIC X(2)  VALUE SPACES.");
            sb.AppendLine( "           05  WS-RL-ACTUAL      PIC X(20) VALUE SPACES.");
            sb.AppendLine( "           05  FILLER            PIC X(2)  VALUE SPACES.");
            sb.AppendLine( "           05  WS-RL-STATUS      PIC X(9)  VALUE SPACES.");
            sb.AppendLine( "           05  FILLER            PIC X(49) VALUE SPACES.");
            sb.AppendLine( "       01  WS-NUM-DISP           PIC Z(13)9.99.");
            sb.AppendLine( "       01  WS-CNT-DISP           PIC Z(8)9.");
            sb.AppendLine( "      *================================================================");
            sb.AppendLine( "       PROCEDURE DIVISION.");
            sb.AppendLine( "       0000-MAIN.");
            sb.AppendLine( "           PERFORM 1000-INIT");
            sb.AppendLine( "           PERFORM 2000-PROCESS UNTIL END-OF-FILE");
            sb.AppendLine( "           PERFORM 9000-TERMINATE");
            sb.AppendLine( "           STOP RUN.");
            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       1000-INIT.");
            sb.AppendLine( "           OPEN INPUT  TARGET-FILE");
            sb.AppendLine( "           OPEN OUTPUT REPT-FILE");
            sb.AppendLine( "           IF WS-TARGET-STATUS NOT = '00'");
            sb.AppendLine( "               DISPLAY 'ERROR OPENING TARGET FILE: ' WS-TARGET-STATUS");
            sb.AppendLine( "               STOP RUN");
            sb.AppendLine( "           END-IF");
            sb.AppendLine( "           PERFORM 1100-WRITE-HDR");
            sb.AppendLine( "           READ TARGET-FILE");
            sb.AppendLine( "               AT END MOVE 'Y' TO WS-EOF");
            sb.AppendLine( "           END-READ.");
            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       1100-WRITE-HDR.");
            sb.AppendLine( "           MOVE SPACES TO REPT-RECORD");
            sb.AppendLine( "           MOVE 'RECONCILIATION VERIFICATION REPORT'");
            sb.AppendLine( "               TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD AFTER ADVANCING PAGE");
            sb.AppendLine($"           MOVE 'Job: {jobId}  Program: {programName}'");
            sb.AppendLine( "               TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           MOVE SPACES TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           MOVE 'CHECK                TYPE    EXPECTED" +
                           "             ACTUAL               STATUS'");
            sb.AppendLine( "               TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           MOVE ALL '=' TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD.");
            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       2000-PROCESS.");
            sb.AppendLine( "           ADD 1 TO WS-TOTAL-RECS");
            sb.AppendLine( "      *    Extract recon fields via reference modification");

            foreach (var c in checks)
                sb.AppendLine($"           MOVE TARGET-RECORD({c.StartPosition}:{c.Length}) TO WS-EXT-{c.CobolVarName}");

            foreach (var c in checks)
            {
                if (c.ReconType == "SUM")
                {
                    sb.AppendLine($"      *    Accumulate {c.FieldName} (SUM)");
                    sb.AppendLine($"           IF WS-EXT-{c.CobolVarName} NOT = SPACES");
                    sb.AppendLine($"               COMPUTE WS-ACT-{c.CobolVarName} =");
                    sb.AppendLine($"                   WS-ACT-{c.CobolVarName}");
                    sb.AppendLine($"                   + FUNCTION NUMVAL(WS-EXT-{c.CobolVarName})");
                    sb.AppendLine( "           END-IF");
                }
                else
                {
                    sb.AppendLine($"      *    Count non-blank {c.FieldName} (COUNT)");
                    sb.AppendLine($"           IF WS-EXT-{c.CobolVarName} NOT = SPACES");
                    sb.AppendLine($"               ADD 1 TO WS-CNT-{c.CobolVarName}");
                    sb.AppendLine( "           END-IF");
                }
            }

            sb.AppendLine( "           READ TARGET-FILE");
            sb.AppendLine( "               AT END MOVE 'Y' TO WS-EOF");
            sb.AppendLine( "           END-READ.");
            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       9000-TERMINATE.");
            sb.AppendLine( "           PERFORM 9100-CHK-RECS");

            for (int i = 0; i < checks.Count; i++)
                sb.AppendLine($"           PERFORM 9{101 + i}-CHK-{checks[i].CobolVarName}");

            sb.AppendLine( "           PERFORM 9900-WRITE-SUMMARY");
            sb.AppendLine( "           CLOSE TARGET-FILE");
            sb.AppendLine( "                 REPT-FILE.");
            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       9100-CHK-RECS.");
            sb.AppendLine( "           MOVE 'RECORD-COUNT'        TO WS-RL-FIELD");
            sb.AppendLine( "           MOVE 'COUNT ' TO WS-RL-TYPE");
            sb.AppendLine( "           MOVE WS-EXP-TOTAL-RECS     TO WS-CNT-DISP");
            sb.AppendLine( "           MOVE WS-CNT-DISP           TO WS-RL-EXPECTED");
            sb.AppendLine( "           MOVE WS-TOTAL-RECS         TO WS-CNT-DISP");
            sb.AppendLine( "           MOVE WS-CNT-DISP           TO WS-RL-ACTUAL");
            sb.AppendLine( "           IF WS-TOTAL-RECS = WS-EXP-TOTAL-RECS");
            sb.AppendLine( "               MOVE 'PASS     ' TO WS-RL-STATUS");
            sb.AppendLine( "               ADD 1 TO WS-PASS-CNT");
            sb.AppendLine( "           ELSE");
            sb.AppendLine( "               MOVE 'FAIL ****' TO WS-RL-STATUS");
            sb.AppendLine( "               ADD 1 TO WS-FAIL-CNT");
            sb.AppendLine( "           END-IF");
            sb.AppendLine( "           MOVE WS-REPT-LINE TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD.");

            for (int i = 0; i < checks.Count; i++)
            {
                var c    = checks[i];
                var para = $"9{101 + i}-CHK-{c.CobolVarName}";
                sb.AppendLine( "      *----------------------------------------------------------------");
                sb.AppendLine($"       {para}.");
                sb.AppendLine($"           MOVE '{Truncate(c.FieldName, 20)}'");
                sb.AppendLine( "               TO WS-RL-FIELD");

                if (c.ReconType == "SUM")
                {
                    sb.AppendLine( "           MOVE 'SUM   ' TO WS-RL-TYPE");
                    sb.AppendLine($"           MOVE WS-EXP-{c.CobolVarName}  TO WS-NUM-DISP");
                    sb.AppendLine( "           MOVE WS-NUM-DISP              TO WS-RL-EXPECTED");
                    sb.AppendLine($"           MOVE WS-ACT-{c.CobolVarName}  TO WS-NUM-DISP");
                    sb.AppendLine( "           MOVE WS-NUM-DISP              TO WS-RL-ACTUAL");
                    sb.AppendLine($"           IF WS-ACT-{c.CobolVarName} = WS-EXP-{c.CobolVarName}");
                }
                else
                {
                    sb.AppendLine( "           MOVE 'COUNT ' TO WS-RL-TYPE");
                    sb.AppendLine($"           MOVE WS-EXP-{c.CobolVarName}-CNT TO WS-CNT-DISP");
                    sb.AppendLine( "           MOVE WS-CNT-DISP                 TO WS-RL-EXPECTED");
                    sb.AppendLine($"           MOVE WS-CNT-{c.CobolVarName}     TO WS-CNT-DISP");
                    sb.AppendLine( "           MOVE WS-CNT-DISP                 TO WS-RL-ACTUAL");
                    sb.AppendLine($"           IF WS-CNT-{c.CobolVarName} = WS-EXP-{c.CobolVarName}-CNT");
                }

                sb.AppendLine( "               MOVE 'PASS     ' TO WS-RL-STATUS");
                sb.AppendLine( "               ADD 1 TO WS-PASS-CNT");
                sb.AppendLine( "           ELSE");
                sb.AppendLine( "               MOVE 'FAIL ****' TO WS-RL-STATUS");
                sb.AppendLine( "               ADD 1 TO WS-FAIL-CNT");
                sb.AppendLine( "           END-IF");
                sb.AppendLine( "           MOVE WS-REPT-LINE TO REPT-RECORD");
                sb.AppendLine( "           WRITE REPT-RECORD.");
            }

            sb.AppendLine( "      *----------------------------------------------------------------");
            sb.AppendLine( "       9900-WRITE-SUMMARY.");
            sb.AppendLine( "           MOVE SPACES TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD AFTER ADVANCING 2 LINES");
            sb.AppendLine( "           MOVE ALL '=' TO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           MOVE WS-PASS-CNT TO WS-CNT-DISP");
            sb.AppendLine( "           STRING 'CHECKS PASSED : ' WS-CNT-DISP");
            sb.AppendLine( "               DELIMITED SIZE INTO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           MOVE WS-FAIL-CNT TO WS-CNT-DISP");
            sb.AppendLine( "           STRING 'CHECKS FAILED : ' WS-CNT-DISP");
            sb.AppendLine( "               DELIMITED SIZE INTO REPT-RECORD");
            sb.AppendLine( "           WRITE REPT-RECORD");
            sb.AppendLine( "           IF WS-FAIL-CNT = 0");
            sb.AppendLine( "               MOVE 'OVERALL STATUS: PASS - ALL CHECKS MATCH'");
            sb.AppendLine( "                   TO REPT-RECORD");
            sb.AppendLine( "           ELSE");
            sb.AppendLine( "               MOVE 'OVERALL STATUS: FAIL - DISCREPANCIES DETECTED'");
            sb.AppendLine( "                   TO REPT-RECORD");
            sb.AppendLine( "           END-IF");
            sb.AppendLine( "           WRITE REPT-RECORD AFTER ADVANCING 2 LINES.");

            return sb.ToString();
        }

        // ── JCL generation ────────────────────────────────────────────────────

        private static string BuildJcl(
            string programName,
            string jobName,
            string jobId,
            int totalRecordLength,
            DateTime timestamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"//{jobName} JOB (ACCT),'RECON VERIFY',CLASS=A,MSGCLASS=X,");
            sb.AppendLine( "//            MSGLEVEL=(1,1),NOTIFY=&SYSUID");
            sb.AppendLine( "//*");
            sb.AppendLine($"//* RECONCILIATION VERIFICATION JOB");
            sb.AppendLine($"//* Generated: {timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"//* Source job: {jobId}");
            sb.AppendLine($"//* Target file: RECFM=FB  LRECL={totalRecordLength}");
            sb.AppendLine( "//* *** DEVELOPER: Update dataset names below before submitting ***");
            sb.AppendLine( "//*");
            sb.AppendLine($"//STEP01   EXEC PGM={programName}");
            sb.AppendLine( "//STEPLIB  DD DSN=your.load.library,DISP=SHR");
            sb.AppendLine( "//*");
            sb.AppendLine( "//* Input: target fixed-width output file");
            sb.AppendLine( "//TARGET   DD DSN=your.target.output.dat,DISP=SHR,");
            sb.AppendLine($"//            RECFM=FB,LRECL={totalRecordLength},BLKSIZE=0");
            sb.AppendLine( "//*");
            sb.AppendLine( "//* Output: reconciliation verification report");
            sb.AppendLine( "//RPTFILE  DD DSN=your.recon.report,");
            sb.AppendLine( "//            DISP=(NEW,CATLG,DELETE),");
            sb.AppendLine( "//            SPACE=(CYL,(1,1)),");
            sb.AppendLine( "//            RECFM=FB,LRECL=132,BLKSIZE=0");
            sb.AppendLine( "//SYSOUT   DD SYSOUT=*");
            sb.AppendLine( "//SYSUDUMP DD SYSOUT=*");
            return sb.ToString();
        }

        // ── Spec generation ───────────────────────────────────────────────────

        private static string BuildSpec(
            string programName,
            string jobName,
            string jobId,
            int totalRecordLength,
            int expectedRecords,
            List<ReconCheckDto> checks,
            DateTime timestamp)
        {
            var sb = new StringBuilder();
            var sep = new string('=', 80);

            sb.AppendLine(sep);
            sb.AppendLine("RECONCILIATION VERIFICATION PROGRAM — SPECIFICATION");
            sb.AppendLine(sep);
            sb.AppendLine($"Program Name   : {programName}");
            sb.AppendLine($"Job Name       : {jobName}");
            sb.AppendLine($"Source Job ID  : {jobId}");
            sb.AppendLine($"Generated      : {timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            sb.AppendLine("PURPOSE");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine("  Reads the target fixed-width output file produced by the");
            sb.AppendLine("  DataReconciliation workflow and independently verifies that");
            sb.AppendLine("  record counts and field totals match the expected values");
            sb.AppendLine("  computed by the C# reconciliation step (reconciliation_result.json).");
            sb.AppendLine("  Produces a PASS/FAIL report for each configured check.");
            sb.AppendLine();
            sb.AppendLine("TARGET FILE LAYOUT");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine($"  Format         : Fixed-width (RECFM=FB)");
            sb.AppendLine($"  Record length  : {totalRecordLength} bytes (LRECL)");
            sb.AppendLine($"  Expected records: {expectedRecords}");
            sb.AppendLine();
            sb.AppendLine("RECONCILIATION CHECKS");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine($"  {"#",-4} {"Field",-22} {"Type",-7} {"Source Field",-20} {"Expected Value",-20}");
            sb.AppendLine($"  {new string('-', 4)} {new string('-', 22)} {new string('-', 7)} {new string('-', 20)} {new string('-', 20)}");
            sb.AppendLine($"  {"1",-4} {"RECORD-COUNT",-22} {"COUNT",-7} {"(all records)",-20} {expectedRecords,-20}");

            for (int i = 0; i < checks.Count; i++)
            {
                var c = checks[i];
                sb.AppendLine($"  {i + 2,-4} {c.FieldName,-22} {c.ReconType,-7} {(c.SourceField ?? "-"),-20} {c.ExpectedValue,-20}");
            }

            sb.AppendLine();
            sb.AppendLine("FIELD EXTRACTION (REFERENCE MODIFICATION)");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine($"  {"Field",-22} {"COBOL Variable",-26} {"Start",-7} {"Length",-8} {"PIC"}");
            sb.AppendLine($"  {new string('-', 22)} {new string('-', 26)} {new string('-', 7)} {new string('-', 8)} {new string('-', 15)}");

            foreach (var c in checks)
                sb.AppendLine($"  {c.FieldName,-22} WS-EXT-{c.CobolVarName,-19} {c.StartPosition,-7} {c.Length,-8} {c.PicClause}");

            sb.AppendLine();
            sb.AppendLine("DEVELOPER INSTRUCTIONS");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine("  1. Compile the COBOL source into your load library.");
            sb.AppendLine($"     e.g.  //COMPILE  EXEC PROC=IGYWCL,PARM.COBOL='...'");
            sb.AppendLine($"           //COBOL.SYSIN DD DSN=your.source.lib({programName}),DISP=SHR");
            sb.AppendLine();
            sb.AppendLine("  2. Update the JCL dataset names:");
            sb.AppendLine("     - STEPLIB  : your.load.library");
            sb.AppendLine("     - TARGET   : the actual target output .dat file");
            sb.AppendLine("     - RPTFILE  : output destination for the reconciliation report");
            sb.AppendLine();
            sb.AppendLine("  3. Submit the JCL and check RPTFILE for PASS/FAIL results.");
            sb.AppendLine();
            sb.AppendLine("  4. FAIL lines are marked '****' in the STATUS column.");
            sb.AppendLine("     Review any discrepancies against the source data.");
            sb.AppendLine();
            sb.AppendLine(sep);

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<string> GetReconFolderAsync(string jobId)
        {
            var reportsPath = await _fileService.GetWorkflowPathAsync(jobId, "reports");
            var folder = Path.Combine(reportsPath, "mainframe-recon");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static int FieldWidth(TargetFieldMetadata f)
        {
            var isText = f.Datatype?.IndexOf("STRING",  StringComparison.OrdinalIgnoreCase) >= 0
                      || f.Datatype?.IndexOf("VARCHAR", StringComparison.OrdinalIgnoreCase) >= 0
                      || f.Datatype?.IndexOf("CHAR",    StringComparison.OrdinalIgnoreCase) >= 0
                      || f.Datatype?.IndexOf("TEXT",    StringComparison.OrdinalIgnoreCase) >= 0;
            return f.FieldLength ?? (isText ? 30 : 10);
        }

        private static string ToCobolName(string name)
        {
            var cobol = Regex.Replace(name.ToUpperInvariant(), @"[^A-Z0-9]", "-");
            if (cobol.Length > 28) cobol = cobol[..28];
            return cobol;
        }

        private static string SanitizeCobolName(string name, int maxLen, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            var clean = Regex.Replace(name.ToUpperInvariant(), @"[^A-Z0-9]", "");
            return clean.Length == 0 ? fallback : clean[..Math.Min(clean.Length, maxLen)];
        }

        private static string Pad(string s, int width) =>
            s.Length >= width ? s[..width] : s + new string(' ', width - s.Length);

        private static string Truncate(string s, int max) =>
            s.Length > max ? s[..max] : s;
    }
}
