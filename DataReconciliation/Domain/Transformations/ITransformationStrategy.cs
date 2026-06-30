namespace DataReconciliation.Domain.Transformations
{
    public interface ITransformationStrategy
    {
        bool CanHandle(string operation);
        string Transform(TransformationExecutionInput input, TransformationRuleContract rule);
    }
}
