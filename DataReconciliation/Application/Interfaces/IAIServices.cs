using DataReconciliation.Domain.Models;

namespace DataReconciliation.Application.Interfaces
{
    public interface IAIInferenceService
    {
        Task<AIInferenceResponse> InferAsync(AIInferenceRequest request);
        Task<bool> IsAvailableAsync();
    }

    public interface IAIResponseValidator
    {
        bool ValidateStructure(string jsonResponse);
        bool ValidateConfidenceScore(double score);
        T? DeserializeAndValidate<T>(string jsonResponse) where T : class;
    }
}
