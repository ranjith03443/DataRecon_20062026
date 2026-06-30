using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Transformations;

namespace DataReconciliation.Application.Transformations
{
    public class TransformationRuleContractBuilder
    {
        public TransformationRulesArtifact Build(string jobId, FinalMappingConfig mappingConfig)
        {
            var artifact = new TransformationRulesArtifact
            {
                JobId = jobId,
                ArtifactVersion = "1.0"
            };

            foreach (var mapping in mappingConfig.Mappings)
            {
                if (mapping.SourceField == "[UNRESOLVED]")
                    continue;

                if (mapping.Transformations == null || !mapping.Transformations.Any())
                {
                    var concatSources = mapping.AdditionalSources?
                        .Where(a => string.Equals(a.MergeRule, "CONCAT", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (concatSources?.Any() == true)
                    {
                        var prefix = string.IsNullOrWhiteSpace(mapping.SourceDataset)
                            ? string.Empty : $"{mapping.SourceDataset}.";
                        var allFields = new List<string> { $"{prefix}{mapping.SourceField}" };
                        allFields.AddRange(concatSources.Select(s => $"{prefix}{s.SourceField}"));

                        artifact.Rules.Add(new TransformationRuleContract
                        {
                            SourceField = mapping.SourceField,
                            TargetField = mapping.TargetField,
                            Operation = "CONCAT",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["fields"] = string.Join(",", allFields),
                                ["delimiter"] = " "
                            },
                            Confidence = mapping.Confidence,
                            GeneratedBy = mapping.MatchSource == "AI Inference" ? "AI_RULE_INFERENCE" : "RULE_ENGINE"
                        });
                    }
                    else
                    {
                        artifact.Rules.Add(new TransformationRuleContract
                        {
                            SourceField = mapping.SourceField,
                            TargetField = mapping.TargetField,
                            Operation = "DEFAULT_VALUE",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            Confidence = mapping.Confidence,
                            GeneratedBy = mapping.MatchSource == "AI Inference" ? "AI_RULE_INFERENCE" : "RULE_ENGINE"
                        });
                    }
                    continue;
                }

                foreach (var tr in mapping.Transformations)
                {
                    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrWhiteSpace(tr.Format)) parameters["outputFormat"] = tr.Format;
                    if (!string.IsNullOrWhiteSpace(tr.DefaultValue)) parameters["value"] = tr.DefaultValue;
                    if (!string.IsNullOrWhiteSpace(tr.MaskPattern)) parameters["pattern"] = tr.MaskPattern;
                    if (tr.FixedWidth.HasValue) parameters["width"] = tr.FixedWidth.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(tr.PadCharacter)) parameters["padChar"] = tr.PadCharacter;
                    if (!string.IsNullOrWhiteSpace(tr.Alignment)) parameters["alignment"] = tr.Alignment;

                    if (tr.Rules != null)
                    {
                        foreach (var kv in tr.Rules)
                            parameters[$"map:{kv.Key}"] = kv.Value;
                    }

                    if (tr.AIParameters != null)
                    {
                        foreach (var kv in tr.AIParameters)
                            parameters[kv.Key] = kv.Value;
                    }

                    artifact.Rules.Add(new TransformationRuleContract
                    {
                        SourceField = mapping.SourceField,
                        TargetField = mapping.TargetField,
                        Operation = string.IsNullOrWhiteSpace(tr.Operation) ? "DEFAULT_VALUE" : tr.Operation,
                        Parameters = parameters,
                        Confidence = mapping.Confidence,
                        GeneratedBy = mapping.MatchSource == "AI Inference" ? "AI_RULE_INFERENCE" : "RULE_ENGINE"
                    });
                }
            }

            return artifact;
        }
    }
}
