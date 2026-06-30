using DataReconciliation.Domain.Enums;

namespace DataReconciliation.Domain.Models
{
    // ─── Workflow Context ───────────────────────────────────────────────────────
    public class WorkflowContext
    {
        public string JobId { get; set; } = string.Empty;
        public int WorkflowDbId { get; set; }  // integer PK of WorkflowJob row (for FK relationships)
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
        public WorkflowStatus WorkflowStatus { get; set; } = WorkflowStatus.Pending;
        public WorkflowStep CurrentStep { get; set; } = WorkflowStep.DatasetRegistration;
        public Dictionary<string, string> ArtifactPaths { get; set; } = new();
        public Dictionary<string, double> ConfidenceScores { get; set; } = new();
        public Dictionary<string, DateTime> Timestamps { get; set; } = new();
        public List<string> ErrorDetails { get; set; } = new();
        public bool IsReplay { get; set; } = false;
        public string WorkflowBasePath { get; set; } = string.Empty;
    }

    // ─── Dataset Manifest ──────────────────────────────────────────────────────
    public class DatasetManifest
    {
        public string JobId { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<DatasetRegistration> Datasets { get; set; } = new();
        /// <summary>Optional path to ingested source parameter file (.xlsx).</summary>
        public string? SourceParameterFilePath { get; set; }
        /// <summary>Optional path to ingested target parameter file (.xlsx).</summary>
        public string? TargetParameterFilePath { get; set; }
        /// <summary>Optional path to ingested value-mapping seed Excel file (.xlsx/.xls).</summary>
        public string? ValueMappingsExcelFilePath { get; set; }
    }

    public class DatasetRegistration
    {
        public string DatasetId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DatasetRole DatasetRole { get; set; }
        public DatasetType DatasetType { get; set; }
        public string? DatasetAlias { get; set; }
        public string? DatasetDomain { get; set; }
        public string? Description { get; set; }
        public string LocalPath { get; set; } = string.Empty;
    }

    // ─── Source Schema Profile ─────────────────────────────────────────────────
    public class SourceSchemaProfile
    {
        public string JobId { get; set; } = string.Empty;
        public string SchemaType { get; set; } = "SOURCE";
        public string Dataset { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public DateTime ProfiledAt { get; set; } = DateTime.UtcNow;
        public List<SourceFieldProfile> Fields { get; set; } = new();
    }

    public class SourceFieldProfile
    {
        public string FieldName { get; set; } = string.Empty;
        public string Datatype { get; set; } = string.Empty;
        public bool Nullable { get; set; }
        public int MaxLength { get; set; }
        public bool IsUnique { get; set; }
        public List<string> SampleValues { get; set; } = new();
        public List<string> DistinctValues { get; set; } = new();
        public int DistinctValueCount { get; set; }
        public string? FieldPattern { get; set; }
        public string? PossibleMeaning { get; set; }
        public int ColumnIndex { get; set; }
    }

    // ─── Target Metadata Profile ───────────────────────────────────────────────
    public class TargetMetadataProfile
    {
        public string JobId { get; set; } = string.Empty;
        public string TargetDataset { get; set; } = string.Empty;
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
        public List<TargetFieldMetadata> Fields { get; set; } = new();
    }

    public class TargetFieldMetadata
    {
        public string FieldName { get; set; } = string.Empty;
        public string Datatype { get; set; } = string.Empty;
        public string? Format { get; set; }
        public string? Rule { get; set; }
        public string? Description { get; set; }
        public string? HardcodedValue { get; set; }
        public bool IsRequired { get; set; }
        public int? FieldLength { get; set; }
        public int ColumnOrder { get; set; }
        public string? DefaultValue { get; set; }
        public string? ValidationPattern { get; set; }
    }

    // ─── Semantic Schema Profile ───────────────────────────────────────────────
    public class SemanticSchemaProfile
    {
        public string JobId { get; set; } = string.Empty;
        public string Dataset { get; set; } = string.Empty;
        public DateTime EnrichedAt { get; set; } = DateTime.UtcNow;
        public List<SemanticFieldEnrichment> EnrichedFields { get; set; } = new();
    }

    public class SemanticFieldEnrichment
    {
        public string FieldName { get; set; } = string.Empty;
        public string? SemanticMeaning { get; set; }
        public string? ExpandedAbbreviation { get; set; }
        public string? BusinessCategory { get; set; }
        public string? InterpretedDescription { get; set; }
        public string? DataTypeHint { get; set; }
        public double ConfidenceScore { get; set; }
        public bool WasAIEnriched { get; set; }
    }

