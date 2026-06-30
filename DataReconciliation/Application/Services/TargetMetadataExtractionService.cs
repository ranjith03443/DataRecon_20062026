using CsvHelper;
using CsvHelper.Configuration;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using ExcelDataReader;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;
using System.Text;

namespace DataReconciliation.Application.Services
{
    public class TargetMetadataExtractionService : ITargetMetadataExtractionService
    {
        private readonly ILogger<TargetMetadataExtractionService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        public TargetMetadataExtractionService(
            ILogger<TargetMetadataExtractionService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
            // Required for ExcelDataReader on non-Windows or .NET Core
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public async Task<TargetMetadataProfile> ExtractTargetMetadataAsync(string jobId, string filePath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting target metadata extraction. JobId={JobId} FilePath={FilePath}", jobId, filePath);

            var profile = new TargetMetadataProfile
            {
                JobId = jobId,
                TargetDataset = Path.GetFileName(filePath),
                ExtractedAt = DateTime.UtcNow
            };

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".csv" || ext == ".txt")
                await ExtractFromCsvAsync(jobId, filePath, profile);
            else
                await ExtractFromExcelAsync(jobId, filePath, profile);

            sw.Stop();
            _logger.LogInformation("Target metadata extraction completed. JobId={JobId} FieldCount={Count} Duration={Duration}ms",
                jobId, profile.Fields.Count, sw.ElapsedMilliseconds);

            await PersistMetadataProfileAsync(jobId, profile);
            return profile;
        }

