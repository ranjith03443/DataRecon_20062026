using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class RelationshipResolutionService : IRelationshipResolutionService
    {
        private readonly ILogger<RelationshipResolutionService> _logger;
        private readonly IArtifactPersistenceService _artifactService;

        private static readonly HashSet<string> CommonJoinKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "CUSTOMER_ID", "CUST_ID", "CLIENT_ID", "ACCOUNT_ID", "ACCT_ID",
            "ACCT_NUM", "CUST_NUM", "CUSTOMER_NO", "ACCOUNT_NO",
            "LOAN_ID", "LOAN_NO", "CONTRACT_NO", "POLICY_ID", "EMPLOYEE_ID",
            "ORDER_ID", "TRANSACTION_ID", "RECORD_ID", "ID"
        };

        public RelationshipResolutionService(
            ILogger<RelationshipResolutionService> logger,
            IArtifactPersistenceService artifactService)
        {
            _logger = logger;
            _artifactService = artifactService;
        }

        public async Task<CanonicalMappingConfig> ResolveRelationshipsAsync(
            string jobId,
            IEnumerable<SourceSchemaProfile> sourceProfiles)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting relationship resolution. JobId={JobId}", jobId);

            var config = new CanonicalMappingConfig { JobId = jobId, GeneratedAt = DateTime.UtcNow };
            var profileList = sourceProfiles.ToList();

            for (int i = 0; i < profileList.Count; i++)
            {
                for (int j = i + 1; j < profileList.Count; j++)
                {
                    var left = profileList[i];
                    var right = profileList[j];

                    var relationships = DetectRelationships(left, right);
                    config.Relationships.AddRange(relationships);

                    foreach (var rel in relationships)
                    {
                        _logger.LogInformation(
                            "Relationship detected. JobId={JobId} Left={Left}.{LeftField} Right={Right}.{RightField} Type={Type}",
                            jobId, rel.LeftDataset, rel.LeftField, rel.RightDataset, rel.RightField, rel.RelationshipType);
                    }
                }
            }

            sw.Stop();
            _logger.LogInformation("Relationship resolution completed. JobId={JobId} RelationshipCount={Count} Duration={Duration}ms",
                jobId, config.Relationships.Count, sw.ElapsedMilliseconds);

            await PersistCanonicalMappingConfigAsync(jobId, config);
            return config;
        }

        public async Task PersistCanonicalMappingConfigAsync(string jobId, CanonicalMappingConfig config)
        {
            await _artifactService.PersistArtifactAsync(jobId, config, ArtifactType.CanonicalMappingConfig, "canonical_mapping_config.json");
        }

        private IEnumerable<DatasetRelationship> DetectRelationships(
            SourceSchemaProfile left, SourceSchemaProfile right)
        {
            var relationships = new List<DatasetRelationship>();

            var leftFields = left.Fields.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rightFields = right.Fields.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Find common fields that are likely join keys
            var commonFields = leftFields.Intersect(rightFields, StringComparer.OrdinalIgnoreCase);

            foreach (var field in commonFields)
            {
                var isLikelyKey = CommonJoinKeys.Contains(field) ||
                                  field.EndsWith("_ID", StringComparison.OrdinalIgnoreCase) ||
                                  field.EndsWith("_NO", StringComparison.OrdinalIgnoreCase) ||
                                  field.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) ||
                                  field.EndsWith("_NUM", StringComparison.OrdinalIgnoreCase);

                if (isLikelyKey)
                {
                    // Check if left field is unique (potential primary key)
                    var leftField = left.Fields.First(f => f.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase));
                    var joinType = leftField.IsUnique ? RelationshipType.INNER_JOIN : RelationshipType.LEFT_JOIN;

                    relationships.Add(new DatasetRelationship
                    {
                        LeftDataset = left.DatasetId,
                        LeftField = field,
                        RightDataset = right.DatasetId,
                        RightField = field,
                        RelationshipType = joinType,
                        ConfidenceScore = isLikelyKey ? 0.95 : 0.70
                    });
                }
            }

            return relationships;
        }
    }
}
