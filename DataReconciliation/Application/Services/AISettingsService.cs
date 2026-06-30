using DataReconciliation.Application.DTOs;
using DataReconciliation.Application.Interfaces;
using System.Text;
using System.Text.Json;

namespace DataReconciliation.Application.Services
{
    public class AISettingsService : IAISettingsService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<AISettingsService> _logger;
        private readonly string _settingsFilePath;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public AISettingsService(
            IWebHostEnvironment env,
            IConfiguration config,
            ILogger<AISettingsService> logger)
        {
            _env = env;
            _config = config;
            _logger = logger;
            _settingsFilePath = Path.Combine(_env.ContentRootPath, "config", "ai_settings.json");
        }

        public AISettingsDto Load()
        {
            if (!File.Exists(_settingsFilePath))
                return BuildFromAppSettings();

            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<AISettingsDto>(json, _jsonOpts) ?? BuildFromAppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read ai_settings.json — using appsettings defaults");
                return BuildFromAppSettings();
            }
        }

        public AISettingsSaveResult Save(AISettingsDto dto)
        {
            var result = new AISettingsSaveResult();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
                var json = JsonSerializer.Serialize(dto, _jsonOpts);
                File.WriteAllText(_settingsFilePath, json);

                result.PythonEnvSynced = TrySyncPythonEnv(dto);
                result.Success = true;
                _logger.LogInformation("AI settings saved. PythonEnvSynced={Synced}", result.PythonEnvSynced);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save AI settings");
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        public string GetPythonEnvPath() => ResolvePythonEnvPath();

        // ── Private helpers ───────────────────────────────────────────────────

        private AISettingsDto BuildFromAppSettings()
        {
            return new AISettingsDto
            {
                LlmProvider = _config["AI:Provider"] == "OpenAI" ? "openai" : "claude",
                PythonServiceUrl = _config["AI:PythonService:BaseUrl"] ?? "http://localhost:8000",
                AutoAcceptThreshold = double.TryParse(_config["AI:ConfidenceThreshold:AutoAccept"], out var a) ? a : 0.90,
                WarningThreshold = double.TryParse(_config["AI:ConfidenceThreshold:Warning"], out var w) ? w : 0.70,
            };
        }

        private bool TrySyncPythonEnv(AISettingsDto dto)
        {
            var envPath = ResolvePythonEnvPath();
            if (string.IsNullOrWhiteSpace(envPath))
            {
                _logger.LogWarning("Python .env path not found — skipping sync");
                return false;
            }

            try
            {
                var lines = BuildEnvFileLines(dto);
                File.WriteAllText(envPath, lines, Encoding.UTF8);
                _logger.LogInformation("Python .env synced at {Path}", envPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write Python .env at {Path}", envPath);
                return false;
            }
        }

        private string ResolvePythonEnvPath()
        {
            // Configurable override first
            var configured = _config["AI:PythonEnvPath"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var full = Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(_env.ContentRootPath, configured);
                return full;
            }

            // Auto-detect: walk up from content root to find AISemanticMapping/.env
            var dir = new DirectoryInfo(_env.ContentRootPath);
            for (int i = 0; i < 4; i++)
            {
                if (dir == null) break;
                var candidate = Path.Combine(dir.FullName, "AISemanticMapping", ".env");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            return string.Empty;
        }

        private static string BuildEnvFileLines(AISettingsDto dto)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Auto-generated by DataRecon Settings Page");
            sb.AppendLine($"LLM_PROVIDER={dto.LlmProvider}");
            sb.AppendLine($"EMBEDDING_PROVIDER={dto.EmbeddingProvider}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(dto.AnthropicApiKey))
                sb.AppendLine($"ANTHROPIC_API_KEY={dto.AnthropicApiKey}");

            if (!string.IsNullOrWhiteSpace(dto.OpenAiApiKey))
                sb.AppendLine($"OPENAI_API_KEY={dto.OpenAiApiKey}");

            if (!string.IsNullOrWhiteSpace(dto.GeminiApiKey))
                sb.AppendLine($"GEMINI_API_KEY={dto.GeminiApiKey}");

            if (!string.IsNullOrWhiteSpace(dto.MistralApiKey))
                sb.AppendLine($"MISTRAL_API_KEY={dto.MistralApiKey}");
            if (!string.IsNullOrWhiteSpace(dto.MistralBaseUrl))
                sb.AppendLine($"MISTRAL_BASE_URL={dto.MistralBaseUrl}");

            if (!string.IsNullOrWhiteSpace(dto.AzureOpenAiEndpoint))
                sb.AppendLine($"AZURE_OPENAI_ENDPOINT={dto.AzureOpenAiEndpoint}");
            if (!string.IsNullOrWhiteSpace(dto.AzureOpenAiApiKey))
                sb.AppendLine($"AZURE_OPENAI_API_KEY={dto.AzureOpenAiApiKey}");
            if (!string.IsNullOrWhiteSpace(dto.AzureOpenAiApiVersion))
                sb.AppendLine($"AZURE_OPENAI_API_VERSION={dto.AzureOpenAiApiVersion}");
            if (!string.IsNullOrWhiteSpace(dto.AzureOpenAiDeploymentName))
                sb.AppendLine($"AZURE_OPENAI_DEPLOYMENT_NAME={dto.AzureOpenAiDeploymentName}");
            if (!string.IsNullOrWhiteSpace(dto.AzureEmbeddingDeployment))
                sb.AppendLine($"AZURE_OPENAI_EMBEDDING_DEPLOYMENT={dto.AzureEmbeddingDeployment}");

            if (!string.IsNullOrWhiteSpace(dto.OllamaBaseUrl))
                sb.AppendLine($"OLLAMA_BASE_URL={dto.OllamaBaseUrl}");

            sb.AppendLine();
            sb.AppendLine($"ENVIRONMENT=dev");
            sb.AppendLine($"LOG_LEVEL=INFO");
            sb.AppendLine($"CONFIG_PATH=config");
            sb.AppendLine($"PROMPTS_PATH=prompts");

            return sb.ToString();
        }
    }
}
