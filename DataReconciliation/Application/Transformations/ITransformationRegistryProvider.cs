using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Application.Transformations
{
    public interface ITransformationRegistryProvider
    {
        Task<SupportedOperationsRegistry> GetRegistryAsync();
        bool IsSupported(string operation);
        string NormalizeOperation(string operation);
    }
}
