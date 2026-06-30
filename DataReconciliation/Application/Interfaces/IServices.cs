using DataReconciliation.Application.DTOs;
using DataReconciliation.Domain.Models;
using DataReconciliation.Domain.Enums;

namespace DataReconciliation.Application.Interfaces
{
    public interface IWorkflowOrchestratorService
    {
        Task<WorkflowContext> StartWorkflowAsync(DatasetManifest manifest);
        Task<WorkflowContext> ExecuteStepAsync(string jobId, WorkflowStep step);
        Task<WorkflowContext> ReplayWorkflowAsync(string jobId, WorkflowStep fromStep);
        Task<WorkflowContext?> GetWorkflowContextAsync(string jobId);
    }

    /// <summary>
    /// Calls the Python /api/rule-inference endpoint to interpret a free-text
    /// transformation rule and return a structured operation + parameters.
    /// </summary>
    public interface IAIRuleInferenceService
    {
        Task<RuleInferenceResult?> InferRuleAsync(string rule, string fieldContext, string jobId, string workflowStep = "TransformationExecution");
        Task<bool> IsAvailableAsync();
    }

    public interface IDatasetRegistrationService
    {
        Task<DatasetManifest> RegisterDatasetsAsync(string jobId, IEnumerable<DatasetRegistrationRequest> requests);
        Task<DatasetManifest?> LoadManifestAsync(string jobId);
        Task PersistManifestAsync(string jobId, DatasetManifest manifest);
    }

    public interface IFileIngestionService
    {
        Task<string> IngestFileAsync(string jobId, Stream fileStream, string fileName, DatasetRole role);
        Task<bool> ValidateFileAsync(string filePath, DatasetType type);
        Task<string> GetWorkflowPathAsync(string jobId, string subfolder);
    }

    public interface ISourceSchemaProfilingService
    {
        Task<SourceSchemaProfile> ProfileSourceFileAsync(string jobId, string datasetId, string filePath);
        Task<IEnumerable<SourceSchemaProfile>> ProfileAllSourcesAsync(string jobId, DatasetManifest manifest);
        Task PersistProfileAsync(string jobId, SourceSchemaProfile profile);
    }

    public interface ITargetMetadataExtractionService
    {
        Task<TargetMetadataProfile> ExtractTargetMetadataAsync(string jobId, string filePath);
        Task PersistMetadataProfileAsync(string jobId, TargetMetadataProfile profile);
    }

    public interface ITargetSchemaGeneratorService
    {
        Task<TargetSchemaGenerationResultDto> GenerateAsync(
            string jobId,
            string schemaName,
            string sourceFileName,
            string sourceFilePath,
            bool useAiEnrichment = true);

        Task<TargetSchemaGenerationResultDto> GenerateMultiSourceAsync(
            string jobId,
            string schemaName,
            IReadOnlyList<(string FileName, string FilePath)> sourceFiles,
            bool useAiEnrichment = true);

        Task<string?> ResolveWorkbookPathAsync(string jobId, string fileName);
        Task<TargetSchemaGenerationResultDto> SaveEditsAsync(SaveSchemaEditsRequest request);

        /// <summary>Builds an editable mapping Excel and returns its bytes + suggested file name.</summary>
        Task<(byte[] Bytes, string FileName)> DownloadMappingAsync(string jobId);

        /// <summary>Reads an edited mapping Excel and regenerates the workbook.</summary>
        Task<TargetSchemaGenerationResultDto> ImportMappingAsync(
            string jobId,
            string schemaName,
            Stream mappingStream);
    }

    public interface IMainframeArtifactGenerationService
    {
        Task<MainframeArtifactGenerationResultDto> GenerateAsync(
            string jobId,
            string recordName,
            string applicationName,
            bool includeComments = true);

        Task<string?> ResolveArtifactPathAsync(string jobId, string fileName);
    }

    public interface IMainframeAssetGenerationService
    {
        Task<MainframeAssetGenerationResultDto> GenerateAsync(
            string jobId,
            MainframeAssetRequest request);

        Task<string?> ResolveAssetPathAsync(string jobId, string fileName);
    }

    /// <summary>
    /// Calls the Python Mainframe Development AI Agent endpoints.
    /// Returns AI-generated explanations, reviews, enhancements, and guidance.
    /// Advisory only — all output is developer review required.
    /// </summary>
    public interface IMainframeAiAgentService
    {
        Task<MainframeAiAgentResponse?> RunAgentAsync(MainframeAiAgentRequest request);
        Task<bool> IsAvailableAsync();
    }

    public interface ISemanticSchemaEnrichmentService
    {
        Task<SemanticSchemaProfile> EnrichSchemaAsync(string jobId, SourceSchemaProfile profile);
        Task PersistEnrichedProfileAsync(string jobId, SemanticSchemaProfile profile);
    }

    public interface IDeterministicMappingService
    {
        Task<MappingCandidatesDocument> GenerateMappingCandidatesAsync(
            string jobId,
            IEnumerable<SourceSchemaProfile> sourceProfiles,
            TargetMetadataProfile targetProfile,
            IEnumerable<SemanticSchemaProfile>? semanticProfiles = null);
        Task PersistMappingCandidatesAsync(string jobId, MappingCandidatesDocument candidates);
    }

    public interface IAIMappingInferenceService
    {
        Task<AIMappingResponse> InferMappingsAsync(
            string jobId,
            MappingCandidatesDocument candidates,
            TargetMetadataProfile targetProfile,
            IEnumerable<SemanticSchemaProfile>? semanticProfiles = null,
            IEnumerable<SourceSchemaProfile>? sourceProfiles = null);
        Task PersistAIMappingResponseAsync(string jobId, AIMappingResponse response);
    }