    // ─── Mapping Candidates ────────────────────────────────────────────────────
    public class MappingCandidatesDocument
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public List<MappingCandidate> Mappings { get; set; } = new();
    }

    public class MappingCandidate
    {
        public string SourceField { get; set; } = string.Empty;
        public string? SourceDataset { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public string MatchType { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public MappingStatus Status { get; set; }
        public string? TransformationRule { get; set; }
        public string? Notes { get; set; }
    }

    // ─── AI Mapping Response ───────────────────────────────────────────────────
    public class AIMappingResponse
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public List<AIMappingEntry> AiMappings { get; set; } = new();
    }

    public class AIMappingEntry
    {
        public string SourceField { get; set; } = string.Empty;
        public string SourceDataset { get; set; } = string.Empty;  // stamped from candidate before returning
        public string TargetField { get; set; } = string.Empty;
        public string TransformationRule { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Reasoning { get; set; } = string.Empty;
        public ConfidenceLevel ConfidenceLevel { get; set; }
    }

    // ─── Canonical Mapping Config ──────────────────────────────────────────────
    public class CanonicalMappingConfig
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public List<DatasetRelationship> Relationships { get; set; } = new();
    }

    public class DatasetRelationship
    {
        public string LeftDataset { get; set; } = string.Empty;
        public string LeftField { get; set; } = string.Empty;
        public string RightDataset { get; set; } = string.Empty;
        public string RightField { get; set; } = string.Empty;
        public RelationshipType RelationshipType { get; set; }
        public double ConfidenceScore { get; set; }
    }

    // ─── Final Mapping Config ──────────────────────────────────────────────────
    public class FinalMappingConfig
    {
        public string JobId { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0";
        public DateTime FinalizedAt { get; set; } = DateTime.UtcNow;
        public List<FinalMapping> Mappings { get; set; } = new();
    }

    public class FinalMapping
    {
        public string SourceField { get; set; } = string.Empty;
        public string? SourceDataset { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public MappingStatus Status { get; set; }
        /// <summary>How this mapping was resolved: Historical, Deterministic, AI, Hardcoded</summary>
        public string MatchSource { get; set; } = string.Empty;
        public List<TransformationRule> Transformations { get; set; } = new();
        /// <summary>Secondary source fields beyond the primary. Each has a merge rule (FALLBACK or CONCAT).</summary>
        public List<AdditionalSourceMapping> AdditionalSources { get; set; } = new();
    }

    public class AdditionalSourceMapping
    {
        public string SourceField { get; set; } = string.Empty;
        /// <summary>FALLBACK or CONCAT</summary>
        public string MergeRule { get; set; } = "FALLBACK";
    }

    public class TransformationRule
    {
        public string Operation { get; set; } = string.Empty;
        public Dictionary<string, string>? Rules { get; set; }
        public string? Format { get; set; }
        public string? DefaultValue { get; set; }
        public string? MaskPattern { get; set; }
        public int? FixedWidth { get; set; }
        public string? PadCharacter { get; set; }
        public string? Alignment { get; set; }
        // Populated when rule was AI-inferred via /api/rule-inference
        public Dictionary<string, string>? AIParameters { get; set; }
    }

    public class ValueMappingsDocument
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public List<ValueMappingFieldDocument> Fields { get; set; } = new();
    }

    public class ValueMappingFieldDocument
    {
        public string TargetField { get; set; } = string.Empty;
        public string? SourceField { get; set; }
        public string? TemplateName { get; set; }
        public List<ValueMappingEntry> Entries { get; set; } = new();
    }

    public class ValueMappingEntry
    {
        public string SourceValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string? Notes { get; set; }
    }

    // ─── Value Mapping Candidates ──────────────────────────────────────────────
    public class ValueMappingCandidatesDocument
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
        public List<ValueMappingCandidateEntry> Candidates { get; set; } = new();
    }

    public class ValueMappingCandidateEntry
    {
        public string SourceField { get; set; } = string.Empty;
        public string? SourceDataset { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public List<string> DistinctSourceValues { get; set; } = new();
        public int DistinctValueCount { get; set; }
        /// <summary>Pending | Reviewed | Complete</summary>
        public string Status { get; set; } = "Pending";
        public List<ValueMappingCandidateRule> SuggestedMappings { get; set; } = new();
        public double AiConfidence { get; set; }
        public DateTime? ReviewedAt { get; set; }
    }

