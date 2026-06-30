namespace DataReconciliation.Application.DTOs
{
    public class AISettingsDto
    {
        // ── Provider selection ────────────────────────────────────────────────
        public string LlmProvider { get; set; } = "claude";
        public string EmbeddingProvider { get; set; } = "azure_openai";

        // ── Anthropic Claude ──────────────────────────────────────────────────
        public string? AnthropicApiKey { get; set; }

        // ── OpenAI ────────────────────────────────────────────────────────────
        public string? OpenAiApiKey { get; set; }

        // ── Azure OpenAI ──────────────────────────────────────────────────────
        public string? AzureOpenAiEndpoint { get; set; }
        public string? AzureOpenAiApiKey { get; set; }
        public string AzureOpenAiApiVersion { get; set; } = "2024-02-15-preview";
        public string? AzureOpenAiDeploymentName { get; set; }
        public string? AzureEmbeddingDeployment { get; set; }

        // ── Ollama ────────────────────────────────────────────────────────────
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaDefaultModel { get; set; } = "llama3";

        // ── Gemini ────────────────────────────────────────────────────────────
        public string? GeminiApiKey { get; set; }

        // ── Mistral AI ────────────────────────────────────────────────────────
        public string? MistralApiKey { get; set; }
        public string MistralBaseUrl { get; set; } = "https://api.mistral.ai/v1";

        // ── Models per task ───────────────────────────────────────────────────
        public string SemanticMappingModel { get; set; } = "claude-haiku-4-5-20251001";
        public string SchemaEnrichmentModel { get; set; } = "claude-haiku-4-5-20251001";
        public string RuleInferenceModel { get; set; } = "claude-haiku-4-5-20251001";
        public string MainframeAgentModel { get; set; } = "claude-haiku-4-5-20251001";
        public double SemanticMappingTemperature { get; set; } = 0.1;
        public double SchemaEnrichmentTemperature { get; set; } = 0.1;
        public double RuleInferenceTemperature { get; set; } = 0.0;
        public int SemanticMappingMaxTokens { get; set; } = 2000;
        public int SchemaEnrichmentMaxTokens { get; set; } = 1000;
        public int RuleInferenceMaxTokens { get; set; } = 1000;

        // ── Embedding model ───────────────────────────────────────────────────
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
        public int EmbeddingDimensions { get; set; } = 1536;

        // ── Python service connection ─────────────────────────────────────────
        public string PythonServiceUrl { get; set; } = "http://localhost:8000";

        // ── Confidence thresholds ─────────────────────────────────────────────
        public double AutoAcceptThreshold { get; set; } = 0.90;
        public double WarningThreshold { get; set; } = 0.70;
    }

    public class AISettingsSaveResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool PythonEnvSynced { get; set; }
    }
}