        private async Task ExtractFromCsvAsync(string jobId, string filePath, TargetMetadataProfile profile)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                BadDataFound = null
            };

            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, config);

            await csv.ReadAsync();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();

            _logger.LogInformation("Parsing CSV target schema. JobId={JobId} Columns={Cols}", jobId, headers.Length);

            int order = 0;
            foreach (var header in headers.Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                // Read optional metadata columns if present (FIELD_NAME, DATATYPE, FORMAT, RULE, DESCRIPTION)
                // For a simple header-only schema CSV, treat each column as a field name
                var field = new TargetFieldMetadata
                {
                    FieldName = header.Trim(),
                    Datatype = "string",
                    ColumnOrder = order++
                };
                profile.Fields.Add(field);
                _logger.LogDebug("Target field extracted from CSV. FieldName={FieldName}", header);
            }

            // If file has data rows, try to read richer metadata (FIELD_NAME, DATATYPE, FORMAT columns)
            if (headers.Any(h => h.Contains("FIELD", StringComparison.OrdinalIgnoreCase)
                               || h.Contains("COLUMN", StringComparison.OrdinalIgnoreCase)
                               || h.Contains("TYPE", StringComparison.OrdinalIgnoreCase)))
            {
                profile.Fields.Clear();
                order = 0;
                while (await csv.ReadAsync())
                {
                    var fieldName = TryGetCsvField(csv, headers, "FIELD_NAME", "FIELD", "COLUMN", "TARGET_FIELD");
                    if (string.IsNullOrWhiteSpace(fieldName)) continue;

                    var datatype = TryGetCsvField(csv, headers, "DATATYPE", "DATA_TYPE", "TYPE") ?? "string";
                    var format = TryGetCsvField(csv, headers, "FORMAT", "VALUE_FORMAT", "VALUE");
                    var rule = TryGetCsvField(csv, headers, "RULE", "VALIDATION", "CONSTRAINT");
                    var description = TryGetCsvField(csv, headers, "DESCRIPTION", "DESC");

                    profile.Fields.Add(new TargetFieldMetadata
                    {
                        FieldName = fieldName.Trim(),
                        Datatype = datatype.Trim(),
                        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim(),
                        Rule = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim(),
                        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                        IsRequired = rule?.Contains("Must", StringComparison.OrdinalIgnoreCase) == true,
                        FieldLength = ParseFieldLength(datatype, format),
                        ColumnOrder = order++
                    });
                }
            }
        }

        private static string? TryGetCsvField(CsvReader csv, string[] headers, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var match = headers.FirstOrDefault(h => h.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    try { return csv.GetField(match); } catch { }
                }
            }
            return null;
        }

        private async Task ExtractFromExcelAsync(string jobId, string filePath, TargetMetadataProfile profile)
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
            });

            if (dataSet.Tables.Count == 0)
            {
                _logger.LogWarning("No tables found in Excel file. JobId={JobId} FilePath={FilePath}", jobId, filePath);
                return;
            }

            // Prefer a sheet named "Target Schema" (generated by TargetSchemaGeneratorService).
            // Fall back to any sheet whose first column header contains "TARGET FIELDS" or "FIELD".
            // Last resort: use Tables[0] (handles plain single-sheet uploads).
            System.Data.DataTable? table = null;
            foreach (System.Data.DataTable candidate in dataSet.Tables)
            {
                var firstName = candidate.Columns.Count > 0 ? candidate.Columns[0].ColumnName : string.Empty;
                if (candidate.TableName.Equals("Target Schema", StringComparison.OrdinalIgnoreCase) ||
                    candidate.TableName.Equals("TargetSchema", StringComparison.OrdinalIgnoreCase) ||
                    firstName.Contains("TARGET", StringComparison.OrdinalIgnoreCase) ||
                    firstName.Contains("FIELD", StringComparison.OrdinalIgnoreCase))
                {
                    table = candidate;
                    break;
                }
            }
            table ??= dataSet.Tables[0];

            _logger.LogInformation("Parsing Excel table. JobId={JobId} Sheet={Sheet} Columns={Cols} Rows={Rows}",
                jobId, table.TableName, table.Columns.Count, table.Rows.Count);

            int fieldNameCol = FindColumnIndex(table, "TARGET FIELDS", "FIELD", "FIELD NAME", "COLUMN");
            int datatypeCol = FindColumnIndex(table, "DATA TYPE", "DATATYPE", "TYPE");
            int formatCol = FindColumnIndex(table, "VALUE/FORMAT", "FORMAT", "VALUE");
            int ruleCol = FindColumnIndex(table, "RULE", "VALIDATION", "CONSTRAINT");
            int descCol = FindColumnIndex(table, "DESCRIPTION", "DESC");

            for (int i = 0; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                var fieldName = GetCellValue(row, fieldNameCol);
                if (string.IsNullOrWhiteSpace(fieldName)) continue;

                var datatype = GetCellValue(row, datatypeCol);
                var format = GetCellValue(row, formatCol);
                var rule = GetCellValue(row, ruleCol);
                var description = GetCellValue(row, descCol);

                string? hardcodedValue = null;
                if (!string.IsNullOrWhiteSpace(format) && format.Length <= 10 &&
                    format == format.ToUpperInvariant() && !format.Contains('/') && !format.Contains('Y'))
                {
                    hardcodedValue = format;
                }

                var field = new TargetFieldMetadata
                {
                    FieldName = fieldName.Trim(),
                    Datatype = datatype?.Trim() ?? "string",
                    Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim(),
                    Rule = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim(),
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    HardcodedValue = hardcodedValue,
                    IsRequired = rule?.Contains("Must", StringComparison.OrdinalIgnoreCase) == true,
                    FieldLength = ParseFieldLength(datatype, format),
                    ColumnOrder = i
                };

                profile.Fields.Add(field);
                _logger.LogDebug("Target field extracted from Excel. FieldName={FieldName} Datatype={Datatype}",
                    fieldName, datatype);
            }
        }

        public async Task PersistMetadataProfileAsync(string jobId, TargetMetadataProfile profile)
        {
            await _artifactService.PersistArtifactAsync(jobId, profile, ArtifactType.TargetMetadataProfile, "target_metadata_profile.json");
        }

        private static int FindColumnIndex(DataTable table, params string[] candidates)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var colName = table.Columns[i].ColumnName.Trim().ToUpperInvariant();
                if (candidates.Any(c => colName.Contains(c, StringComparison.OrdinalIgnoreCase)))
                    return i;
            }
            return -1;
        }

        private static string? GetCellValue(DataRow row, int colIndex)
        {
            if (colIndex < 0 || colIndex >= row.Table.Columns.Count) return null;
            return row.IsNull(colIndex) ? null : row[colIndex]?.ToString();
        }

        private static int? ParseFieldLength(string? datatype, string? format = null)
        {
            var normalizedDatatype = datatype?.Trim() ?? string.Empty;
            var normalizedFormat = format?.Trim().Trim('"', '\'') ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(normalizedDatatype))
            {
                var match = System.Text.RegularExpressions.Regex.Match(normalizedDatatype, @"\((\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var len))
                    return len;
            }

            // Date datatypes often declare length only in the format column, e.g. YYYY/MM/DD -> 10.
            if (normalizedDatatype.Contains("DATE", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(normalizedFormat))
                    return normalizedFormat.Length;

                return 10;
            }

            // If datatype is missing but format is clearly date-like, infer from format length.
            if (!string.IsNullOrWhiteSpace(normalizedFormat) &&
                normalizedFormat.Contains('Y', StringComparison.OrdinalIgnoreCase) &&
                normalizedFormat.Contains('M', StringComparison.OrdinalIgnoreCase) &&
                normalizedFormat.Contains('D', StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFormat.Length;
            }

            return null;
        }
    }
}
