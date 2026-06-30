using DataReconciliation.Application.Transformations;
using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Infrastructure.Transformations
{
    public class TransformationStrategyFactory : ITransformationStrategyFactory
    {
        private readonly IEnumerable<ITransformationStrategy> _strategies;

        public TransformationStrategyFactory(IEnumerable<ITransformationStrategy> strategies)
        {
            _strategies = strategies;
        }

        public ITransformationStrategy? Resolve(string operation)
            => _strategies.FirstOrDefault(s => s.CanHandle(operation));
    }
}
