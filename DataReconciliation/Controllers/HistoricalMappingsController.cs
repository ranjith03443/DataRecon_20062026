using DataReconciliation.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class HistoricalMappingsController : Controller
    {
        private readonly IHistoricalMappingService _svc;
        private readonly IRagSyncService? _ragSync;
        private readonly ILogger<HistoricalMappingsController> _logger;

        public HistoricalMappingsController(
            IHistoricalMappingService svc,
            ILogger<HistoricalMappingsController> logger,
            IRagSyncService? ragSync = null)
        {
            _svc     = svc;
            _ragSync = ragSync;
            _logger  = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search, bool activeOnly = false)
        {
            var entries = await _svc.GetAllAsync(activeOnly, search);
            ViewBag.Search     = search ?? string.Empty;
            ViewBag.ActiveOnly = activeOnly;
            ViewBag.Total      = await _svc.GetTotalCountAsync();
            return View(entries.ToList());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(
            [FromQuery] int id,
            [FromBody] UpdateHistoricalMappingRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body is required." });

            try
            {
                var updated = await _svc.UpdateAsync(id, request.SourceField, request.Notes, request.IsActive);
                _logger.LogInformation("Historical mapping updated. Id={Id} TargetField={Field}", id, updated.TargetField);
                return Ok(new
                {
                    id          = updated.Id,
                    targetField = updated.TargetField,
                    sourceField = updated.SourceField,
                    notes       = updated.Notes,
                    isActive    = updated.IsActive
                });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Mapping {id} not found." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed updating historical mapping. Id={Id}", id);
                return StatusCode(500, new { error = "Update failed." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var entry = await _svc.GetByIdAsync(id);
            if (entry == null) return NotFound(new { error = "Not found." });

            var updated = await _svc.UpdateAsync(id, entry.SourceField, entry.Notes, !entry.IsActive);
            return Ok(new { id = updated.Id, isActive = updated.IsActive });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            await _svc.DeleteAsync(id);
            _logger.LogInformation("Historical mapping deleted. Id={Id}", id);
            TempData["Success"] = "Mapping entry deleted.";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Manually syncs all active historical mappings into the ChromaDB RAG vector store.
        /// Useful when the Python service was restarted (ChromaDB data is persistent but this
        /// ensures the index is up-to-date).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncToRag()
        {
            if (_ragSync == null)
            {
                TempData["Error"] = "Knowledge Base sync is not configured. Ensure the Python service is registered.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var synced = await _ragSync.SyncAllActiveHistoricalMappingsAsync();
                _logger.LogInformation("Manual RAG sync triggered. Synced={Count}", synced);
                TempData["Success"] = $"Synced {synced} active mappings to the Knowledge Base.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual RAG sync failed.");
                TempData["Error"] = "Knowledge Base sync failed — check that the Python AI service is running.";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Downloads active mappings as a CSV that can be uploaded as a HISTORICAL_MAPPINGS
        /// dataset when creating a new workflow. The user must upload it manually — it is
        /// never injected automatically.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> DownloadCsv()
        {
            var entries = (await _svc.GetAllAsync(activeOnly: true)).ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SOURCE_FIELD,TARGET_FIELD,TRANSFORMATION,CONFIDENCE,NOTES");

            foreach (var e in entries)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(e.SourceField),
                    CsvEscape(e.TargetField),
                    CsvEscape(string.IsNullOrWhiteSpace(e.TransformationSummary) ? "DIRECT" : e.TransformationSummary),
                    e.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    CsvEscape(e.Notes ?? string.Empty)));
            }

            _logger.LogInformation("Historical mappings CSV downloaded. ActiveEntries={Count}", entries.Count);
            return File(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
                "text/csv",
                $"historical_mappings_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        private static string CsvEscape(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }

    public class UpdateHistoricalMappingRequest
    {
        public string? SourceField { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; }
    }
}