    public interface IRelationshipResolutionService
    {
        Task<CanonicalMappingConfig> ResolveRelationshipsAsync(
            string jobId,
            IEnumerable<SourceSchemaProfile> sourceProfiles);
        Task PersistCanonicalMappingConfigAsync(string jobId, CanonicalMappingConfig config);
    }

    public interface ICanonicalDataModelBuilderService
    {
        Task<IEnumerable<CanonicalRecord>> BuildCanonicalModelAsync(
            string jobId,
            DatasetManifest manifest,
            CanonicalMappingConfig config);
        Task PersistCanonicalModelAsync(string jobId, IEnumerable<CanonicalRecord> records);
    }

    public interface IMappingConsolidationService
    {
        Task<FinalMappingConfig> ConsolidateMappingsAsync(
            string jobId,
            MappingCandidatesDocument deterministicMappings,
            AIMappingResponse aiMappings,
            TargetMetadataProfile targetProfile);
        Task PersistFinalMappingConfigAsync(string jobId, FinalMappingConfig config);
    }

    public interface ITransformationEngineService
    {
        Task<IEnumerable<Dictionary<string, string>>> ExecuteTransformationsAsync(
            string jobId,
            IEnumerable<CanonicalRecord> canonicalRecords,
            FinalMappingConfig mappingConfig,
            TargetMetadataProfile targetProfile);
        Task PersistTransformationOutputAsync(string jobId, IEnumerable<Dictionary<string, string>> output);
    }

    public interface ITargetFileGenerationService
    {
        Task<string> GenerateTargetFileAsync(
            string jobId,
            IEnumerable<Dictionary<string, string>> transformedRecords,
            TargetMetadataProfile targetProfile);
    }

    public interface IReconciliationService
    {
        Task<ReconciliationResult> ReconcileAsync(
            string jobId,
            IEnumerable<CanonicalRecord> sourceRecords,
            IEnumerable<Dictionary<string, string>> transformedRecords,
            TargetMetadataProfile targetProfile);
        Task PersistReconciliationResultAsync(string jobId, ReconciliationResult result);
    }

    public interface IArtifactPersistenceService
    {
        Task<string> PersistArtifactAsync<T>(string jobId, T data, ArtifactType type, string fileName) where T : class;
        Task<T?> LoadArtifactAsync<T>(string jobId, ArtifactType type) where T : class;
        Task<T?> LoadArtifactByNameAsync<T>(string jobId, string fileName) where T : class;
        Task<bool> ArtifactExistsAsync(string jobId, ArtifactType type);
    }

    public interface IReportGenerationService
    {
        Task GenerateAuditReportAsync(string jobId);
        Task GenerateReconciliationReportAsync(string jobId, ReconciliationResult result);
    }

    public interface IValueMappingDiscoveryService
    {
        Task<DataReconciliation.Domain.Models.ValueMappingCandidatesDocument> DiscoverAsync(string jobId);
    }

    /// <summary>
    /// Enhancement 1: Provides incremental (delta) file upload processing without restarting the workflow.
    /// </summary>
    public interface IDeltaFileUploadService
    {
        Task<DataReconciliation.Application.DTOs.DeltaFileUploadResultDto> ProcessDeltaUploadAsync(
            string jobId,
            Stream fileStream,
            string fileName,
            string datasetId,
            DataReconciliation.Domain.Enums.DatasetRole role,
            DataReconciliation.Domain.Enums.DatasetType type,
            string? alias = null,
            string? domain = null);
    }

    /// <summary>
    /// Enhancement 2: Manages reconciliation configuration — suggesting fields and storing user selections.
    /// </summary>
    public interface IReconciliationConfigService
    {
        Task<DataReconciliation.Domain.Models.ReconciliationConfig> SuggestReconciliationFieldsAsync(string jobId);
        Task<DataReconciliation.Domain.Models.ReconciliationConfig?> LoadConfigAsync(string jobId);
        Task SaveConfigAsync(string jobId, DataReconciliation.Domain.Models.ReconciliationConfig config);
        Task<DataReconciliation.Application.DTOs.ReconciliationDashboardDto> GetDashboardAsync(string jobId);
    }

    /// <summary>
    /// Enhancement 3: Manages transformation overrides allowing users to override AI-suggested transformations.
    /// </summary>
    public interface ITransformationOverrideService
    {
        Task<List<DataReconciliation.Application.DTOs.TransformationOverrideDto>> GetOverridesAsync(string jobId);
        Task SaveOverridesAsync(string jobId, List<DataReconciliation.Application.DTOs.TransformationOverrideDto> overrides);
        Task ApplyOverridesToFinalMappingAsync(string jobId);
    }

    public interface IValueMappingAIAgentService
    {
        Task<DataReconciliation.Domain.Models.ValueMappingCandidateEntry> SuggestMappingsAsync(
            string jobId,
            string sourceField,
            string targetField,
            List<string> sourceValues,
            string? targetDescription = null,
            List<string>? sourceParameterValues = null,
            List<string>? targetParameterValues = null);
        Task<bool> IsAvailableAsync();
    }

    public interface IAISettingsService
    {
        AISettingsDto Load();
        AISettingsSaveResult Save(AISettingsDto dto);
        string GetPythonEnvPath();
    }
}

namespace DataReconciliation.Application.Interfaces
{
    public class DatasetRegistrationRequest
    {
        public string DatasetId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DataReconciliation.Domain.Enums.DatasetRole DatasetRole { get; set; }
        public DataReconciliation.Domain.Enums.DatasetType DatasetType { get; set; }
        public string? DatasetAlias { get; set; }
        public string? DatasetDomain { get; set; }
        public string? Description { get; set; }
        public IFormFile? File { get; set; }
    }
}


