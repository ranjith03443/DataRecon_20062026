using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Application.Transformations
{
    public interface ITransformationStrategyFactory
    {
        ITransformationStrategy? Resolve(string operation);
    }
}
