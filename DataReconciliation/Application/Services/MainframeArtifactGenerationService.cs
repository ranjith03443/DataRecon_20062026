using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class MainframeArtifactGenerationService : IMainframeArtifactGenerationService
    {
        private readonly ILogger<MainframeArtifactGenerationService> _logger;
        private readonly IArtifactPersistenceService _artifactPersistenceService;
        private readonly IFileIngestionService _fileIngestionService;

        public MainframeArtifactGenerationService(
            ILogger<MainframeArtifactGenerationService> logger,
            IArtifactPersistenceService artifactPersistenceService,
            IFileIngestionService fileIngestionService)
        {
            _logger = logger;
            _artifactPersistenceService = artifactPersistenceService;
            _fileIngestionService = fileIngestionService;
        }

        public async Task<MainframeArtifactGenerationResultDto> GenerateAsync(
            string jobId,
            string recordName,
            string applicationName,
            bool includeComments = true)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting mainframe artifact generation. JobId={JobId} RecordName={RecordName} ApplicationName={ApplicationName}", jobId, recordName, applicationName);

            var targetProfile = await _artifactPersistenceService.LoadArtifactAsync<TargetMetadataProfile>(jobId, ArtifactType.TargetMetadataProfile);
            if (targetProfile == null)
            {
                throw new InvalidOperationException($"Target metadata profile not found for job {jobId}. Run the target schema generator or workflow target metadata step first.");
            }

            var finalMapping = await _artifactPersistenceService.LoadArtifactAsync<FinalMappingConfig>(jobId, ArtifactType.FinalMappingConfig);
            var mappingLookup = finalMapping?.Mappings
                .GroupBy(mapping => mapping.TargetField, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, FinalMapping>(StringComparer.OrdinalIgnoreCase);

            var fields = targetProfile.Fields
                .OrderBy(field => field.ColumnOrder)
                .Select(field => BuildFieldDto(field, mappingLookup.TryGetValue(field.FieldName, out var mapping) ? mapping : null))
                .ToList();

            var mainframeFolder = await GetMainframeFolderAsync(jobId);
            Directory.CreateDirectory(mainframeFolder);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var safeRecordName = SanitizeName(recordName, "MAINFRAME_RECORD");
            var safeApplicationName = SanitizeName(applicationName, "MAINFRAME_APPLICATION");

            var copybookFileName = $"mainframe_copybook_{timestamp}.cpy";
            var specificationFileName = $"mainframe_specification_{timestamp}.txt";
            var summaryFileName = $"mainframe_artifact_summary_{timestamp}.json";

            var copybookPath = Path.Combine(mainframeFolder, copybookFileName);
            var specificationPath = Path.Combine(mainframeFolder, specificationFileName);
            var summaryPath = Path.Combine(mainframeFolder, summaryFileName);

            await File.WriteAllTextAsync(copybookPath, BuildCopybookText(safeRecordName, safeApplicationName, fields, includeComments), Encoding.UTF8);
            await File.WriteAllTextAsync(specificationPath, BuildSpecificationText(safeRecordName, safeApplicationName, fields, includeComments), Encoding.UTF8);

            var result = new MainframeArtifactGenerationResultDto
            {
                JobId = jobId,
                RecordName = safeRecordName,
                ApplicationName = safeApplicationName,
                GeneratedAt = DateTime.UtcNow,
                CopybookFileName = copybookFileName,
                SpecificationFileName = specificationFileName,
                SummaryFileName = summaryFileName,
                Fields = fields
            };

            await File.WriteAllTextAsync(summaryPath, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);

            stopwatch.Stop();
            _logger.LogInformation(
                "Mainframe artifact generation completed. JobId={JobId} Files={Copybook},{Spec},{Summary} Duration={Duration}ms",
                jobId,
                copybookFileName,
                specificationFileName,
                summaryFileName,
                stopwatch.ElapsedMilliseconds);

            return result;
        }

        public async Task<string?> ResolveArtifactPathAsync(string jobId, string fileName)
        {
            var mainframeFolder = await GetMainframeFolderAsync(jobId);
            var safeFileName = Path.GetFileName(fileName);
            var fullPath = Path.GetFullPath(Path.Combine(mainframeFolder, safeFileName));
            return File.Exists(fullPath) ? fullPath : null;
        }

        private async Task<string> GetMainframeFolderAsync(string jobId)
        {
            var reportsPath = await _fileIngestionService.GetWorkflowPathAsync(jobId, "reports");
            return Path.GetFullPath(Path.Combine(reportsPath, "mainframe"));
        }

        private static MainframeArtifactFieldDto BuildFieldDto(TargetFieldMetadata field, FinalMapping? mapping)
        {
            var (pictureClause, justification) = BuildPictureClause(field);
            return new MainframeArtifactFieldDto
            {
                ColumnOrder = field.ColumnOrder,
                FieldName = field.FieldName,
                DataType = field.Datatype,
                Length = field.FieldLength,
                PictureClause = pictureClause,
                Justification = justification,
                Format = field.Format,
                Rule = field.Rule,
                Description = field.Description,
                SourceField = mapping?.SourceField
            };
        }

        private static (string PictureClause, string Justification) BuildPictureClause(TargetFieldMetadata field)
        {
            var fieldName = field.FieldName.ToUpperInvariant();
            var length = Math.Max(field.FieldLength ?? InferDefaultLength(field), 1);

            if (IsIdentifierLike(fieldName))
            {
                return ($"PIC X({length})", "Identifier-style field kept as alphanumeric to preserve account/code values.");
            }

            if (IsBoolean(field.Datatype))
            {
                return ("PIC X(1)", "Boolean field represented as a single-character flag.");
            }

            if (IsInteger(field.Datatype))
            {
                return ($"PIC 9({length})", "Numeric whole-number field represented as packed-display digits.");
            }

            if (IsDecimal(field.Datatype))
            {
                return ($"PIC 9({Math.Max(length - 2, 1)})V99", "Decimal field with 2 implied decimal places.");
            }

            if (IsDate(field.Datatype) || LooksLikeDate(field.Format))
            {
                return ("PIC X(8)", "Date field represented as an 8-character external date format.");
            }

            return ($"PIC X({length})", "Default alphanumeric representation.");
        }

        private static int InferDefaultLength(TargetFieldMetadata field)
        {
            if (IsDate(field.Datatype) || LooksLikeDate(field.Format))
                return 8;

            if (IsBoolean(field.Datatype))
                return 1;

            if (IsInteger(field.Datatype) || IsDecimal(field.Datatype))
                return 18;

            return 30;
        }

        private static string BuildCopybookText(
            string recordName,
            string applicationName,
            IReadOnlyCollection<MainframeArtifactFieldDto> fields,
            bool includeComments)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"      * Mainframe Copybook for {applicationName}");
            sb.AppendLine($"      * Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"       01  {recordName}.");

            foreach (var field in fields)
            {
                var comment = includeComments
                    ? $" *> {field.Description ?? field.Justification}"
                    : string.Empty;
                sb.AppendLine($"           05  {NormalizeCobolName(field.FieldName),-30} {field.PictureClause}.{comment}");
            }

            return sb.ToString();
        }

        private static string BuildSpecificationText(
            string recordName,
            string applicationName,
            IReadOnlyCollection<MainframeArtifactFieldDto> fields,
            bool includeComments)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MAINFRAME SPECIFICATION - {applicationName}");
            sb.AppendLine($"RECORD NAME: {recordName}");
            sb.AppendLine($"GENERATED AT: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine(new string('-', 110));
            sb.AppendLine($"{"ORD",4}  {"FIELD",-24} {"TYPE",-12} {"LEN",5} {"PICTURE",-20} {"SOURCE FIELD",-24} DESCRIPTION");
            sb.AppendLine(new string('-', 110));

            foreach (var field in fields)
            {
                var description = includeComments ? (field.Description ?? field.Justification) : string.Empty;
                sb.AppendLine($"{field.ColumnOrder + 1,4}  {field.FieldName,-24} {field.DataType,-12} {field.Length?.ToString() ?? "-",5} {field.PictureClause,-20} {(field.SourceField ?? "-") ,-24} {description}");
            }

            return sb.ToString();
        }

        private static bool IsIdentifierLike(string fieldName)
        {
            var tokens = new[] { "ACCT", "ACCOUNT", "KONTO", "ID", "NO", "NUM", "CODE", "REF", "SEQ", "KEY" };
            return tokens.Any(token => fieldName.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsInteger(string datatype) =>
            datatype.Contains("INTEGER", StringComparison.OrdinalIgnoreCase) ||
            datatype.Contains("INT", StringComparison.OrdinalIgnoreCase) ||
            datatype.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase);

        // NUMERIC is treated as integer (whole-number), not decimal — use DECIMAL explicitly for V99.
        private static bool IsDecimal(string datatype) =>
            datatype.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase);

        private static bool IsBoolean(string datatype) =>
            datatype.Contains("BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            datatype.Contains("BOOL", StringComparison.OrdinalIgnoreCase);

        private static bool IsDate(string datatype) =>
            datatype.Contains("DATE", StringComparison.OrdinalIgnoreCase) ||
            datatype.Contains("TIME", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeDate(string? format)
        {
            if (string.IsNullOrWhiteSpace(format))
                return false;

            return Regex.IsMatch(format, "(YYYY|yyyy|YY|yy).*(MM|mm)|(?:MM|mm).*(YYYY|yyyy|YY|yy)", RegexOptions.IgnoreCase);
        }

        private static string NormalizeCobolName(string fieldName)
        {
            var normalized = Regex.Replace(fieldName.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "-");
            normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "FIELD";

            if (char.IsDigit(normalized[0]))
                normalized = $"F-{normalized}";

            return normalized;
        }

        private static string SanitizeName(string value, string fallback)
        {
            var normalized = Regex.Replace(value?.Trim() ?? string.Empty, @"[^A-Za-z0-9_\-]+", "_");
            normalized = Regex.Replace(normalized, "_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }
    }
}
