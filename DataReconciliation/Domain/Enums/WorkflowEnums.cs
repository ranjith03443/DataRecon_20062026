namespace DataReconciliation.Domain.Enums
{
    public enum WorkflowStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        PartialSuccess,
        Replaying,
        AwaitingMappingApproval
    }

    public enum WorkflowStep
    {
        DatasetRegistration = 1,
        FileIngestion = 2,
        SourceSchemaProfiling = 3,
        TargetMetadataExtraction = 4,
        SemanticSchemaEnrichment = 5,
        DeterministicMapping = 6,
        AISemanticMapping = 7,
        RelationshipResolution = 8,
        CanonicalDataModelConstruction = 9,
        MappingConsolidation = 10,
        TransformationExecution = 11,
        TargetFileGeneration = 12,
        ReconciliationValidation = 13,
        ReportingAuditGeneration = 14
    }

    public enum DatasetRole
    {
        SOURCE,
        TARGET_SCHEMA,
        HISTORICAL_MAPPINGS,
        MAPPING_RULES
    }

    public enum DatasetType
    {
        CSV,
        EXCEL,
        JSON,
        DAT,
        TXT
    }

    public enum StepStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Skipped,
        Retrying
    }

    public enum MappingStatus
    {
        AUTO_MATCHED,
        AI_MATCHED,
        MANUAL_REVIEW_REQUIRED,
        UNRESOLVED,
        CONFIRMED
    }

    public enum ConfidenceLevel
    {
        High,      // > 0.90
        Medium,    // 0.70 - 0.90
        Low        // < 0.70
    }

    public enum TransformationOperation
    {
        DateFormatting,
        DecimalFormatting,
        Masking,
        ValueMapping,
        HardcodedValue,
        FixedWidthFormatting,
        DatatypeConversion,
        DefaultValueAssignment,
        DirectMapping,
        Concatenation,
        Substring,
        Trim,
        UpperCase,
        LowerCase
    }

    public enum RelationshipType
    {
        INNER_JOIN,
        LEFT_JOIN,
        RIGHT_JOIN,
        FULL_OUTER_JOIN
    }

    public enum ArtifactType
    {
        DatasetManifest,
        SourceSchemaProfile,
        TargetMetadataProfile,
        SemanticSchemaProfile,
        MappingCandidates,
        AIMappingResponse,
        CanonicalMappingConfig,
        FinalMappingConfig,
        ValueMappings,
        ValueMappingCandidates,
        TransformationOutput,
        ReconciliationResult,
        ReconciliationConfig,
        TransformationOverrides,
        AuditLog,
        TargetFile,
        WorkflowLog
    }

    public enum ReconciliationStatus
    {
        SUCCESS,
        PARTIAL_SUCCESS,
        FAILED,
        PENDING
    }
}
