using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class SettingsController : Controller
    {
        private readonly IAISettingsService _settingsService;
        private readonly ILogger<SettingsController> _logger;

        public SettingsController(IAISettingsService settingsService, ILogger<SettingsController> logger)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var dto = _settingsService.Load();
            ViewBag.PythonEnvPath = _settingsService.GetPythonEnvPath();
            return View(dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(AISettingsDto dto)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.PythonEnvPath = _settingsService.GetPythonEnvPath();
                return View("Index", dto);
            }

            var result = _settingsService.Save(dto);

            if (result.Success)
            {
                var msg = result.PythonEnvSynced
                    ? "Settings saved and Python service .env updated. Restart the Python service to apply."
                    : "Settings saved. Python service .env could not be found — update it manually.";
                TempData["Success"] = msg;
            }
            else
            {
                TempData["Error"] = $"Failed to save settings: {result.Error}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> GetBudget(string? from, string? to, string? provider)
        {
            var settings = _settingsService.Load();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var qs = new List<string>();
                if (!string.IsNullOrWhiteSpace(from))     qs.Add($"from={Uri.EscapeDataString(from)}");
                if (!string.IsNullOrWhiteSpace(to))       qs.Add($"to={Uri.EscapeDataString(to)}");
                if (!string.IsNullOrWhiteSpace(provider)) qs.Add($"provider={Uri.EscapeDataString(provider)}");
                var url = $"{settings.PythonServiceUrl.TrimEnd('/')}/api/budget";
                if (qs.Count > 0) url += "?" + string.Join("&", qs);
                var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                return Content(body, "application/json");
            }
            catch (Exception ex)
            {
                return Content($"{{\"error\":\"{ex.Message}\",\"total_calls\":0,\"total_cost_usd\":0,\"total_tokens\":0,\"by_provider\":[],\"records\":[]}}", "application/json");
            }
        }

        [HttpGet]
        public async Task<IActionResult> CheckPythonHealth()
        {
            var settings = _settingsService.Load();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var response = await http.GetAsync($"{settings.PythonServiceUrl.TrimEnd('/')}/api/health");
                var body = await response.Content.ReadAsStringAsync();
                return Content(body, "application/json");
            }
            catch (Exception ex)
            {
                return Content($"{{\"status\":\"UNREACHABLE\",\"error\":\"{ex.Message}\"}}", "application/json");
            }
        }
    }
}
