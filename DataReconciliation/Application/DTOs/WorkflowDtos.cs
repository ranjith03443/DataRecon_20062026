namespace DataReconciliation.Application.DTOs
{
    // ── Folder-scan DTOs ──────────────────────────────────────────────────────

    public class ScanFolderRequest
    {
        public string FolderPath { get; set; } = string.Empty;
    }

    public class ScannedFileDto
    {
        public string FileName { get; set; } = string.Empty;
        public string DetectedRole { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetType { get; set; } = string.Empty;
        public bool IsParameterFile { get; set; }
        public string ParameterFileRole { get; set; } = string.Empty; // "SOURCE" or "TARGET"
        public bool IsValueMappingFile { get; set; }
        public bool IsUnclassified { get; set; }
        public bool IsUnsupported { get; set; }
        public long FileSizeBytes { get; set; }
        public string Extension { get; set; } = string.Empty;
    }

    public class CreateFromFolderRequest
    {
        public string JobName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string FolderPath { get; set; } = string.Empty;
        public List<FolderDatasetAssignmentDto> Datasets { get; set; } = new();
        public string? SourceParameterFilePath { get; set; }
        public string? TargetParameterFilePath { get; set; }
        public string? ValueMappingsExcelFilePath { get; set; }
    }

    public class FolderDatasetAssignmentDto
    {
        public string FileName { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetRole { get; set; } = string.Empty;
        public string DatasetType { get; set; } = string.Empty;
        public string? DatasetAlias { get; set; }
        public string? DatasetDomain { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class CreateWorkflowRequest
    {
        public string JobName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<DatasetUploadDto> Datasets { get; set; } = new();
        /// <summary>Optional source parameter file (.xlsx) for value mapping discovery context.</summary>
        public IFormFile? SourceParameterFile { get; set; }
        /// <summary>Optional target parameter file (.xlsx) for value mapping discovery context.</summary>
        public IFormFile? TargetParameterFile { get; set; }
        /// <summary>Optional value mapping seed Excel file to be reused in Value Mapping Editor.</summary>
        public IFormFile? ValueMappingsExcelFile { get; set; }
    }

    public class DatasetUploadDto
    {
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetRole { get; set; } = string.Empty;
        public string DatasetType { get; set; } = string.Empty;
        public string? DatasetAlias { get; set; }
        public string? DatasetDomain { get; set; }
        public IFormFile? File { get; set; }
    }

    public class WorkflowJobDto
    {
        public string JobId { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CurrentStep { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorDetails { get; set; }
    }

    public class WorkflowStepDto
    {
        public string Step { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public double? DurationMs { get; set; }
        public int RetryCount { get; set; }
        public string? ErrorDetails { get; set; }
    }

    public class WorkflowDetailsDto
    {
        public WorkflowJobDto Job { get; set; } = new();
        public List<WorkflowStepDto> Steps { get; set; } = new();
        public List<ArtifactDto> Artifacts { get; set; } = new();
    }

    public class ArtifactDto
    {
        public string ArtifactType { get; set; } = string.Empty;
        public string ArtifactName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? Version { get; set; }
    }

    public class AdditionalSourceMappingDto
    {
        public string SourceField { get; set; } = string.Empty;
        public string MergeRule { get; set; } = "FALLBACK";
    }

    public class MappingUpdateDto
    {
        public string TargetField { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public bool Delete { get; set; }
        public List<AdditionalSourceMappingDto> AdditionalSources { get; set; } = new();
    }

    public class SaveMappingsRequest
    {
        public List<MappingUpdateDto> Updates { get; set; } = new();
    }

    public class ImportStructuralMappingRequest
    {
        public string JobId { get; set; } = string.Empty;
        public IFormFile? MappingFile { get; set; }
    }

    public class ValueMappingEntryDto
    {
        public string SourceValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string? Notes { get; set; }
    }

    public class ValueMappingFieldDto
    {
        public string TargetField { get; set; } = string.Empty;
        public string? SourceField { get; set; }
        public string? TemplateName { get; set; }
        public List<ValueMappingEntryDto> Entries { get; set; } = new();
    }

    public class ValueMappingsDocumentDto
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public List<ValueMappingFieldDto> Fields { get; set; } = new();
    }

    public class ValueMappingTemplateInfoDto
    {
        public string TemplateName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DateTime? LastModifiedUtc { get; set; }
    }

    public class ValueMappingEditorRequest
    {
        public string TargetField { get; set; } = string.Empty;
        public string? SourceField { get; set; }
        public string? TemplateName { get; set; }
        public List<ValueMappingEntryDto> Entries { get; set; } = new();
    }

    public class TargetSchemaGeneratorRequest
    {
        public string SchemaName { get; set; } = "Generated Target Schema";
        public bool UseAiEnrichment { get; set; } = true;
        public IFormFile? SourceFile { get; set; }
    }

    // ── Multi-source: one source-field-to-target mapping entry ───────────────
    public class SourceFieldMappingDto
    {
        public string SourceFileName  { get; set; } = string.Empty;
        public int    SourceFileIndex { get; set; }
        public string SourceField     { get; set; } = string.Empty;
        public string MergeRule       { get; set; } = "PRIMARY"; // PRIMARY | FALLBACK | CONCAT
    }

    public class TargetSchemaFieldSuggestionDto
    {
        public int    ColumnOrder    { get; set; }
        public string SourceField    { get; set; } = string.Empty;  // single-source / compat
        public List<SourceFieldMappingDto> SourceMappings { get; set; } = new(); // multi-source
        public string TargetField    { get; set; } = string.Empty;
        public string Datatype       { get; set; } = string.Empty;
        public int?   FieldLength    { get; set; }
        public bool   Nullable       { get; set; }
        public string? SampleValue   { get; set; }
        public string? Format        { get; set; }
        public string? AllowedValues { get; set; }
        public string? Description   { get; set; }
        public string? BusinessCategory { get; set; }
        public double Confidence     { get; set; }
        public string GenerationMethod { get; set; } = "PROFILED FROM SOURCE";
    }

    public class TargetSchemaGenerationResultDto
    {
        public string       JobId            { get; set; } = string.Empty;
        public string       SchemaName       { get; set; } = string.Empty;
        public string       SourceFileName   { get; set; } = string.Empty;  // single-source compat
        public List<string> SourceFileNames  { get; set; } = new();         // multi-source
        public bool         IsMultiSource    { get; set; }
        public DateTime     GeneratedAt      { get; set; }
        public bool         UsedAiEnrichment { get; set; }
        public string       WorkbookFileName { get; set; } = string.Empty;
        public List<TargetSchemaFieldSuggestionDto> Fields { get; set; } = new();
    }

    public class TargetSchemaGeneratorPageDto
    {
        public TargetSchemaGeneratorRequest Request { get; set; } = new();
        public TargetSchemaGenerationResultDto? Result { get; set; }
    }

    // ── Multi-source upload request ───────────────────────────────────────────
    public class MultiSourceSchemaRequest
    {
        public string SchemaName      { get; set; } = "Generated Target Schema";
        public bool   UseAiEnrichment { get; set; } = true;
        public List<IFormFile> SourceFiles { get; set; } = new();
    }

    // ── Import (upload-back) request ──────────────────────────────────────────
    public class ImportMappingRequest
    {
        public string     JobId       { get; set; } = string.Empty;
        public string     SchemaName  { get; set; } = string.Empty;
        public IFormFile? MappingFile { get; set; }
    }

    public class SaveSchemaEditsRequest
    {
        public string       JobId          { get; set; } = string.Empty;
        public string       SchemaName     { get; set; } = string.Empty;
        public string       SourceFileName { get; set; } = string.Empty;
        public List<string> SourceFileNames { get; set; } = new();
        public bool         IsMultiSource  { get; set; }
        public bool         UsedAiEnrichment { get; set; }
        public List<TargetSchemaFieldSuggestionDto> Fields { get; set; } = new();
    }

    public class MainframeArtifactRequest
    {
        public string RecordName { get; set; } = "MAINFRAME-RECORD";
        public string ApplicationName { get; set; } = "MAINFRAME_ONBOARDING";
        public bool IncludeComments { get; set; } = true;
    }

    public class MainframeArtifactFieldDto
    {
        public int ColumnOrder { get; set; }
        public string FieldName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public int? Length { get; set; }
        public string PictureClause { get; set; } = string.Empty;
        public string Justification { get; set; } = string.Empty;
        public string? Format { get; set; }
        public string? Rule { get; set; }
        public string? Description { get; set; }
        public string? SourceField { get; set; }
    }

    public class MainframeArtifactGenerationResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public string RecordName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public string CopybookFileName { get; set; } = string.Empty;
        public string SpecificationFileName { get; set; } = string.Empty;
        public string SummaryFileName { get; set; } = string.Empty;
        public List<MainframeArtifactFieldDto> Fields { get; set; } = new();
    }

    public class MainframeArtifactPageDto
    {
        public string JobId { get; set; } = string.Empty;
        public MainframeArtifactRequest Request { get; set; } = new();
        public MainframeArtifactGenerationResultDto? Result { get; set; }
        /// <summary>Carries asset generation state when returning to the same Index view.</summary>
        public MainframeAssetPageDto? AssetPageModel { get; set; }
        /// <summary>Carries recon program generation state when returning to the same Index view.</summary>
        public ReconProgramPageDto? ReconPageModel { get; set; }
    }

    // ─── Value Mapping Workbench DTOs ───────────────────────────────────────────

    public class ValueMappingCandidateDto
    {
        public string SourceField { get; set; } = string.Empty;
        public string? SourceDataset { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public List<string> DistinctSourceValues { get; set; } = new();
        public int DistinctValueCount { get; set; }
        public string Status { get; set; } = "Pending";
        public List<ValueMappingCandidateRuleDto> SuggestedMappings { get; set; } = new();
        public double AiConfidence { get; set; }
        public int ConfiguredRuleCount { get; set; }
        public double CoveragePercent { get; set; }
    }

    public class ValueMappingCandidateRuleDto
    {
        public string SourceValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public bool IsAiSuggested { get; set; }
    }

    public class SaveCandidateMappingsRequest
    {
        public string TargetField { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public List<ValueMappingCandidateRuleDto> Mappings { get; set; } = new();
    }

    public class TransformationPreviewRowDto
    {
        public int RowIndex { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string BeforeValue { get; set; } = string.Empty;
        public string AfterValue { get; set; } = string.Empty;
        public bool Changed { get; set; }
        public string? TransformationType { get; set; }
    }

    // ─── Mainframe Asset Generation DTOs ──────────────────────────────────────

    public class MainframeAssetRequest
    {
        public string RecordName { get; set; } = "CUSTOMER_MASTER";
        public string ApplicationName { get; set; } = "MAINFRAME_APP";
        public string ProgramName { get; set; } = "CUSTMSTR";
        public string JobName { get; set; } = "CUSTLOAD";
        public bool GenerateCopybook { get; set; } = true;
        public bool GenerateCobolSkeleton { get; set; } = true;
        public bool GenerateJclSkeleton { get; set; } = true;
        public bool GenerateTechnicalSpec { get; set; } = true;
        public bool GenerateSampleRecords { get; set; } = true;
        public int SampleRecordCount { get; set; } = 10;
        public bool UseAiMode { get; set; } = false;
    }

    public class MainframeAssetFieldDto
    {
        public int ColumnOrder { get; set; }
        public string FieldName { get; set; } = string.Empty;
        public string CobolFieldName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public int? Length { get; set; }
        public int StartPosition { get; set; }
        public string PictureClause { get; set; } = string.Empty;
        public string? Format { get; set; }
        public string? Rule { get; set; }
        public string? Description { get; set; }
        public string? SourceField { get; set; }
        public bool IsRequired { get; set; }
    }

    public class MainframeAssetGenerationResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public string RecordName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ProgramName { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public int TotalRecordLength { get; set; }
        public List<MainframeAssetFieldDto> Fields { get; set; } = new();
        // File names (null if not requested)
        public string? CopybookFileName { get; set; }
        public string? CobolSkeletonFileName { get; set; }
        public string? JclSkeletonFileName { get; set; }
        public string? TechnicalSpecFileName { get; set; }
        public string? SampleRecordsFileName { get; set; }
        // AI Mode indicators
        public bool CobolAiGenerated { get; set; }
        public bool JclAiGenerated { get; set; }
    }

    public class MainframeAssetPageDto
    {
        public string JobId { get; set; } = string.Empty;
        public MainframeAssetRequest Request { get; set; } = new();
        public MainframeAssetGenerationResultDto? AssetResult { get; set; }
    }

    // ─── Recon Program Generation DTOs ────────────────────────────────────────

    public class ReconProgramRequest
    {
        public string ProgramName { get; set; } = "RECONPGM";
        public string JobName { get; set; } = "RECONJOB";
        public bool GenerateCobol { get; set; } = true;
        public bool GenerateJcl { get; set; } = true;
        public bool GenerateSpec { get; set; } = true;
        public bool UseAiMode { get; set; } = false;
    }

    /// <summary>One reconciliation check emitted in the COBOL program.</summary>
    public class ReconCheckDto
    {
        public string FieldName { get; set; } = string.Empty;
        public string ReconType { get; set; } = "COUNT";        // SUM or COUNT
        public string CobolVarName { get; set; } = string.Empty; // e.g. TOTALAMOUNT
        public string PicClause { get; set; } = string.Empty;
        public int StartPosition { get; set; }
        public int Length { get; set; }
        public string ExpectedValue { get; set; } = string.Empty; // from reconciliation_result
        public string? SourceField { get; set; }
    }

    public class ReconProgramResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public string ProgramName { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public int TotalExpectedRecords { get; set; }
        public int TotalRecordLength { get; set; }
        public List<ReconCheckDto> Checks { get; set; } = new();
        public string? CobolFileName { get; set; }
        public string? JclFileName { get; set; }
        public string? SpecFileName { get; set; }
        public bool CobolAiGenerated { get; set; }
        public bool JclAiGenerated { get; set; }
    }

    public class ReconProgramPageDto
    {
        public string JobId { get; set; } = string.Empty;
        public ReconProgramRequest Request { get; set; } = new();
        public ReconProgramResultDto? Result { get; set; }
    }

    // ─── Enhancement 1: Delta File Upload DTOs ─────────────────────────────────

    public class DeltaFileUploadRequest
    {
        public string JobId { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetRole { get; set; } = "SOURCE";
        public string DatasetType { get; set; } = "CSV";
        public string? DatasetAlias { get; set; }
        public string? DatasetDomain { get; set; }
        public IFormFile? File { get; set; }
    }

    public class DeltaFileUploadResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public int RowsAdded { get; set; }
        public int MappingsUpdated { get; set; }
        public double TimeProcessedMs { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Processed";
    }

    // ─── Enhancement 2: Reconciliation Configuration DTOs ────────────────────────

    public class ReconciliationConfigDto
    {
        public string JobId { get; set; } = string.Empty;
        public List<ReconciliationFieldDto> Fields { get; set; } = new();
        public DateTime ConfiguredAt { get; set; } = DateTime.UtcNow;
    }

    public class ReconciliationFieldDto
    {
        public string FieldName { get; set; } = string.Empty;
        public int Priority { get; set; }
        public bool IsMandatory { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double? Confidence { get; set; }
        /// <summary>COUNT or SUM — how this field should be validated in reconciliation.</summary>
        public string ValidationType { get; set; } = "COUNT";
    }

    public class ReconciliationDashboardDto
    {
        public int FieldsSelected { get; set; }
        public double CoveragePercent { get; set; }
        public int MatchedRecords { get; set; }
        public int FailedRecords { get; set; }
        public int TotalRecords { get; set; }
        public List<ReconciliationFieldDto> ConfiguredFields { get; set; } = new();
    }

    public class SaveReconciliationConfigRequest
    {
        public List<ReconciliationFieldDto> Fields { get; set; } = new();
    }

    // ─── Enhancement 3: Transformation Override DTOs ─────────────────────────────

    public class TransformationOverrideDto
    {
        public string TargetField { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string SuggestedTransformation { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string OverrideTransformation { get; set; } = string.Empty;
        public string? CustomExpression { get; set; }
        public string Status { get; set; } = "AI_Suggested";
    }

    public class SaveTransformationOverridesRequest
    {
        public List<TransformationOverrideDto> Overrides { get; set; } = new();
    }

    // ─── Enhancement 4: Value Mapping Discovery Improvement DTOs ─────────────────

    public class ValueMappingCandidateEnhancedDto
    {
        public string SourceField { get; set; } = string.Empty;
        public string? SourceDataset { get; set; }
        public string TargetField { get; set; } = string.Empty;
        public List<string> DistinctSourceValues { get; set; } = new();
        public int DistinctValueCount { get; set; }
        public string Status { get; set; } = "Pending";
        public List<ValueMappingCandidateRuleDto> SuggestedMappings { get; set; } = new();
        public double AiConfidence { get; set; }
        public int ConfiguredRuleCount { get; set; }
        public double CoveragePercent { get; set; }
        // Enhancement 4 additions
        public bool IsCandidate { get; set; } = true;
        public string CandidateReason { get; set; } = string.Empty;
        public string? ExcludedReason { get; set; }
    }

    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
        public List<string> Errors { get; set; } = new();

        public static ApiResponse<T> Ok(T data, string? message = null) =>
            new() { Success = true, Data = data, Message = message };

        public static ApiResponse<T> Fail(string error) =>
            new() { Success = false, Errors = new List<string> { error } };

        public static ApiResponse<T> Fail(IEnumerable<string> errors) =>
            new() { Success = false, Errors = errors.ToList() };
    }
}
