using DataReconciliation.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataReconciliation.AI.Validators
{
    public class AIResponseValidator : IAIResponseValidator
    {
        private readonly ILogger<AIResponseValidator> _logger;

        public AIResponseValidator(ILogger<AIResponseValidator> logger)
        {
            _logger = logger;
        }

        public bool ValidateStructure(string jsonResponse)
        {
            if (string.IsNullOrWhiteSpace(jsonResponse)) return false;

            try
            {
                var obj = JToken.Parse(jsonResponse);
                return obj is JObject || obj is JArray;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("AI response JSON structure invalid: {Error}", ex.Message);
                return false;
            }
        }

        public bool ValidateConfidenceScore(double score) => score is >= 0.0 and <= 1.0;

        public T? DeserializeAndValidate<T>(string jsonResponse) where T : class
        {
            if (!ValidateStructure(jsonResponse)) return null;

            try
            {
                var result = JsonConvert.DeserializeObject<T>(jsonResponse);
                if (result == null)
                {
                    _logger.LogWarning("Deserialization returned null for type {Type}", typeof(T).Name);
                    return null;
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize AI response to {Type}", typeof(T).Name);
                return null;
            }
        }
    }
}
