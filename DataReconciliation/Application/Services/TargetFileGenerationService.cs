using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DataReconciliation.Application.Services
{
    public class TargetFileGenerationService : ITargetFileGenerationService
    {
        private readonly ILogger<TargetFileGenerationService> _logger;
        private readonly string _baseWorkflowPath;
        private readonly string _fileFormat;
        private readonly string _delimiter;
        private readonly bool _includeHeader;
        private readonly bool _includeTrailer;

        public TargetFileGenerationService(
            ILogger<TargetFileGenerationService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _baseWorkflowPath = configuration["WorkflowStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "workflow");
            _fileFormat = configuration["TargetFile:Format"] ?? "fixed_width";
            _delimiter = configuration["TargetFile:Delimiter"] ?? ",";
            _includeHeader = !string.Equals(configuration["TargetFile:IncludeHeader"], "false", StringComparison.OrdinalIgnoreCase);
            _includeTrailer = !string.Equals(configuration["TargetFile:IncludeTrailer"], "false", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GenerateTargetFileAsync(
            string jobId,
            IEnumerable<Dictionary<string, string>> transformedRecords,
            TargetMetadataProfile targetProfile)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting target file generation. JobId={JobId}", jobId);

            var outputPath = Path.Combine(_baseWorkflowPath, jobId, "artifacts");
            Directory.CreateDirectory(outputPath);
            var filePath = Path.Combine(outputPath, "target_output.dat");

            var recordList = transformedRecords.ToList();
            var orderedFields = targetProfile.Fields.OrderBy(f => f.ColumnOrder).ToList();

            // Calculate total record width for fixed-width format
            var totalWidth = orderedFields.Sum(f => f.FieldLength ?? 10);
            _logger.LogInformation("Target file format. JobId={JobId} FieldCount={Fields} TotalWidth={Width} Mode={Mode}",
                jobId, orderedFields.Count, totalWidth, _fileFormat);

            await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

            // Write header record
            var headerRecord = _includeHeader
                ? BuildHeaderRecord(orderedFields, totalWidth)
                : string.Empty;
            if (!string.IsNullOrEmpty(headerRecord))
            {
                await writer.WriteLineAsync(headerRecord);
                _logger.LogDebug("Header record written. JobId={JobId}", jobId);
            }

            // Write data records
            int writtenCount = 0;
            foreach (var record in recordList)
            {
                var line = string.Equals(_fileFormat, "delimited", StringComparison.OrdinalIgnoreCase)
                    ? BuildDelimitedRecord(record, orderedFields, _delimiter)
                    : BuildDataRecord(record, orderedFields);
                await writer.WriteLineAsync(line);
                writtenCount++;
            }

            // Write trailer record
            var trailerRecord = _includeTrailer
                ? BuildTrailerRecord(orderedFields, recordList.Count, totalWidth)
                : string.Empty;
            if (!string.IsNullOrEmpty(trailerRecord))
            {
                await writer.WriteLineAsync(trailerRecord);
                _logger.LogDebug("Trailer record written. JobId={JobId} RecordCount={Count}", jobId, recordList.Count);
            }

            sw.Stop();
            _logger.LogInformation("Target file generated. JobId={JobId} Records={Count} FilePath={Path} Duration={Duration}ms",
                jobId, writtenCount, filePath, sw.ElapsedMilliseconds);

            return filePath;
        }

        private static string BuildDataRecord(
            Dictionary<string, string> record,
            List<TargetFieldMetadata> fields)
        {
            var sb = new StringBuilder();

            foreach (var field in fields)
            {
                record.TryGetValue(field.FieldName, out var value);
                var width = field.FieldLength ?? 10;
                var padded = PadField(value ?? string.Empty, width, field.Datatype);
                sb.Append(padded);
            }

            return sb.ToString();
        }

        private static string BuildDelimitedRecord(
            Dictionary<string, string> record,
            List<TargetFieldMetadata> fields,
            string delimiter)
        {
            var values = fields.Select(f =>
            {
                record.TryGetValue(f.FieldName, out var value);
                var escaped = (value ?? string.Empty).Replace("\"", "\"\"");
                return $"\"{escaped}\"";
            });

            return string.Join(delimiter, values);
        }

        private static string BuildHeaderRecord(List<TargetFieldMetadata> fields, int totalWidth)
        {
            // Look for HDR hardcoded field
            var hdrField = fields.FirstOrDefault(f =>
                f.HardcodedValue?.Equals("HDR", StringComparison.OrdinalIgnoreCase) == true ||
                f.FieldName.Equals("HDR_REC_TYPE", StringComparison.OrdinalIgnoreCase));

            if (hdrField == null) return string.Empty;

            return "HDR" + new string(' ', Math.Max(0, totalWidth - 3));
        }

        private static string BuildTrailerRecord(List<TargetFieldMetadata> fields, int recordCount, int totalWidth)
        {
            var trlField = fields.FirstOrDefault(f =>
                f.HardcodedValue?.Equals("TRL", StringComparison.OrdinalIgnoreCase) == true ||
                f.FieldName.Equals("TRL_REC_TYPE", StringComparison.OrdinalIgnoreCase) ||
                f.FieldName.Contains("TRAIL", StringComparison.OrdinalIgnoreCase));

            if (trlField == null) return string.Empty;

            return $"TRL{recordCount.ToString().PadLeft(10, '0')}" + new string(' ', Math.Max(0, totalWidth - 13));
        }

        private static string PadField(string value, int width, string? datatype)
        {
            if (value.Length > width) return value[..width];

            var isNumeric = datatype?.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase) == true ||
                            datatype?.Contains("INT", StringComparison.OrdinalIgnoreCase) == true;

            return isNumeric
                ? value.PadLeft(width, '0')   // Right-align numeric with zero padding
                : value.PadRight(width, ' ');  // Left-align text with space padding
        }
    }
}
