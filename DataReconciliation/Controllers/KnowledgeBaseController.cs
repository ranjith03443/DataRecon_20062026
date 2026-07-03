using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace DataReconciliation.Controllers
{
    public class KnowledgeBaseController : Controller
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<KnowledgeBaseController> _logger;

        public KnowledgeBaseController(
            IHttpClientFactory httpClientFactory,
            ILogger<KnowledgeBaseController> logger)
        {
            _httpClient = httpClientFactory.CreateClient("PythonService");
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var status = await FetchStatusAsync();
            return View(status);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Build()
        {
            try
            {
                var response = await _httpClient.PostAsync("/api/rag/build", null);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    dynamic? result = JsonConvert.DeserializeObject<dynamic>(body);
                    int total = result?.total_indexed ?? 0;
                    TempData["Success"] = $"Knowledge Base built — {total} documents indexed successfully.";
                    _logger.LogInformation("Knowledge Base build triggered from UI. TotalIndexed={Total}", total);
                }
                else
                {
                    TempData["Error"] = $"Knowledge Base build failed (HTTP {(int)response.StatusCode}). Is the Python AI service running?";
                    _logger.LogWarning("Knowledge Base build returned {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(300, body.Length)]);
                }
            }
            catch (HttpRequestException ex)
            {
                TempData["Error"] = "Cannot reach the Python AI service. Start it and try again.";
                _logger.LogError(ex, "Knowledge Base build request failed — Python service unreachable.");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Unexpected error: {ex.Message[..Math.Min(150, ex.Message.Length)]}";
                _logger.LogError(ex, "Knowledge Base build unexpected error.");
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task<Dictionary<string, object>?> FetchStatusAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/rag/status");
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            }
            catch
            {
                return null;
            }
        }
    }
}
