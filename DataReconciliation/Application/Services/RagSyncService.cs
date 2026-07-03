using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using Newtonsoft.Json;
using System.Text;

namespace DataReconciliation.Application.Services
{
    public class RagSyncService : IRagSyncService
    {
        private readonly IHistoricalMappingRepository _repo;
        private readonly HttpClient _httpClient;
        private readonly ILogger<RagSyncService> _logger;

        public RagSyncService(
            IHistoricalMappingRepository repo,
            IHttpClientFactory httpClientFactory,
            ILogger<RagSyncService> logger)
        {
            _repo = repo;
            _httpClient = httpClientFactory.CreateClient("PythonService");
            _logger = logger;
        }

        public async Task<int> SyncHistoricalMappingsAsync(IEnumerable<HistoricalMappingEntry> entries)
        {
            var list = entries.Where(e => e.IsActive).ToList();
            if (list.Count == 0) return 0;

            var documents = list.Select(BuildDocument).ToList();
            var ids = list.Select(e =>
                $"hist_{e.TargetField.ToLowerInvariant().Replace(" ", "_").Replace(".", "_")}"
            ).ToList();
            var metadatas = list.Select(e => new Dictionary<string, string>
            {
                ["target_field"]  = e.TargetField,
                ["source_field"]  = e.SourceField,
                ["confidence"]    = e.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                ["match_source"]  = e.MatchSource ?? string.Empty,
            }).ToList();

            var payload = new { collection = "historical_mappings", documents, ids, metadatas };
            var content = new StringContent(
                JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync("/api/vector-store/index", content);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "RAG sync: {Count} historical mappings indexed into ChromaDB.", list.Count);
                    return list.Count;
                }
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "RAG sync returned {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(200, body.Length)]);
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RAG sync failed (non-critical — mappings saved to SQLite).");
                return 0;
            }
        }

        public async Task<int> SyncAllActiveHistoricalMappingsAsync()
        {
            var entries = await _repo.GetAllAsync(activeOnly: true);
            return await SyncHistoricalMappingsAsync(entries);
        }

        private static string BuildDocument(HistoricalMappingEntry e)
        {
            var parts = new List<string> { $"SOURCE: {e.SourceField}" };
            if (!string.IsNullOrWhiteSpace(e.SourceDataset))
                parts.Add($"(dataset: {e.SourceDataset})");
            parts.Add($"maps to TARGET: {e.TargetField}.");
            if (!string.IsNullOrWhiteSpace(e.TransformationSummary))
                parts.Add($"Transformation: {e.TransformationSummary}.");
            parts.Add($"Confidence: {e.Confidence:F2}.");
            if (!string.IsNullOrWhiteSpace(e.MatchSource))
                parts.Add($"Match source: {e.MatchSource}.");
            if (!string.IsNullOrWhiteSpace(e.Notes))
                parts.Add($"Notes: {e.Notes}.");
            return string.Join(" ", parts);
        }
    }
}
