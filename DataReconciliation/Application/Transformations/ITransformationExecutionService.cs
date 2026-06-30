using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Application.Transformations
{
    public interface ITransformationExecutionService
    {
        Task<TransformationExecutionResult> ExecuteAsync(
            string jobId,
            IEnumerable<CanonicalRecord> canonicalRecords,
            FinalMappingConfig mappingConfig,
            TargetMetadataProfile targetProfile);
    }
}