    public class ValueMappingCandidateRule
    {
        public string SourceValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public bool IsAiSuggested { get; set; }
    }

    // ─── Rule Inference Result (from Python /api/rule-inference) ──────────────
    public class RuleInferenceResult
    {
        public string Operation { get; set; } = string.Empty;
        public string? Format { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
        public string? Description { get; set; }
        public double Confidence { get; set; }
        public string ConfidenceLevel { get; set; } = string.Empty;
        public string? Reasoning { get; set; }
        public bool Cached { get; set; }
    }

    // ─── Canonical Record ──────────────────────────────────────────────────────
    public class CanonicalRecord
    {
        public int RowIndex { get; set; }
        public Dictionary<string, object?> Fields { get; set; } = new();
        public List<string> SourceDatasets { get; set; } = new();
    }

    // ─── Reconciliation Result ─────────────────────────────────────────────────
    public class ReconciliationResult
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public int TotalSourceRecords { get; set; }
        public int TotalTargetRecords { get; set; }
        public int MatchedRecords { get; set; }
        public int MismatchedRecords { get; set; }
        public int ErrorRecords { get; set; }
        public ReconciliationStatus OverallStatus { get; set; }
        public List<ReconciliationMismatch> Mismatches { get; set; } = new();
        public Dictionary<string, decimal> FieldTotals { get; set; } = new();
        /// <summary>Source-side field totals (SUM) for comparison with target FieldTotals.</summary>
        public Dictionary<string, decimal> SourceFieldTotals { get; set; } = new();
        /// <summary>Source-side distinct counts for COUNT-type reconciliation fields.</summary>
        public Dictionary<string, int> SourceFieldCounts { get; set; } = new();
        /// <summary>Target-side distinct counts for COUNT-type reconciliation fields.</summary>
        public Dictionary<string, int> TargetFieldCounts { get; set; } = new();
        public List<string> ValidationErrors { get; set; } = new();
    }

    public class ReconciliationMismatch
    {
        public int RowIndex { get; set; }
        public string FieldName { get; set; } = string.Empty;
        public string? SourceValue { get; set; }
        public string? TransformedValue { get; set; }
        public string ExpectedFormat { get; set; } = string.Empty;
        public string MismatchReason { get; set; } = string.Empty;
    }

    // ─── AI Inference Request/Response ────────────────────────────────────────
    public class AIInferenceRequest
    {
        public string JobId { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string CacheKey { get; set; } = string.Empty;
        public WorkflowStep Step { get; set; }
        public Dictionary<string, object> Context { get; set; } = new();
    }

    public class AIInferenceResponse
    {
        public bool Success { get; set; }
        public string? Content { get; set; }
        public double ConfidenceScore { get; set; }
        public bool WasCached { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public double DurationMs { get; set; }

        // Token usage (populated by PythonAIInferenceService when available)
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public double? EstimatedCostUsd { get; set; }
    }

    // ─── Reconciliation Configuration (Enhancement 2) ─────────────────────────
    public class ReconciliationConfig
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime ConfiguredAt { get; set; } = DateTime.UtcNow;
        public List<string> ReconciliationKeys { get; set; } = new();
        public List<ReconciliationFieldConfig> Fields { get; set; } = new();
    }

    public class ReconciliationFieldConfig
    {
        public string FieldName { get; set; } = string.Empty;
        public int Priority { get; set; }
        public bool IsMandatory { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double? Confidence { get; set; }
        /// <summary>COUNT or SUM — how this field should be validated in reconciliation.</summary>
        public string ValidationType { get; set; } = "COUNT";
    }

    // ─── Transformation Override (Enhancement 3) ──────────────────────────────
    public class TransformationOverrideConfig
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
        public List<TransformationOverrideEntry> Overrides { get; set; } = new();
    }

    public class TransformationOverrideEntry
    {
        public string TargetField { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string SuggestedTransformation { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string OverrideTransformation { get; set; } = string.Empty;
        public string? CustomExpression { get; set; }
        /// <summary>AI_Suggested | User_Override | Approved</summary>
        public string Status { get; set; } = "AI_Suggested";
    }
}
