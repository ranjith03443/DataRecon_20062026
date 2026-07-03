using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Entities;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class HistoricalMappingService : IHistoricalMappingService
    {
        private readonly IHistoricalMappingRepository _repo;
        private readonly ILogger<HistoricalMappingService> _logger;

        public HistoricalMappingService(IHistoricalMappingRepository repo, ILogger<HistoricalMappingService> logger)
        {
            _repo   = repo;
            _logger = logger;
        }

        public async Task ExportFromFinalMappingAsync(
            string jobId,
            FinalMappingConfig finalMapping,
            string reviewerName)
        {
            int exported = 0;
            foreach (var m in finalMapping.Mappings.Where(m =>
                m.Status != MappingStatus.UNRESOLVED &&
                !string.IsNullOrWhiteSpace(m.SourceField) &&
                m.SourceField != "[UNRESOLVED]"))
            {
                try
                {
                    var transformSummary = BuildTransformationSummary(m);
                    await _repo.UpsertAsync(new HistoricalMappingEntry
                    {
                        TargetField           = m.TargetField,
                        SourceField           = m.SourceField ?? string.Empty,
                        SourceDataset         = m.SourceDataset,
                        Confidence            = m.Confidence,
                        MatchSource           = m.MatchSource,
                        TransformationSummary = transformSummary,
                        LastJobId             = jobId,
                        LastReviewerName      = reviewerName,
                        LastUsedAt            = DateTime.UtcNow,
                        CreatedAt             = DateTime.UtcNow
                    });
                    exported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed exporting mapping to history. JobId={JobId} TargetField={Field}", jobId, m.TargetField);
                }
            }
            _logger.LogInformation("Exported {Count} confirmed mappings to history. JobId={JobId}", exported, jobId);
        }

        public async Task<IEnumerable<HistoricalMappingEntry>> GetAllAsync(bool activeOnly = false, string? search = null)
        {
            var all = await _repo.GetAllAsync(activeOnly);
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim();
                all = all.Where(e =>
                    e.TargetField.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.SourceField.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (e.SourceDataset?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
                    (e.Notes?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
            }
            return all;
        }

        public async Task<HistoricalMappingEntry?> GetByIdAsync(int id) =>
            await _repo.GetByIdAsync(id);

        public async Task<HistoricalMappingEntry> UpdateAsync(int id, string? sourceField, string? notes, bool isActive)
        {
            var entry = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException($"Historical mapping {id} not found.");

            if (!string.IsNullOrWhiteSpace(sourceField))
                entry.SourceField = sourceField.Trim();
            entry.Notes    = notes?.Trim();
            entry.IsActive = isActive;

            return await _repo.UpdateAsync(entry);
        }

        public async Task DeleteAsync(int id) => await _repo.DeleteAsync(id);

        public async Task<int> GetTotalCountAsync() =>
            (await _repo.GetAllAsync()).Count();

        private static string? BuildTransformationSummary(FinalMapping m)
        {
            if (m.Transformations == null || !m.Transformations.Any())
                return null;

            var ops = m.Transformations.Select(t => t.Operation).Where(o => !string.IsNullOrWhiteSpace(o));
            return string.Join(" → ", ops);
        }
    }
}
