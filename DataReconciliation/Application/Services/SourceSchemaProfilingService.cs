using CsvHelper;
using CsvHelper.Configuration;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataReconciliation.Application.Services
{
    public class SourceSchemaProfilingService : ISourceSchemaProfilingService
    {
        private readonly ILogger<SourceSchemaProfilingService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        public SourceSchemaProfilingService(
            ILogger<SourceSchemaProfilingService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
        }

        public async Task<SourceSchemaProfile> ProfileSourceFileAsync(string jobId, string datasetId, string filePath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting source schema profiling. JobId={JobId} DatasetId={DatasetId} FilePath={FilePath}",
                jobId, datasetId, filePath);

            var profile = new SourceSchemaProfile
            {
                JobId = jobId,
                Dataset = Path.GetFileName(filePath),
                DatasetId = datasetId,
                ProfiledAt = DateTime.UtcNow
            };

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

            // Initialize field profiles
            var fieldData = headers.Select((h, i) => new
            {
                Header = h,
                Index = i,
                Values = new List<string>()
            }).ToList();

            int maxSampleSize = 1000;
            int rowCount = 0;

            while (await csv.ReadAsync() && rowCount < maxSampleSize)
            {
                rowCount++;
                for (int i = 0; i < headers.Length; i++)
                {
                    var value = csv.GetField(i) ?? string.Empty;
                    fieldData[i].Values.Add(value);
                }
            }

            foreach (var fd in fieldData)
            {
                var nonEmpty = fd.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                var distinctVals = fd.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToList();
                var fieldProfile = new SourceFieldProfile
                {
                    FieldName = fd.Header,
                    ColumnIndex = fd.Index,
                    Nullable = fd.Values.Any(v => string.IsNullOrWhiteSpace(v)),
                    MaxLength = fd.Values.Count > 0 ? fd.Values.Max(v => v.Length) : 0,
                    IsUnique = fd.Values.Distinct().Count() == fd.Values.Count && fd.Values.Count > 0,
                    SampleValues = nonEmpty.Take(5).ToList(),
                    DistinctValues = distinctVals.Take(50).ToList(), // cap at 50 for storage
                    DistinctValueCount = distinctVals.Count,
                    Datatype = InferDatatype(nonEmpty),
                    FieldPattern = InferPattern(nonEmpty),
                    PossibleMeaning = InferPossibleMeaning(fd.Header)
                };

                profile.Fields.Add(fieldProfile);
                _logger.LogDebug("Field profiled. FieldName={FieldName} Datatype={Datatype} Nullable={Nullable}",
                    fieldProfile.FieldName, fieldProfile.Datatype, fieldProfile.Nullable);
            }

            sw.Stop();
            _logger.LogInformation("Source schema profiling completed. JobId={JobId} DatasetId={DatasetId} FieldCount={Count} Duration={Duration}ms",
                jobId, datasetId, profile.Fields.Count, sw.ElapsedMilliseconds);

            return profile;
        }

        public async Task<IEnumerable<SourceSchemaProfile>> ProfileAllSourcesAsync(string jobId, DatasetManifest manifest)
        {
            _logger.LogInformation("Profiling all source datasets. JobId={JobId}", jobId);
            var profiles = new List<SourceSchemaProfile>();

            foreach (var dataset in manifest.Datasets.Where(d => d.DatasetRole == DatasetRole.SOURCE))
            {
                if (File.Exists(dataset.LocalPath))
                {
                    var profile = await ProfileSourceFileAsync(jobId, dataset.DatasetId, dataset.LocalPath);
                    profiles.Add(profile);
                    await PersistProfileAsync(jobId, profile);
                }
                else
                {
                    _logger.LogWarning("Source file not found. JobId={JobId} DatasetId={DatasetId} Path={Path}",
                        jobId, dataset.DatasetId, dataset.LocalPath);
                }
            }

            _logger.LogInformation("All source profiles completed. JobId={JobId} Count={Count}", jobId, profiles.Count);
            return profiles;
        }

        public async Task PersistProfileAsync(string jobId, SourceSchemaProfile profile)
        {
            var fileName = $"source_schema_profile_{profile.DatasetId}.json";
            await _artifactService.PersistArtifactAsync(jobId, profile, ArtifactType.SourceSchemaProfile, fileName);
        }

        private static string InferDatatype(List<string> values)
        {
            if (!values.Any()) return "string";

            if (values.All(v => int.TryParse(v, out _))) return "integer";
            if (values.All(v => decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _))) return "decimal";
            if (values.All(v => DateTime.TryParse(v, out _))) return "datetime";
            if (values.All(v => v == "true" || v == "false" || v == "1" || v == "0")) return "boolean";
            return "string";
        }

        private static string? InferPattern(List<string> values)
        {
            if (!values.Any()) return null;

            // Date patterns
            if (values.All(v => Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}$"))) return "YYYY-MM-DD";
            if (values.All(v => Regex.IsMatch(v, @"^\d{8}$"))) return "YYYYMMDD";
            if (values.All(v => Regex.IsMatch(v, @"^\d{10}$"))) return "Numeric(10)";
            if (values.All(v => Regex.IsMatch(v, @"^\d+\.\d{2}$"))) return "Decimal(2dp)";

            return null;
        }

        private static string InferPossibleMeaning(string fieldName)
        {
            // Simple abbreviation expansion
            var expansions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ACCT", "Account" }, { "ACCT_NO", "Account Number" }, { "CUST", "Customer" },
                { "AMT", "Amount" }, { "DT", "Date" }, { "NO", "Number" }, { "ID", "Identifier" },
                { "NM", "Name" }, { "STAT", "Status" }, { "CD", "Code" }, { "DESC", "Description" },
                { "BAL", "Balance" }, { "QTY", "Quantity" }, { "PCT", "Percentage" },
                { "HDR", "Header" }, { "TRL", "Trailer" }, { "REC", "Record" },
                { "LOAN", "Loan" }, { "PMT", "Payment" }, { "INT", "Interest" }
            };

            if (expansions.TryGetValue(fieldName, out var meaning)) return meaning;

            // Convert underscored names to readable form
            var parts = fieldName.Split('_')
                .Select(p => expansions.TryGetValue(p, out var exp) ? exp : p.ToTitleCase());
            return string.Join(" ", parts);
        }
    }

    public static class StringExtensions
    {
        public static string ToTitleCase(this string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
        }
    }
}
