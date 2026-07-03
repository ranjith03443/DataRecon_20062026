using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using DataReconciliation.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class EvaluationService : IEvaluationService
    {
        private readonly ILogger<EvaluationService> _logger;
        private readonly IArtifactPersistenceService _artifactPersistence;
        private readonly IWorkflowStepExecutionRepository _stepRepo;
        private readonly IAIInferenceAuditRepository _auditRepo;
        private readonly IWorkflowJobRepository _jobRepo;
        private readonly IAISettingsService _aiSettings;

        public EvaluationService(
            ILogger<EvaluationService> logger,
            IArtifactPersistenceService artifactPersistence,
            IWorkflowStepExecutionRepository stepRepo,
            IAIInferenceAuditRepository auditRepo,
            IWorkflowJobRepository jobRepo,
            IAISettingsService aiSettings)
        {
            _logger = logger;
            _artifactPersistence = artifactPersistence;
            _stepRepo = stepRepo;
            _auditRepo = auditRepo;
            _jobRepo = jobRepo;
            _aiSettings = aiSettings;
        }

        public async Task<EvaluationSummary> ComputeEvaluationAsync(string jobId)
        {
            _logger.LogInformation("Computing evaluation summary. JobId={JobId}", jobId);

            var job           = await _jobRepo.GetByJobIdAsync(jobId);
            var finalMapping  = await _artifactPersistence.LoadArtifactAsync<FinalMappingConfig>(jobId, ArtifactType.FinalMappingConfig);
            var targetProfile = await _artifactPersistence.LoadArtifactAsync<TargetMetadataProfile>(jobId, ArtifactType.TargetMetadataProfile);
            var aiResponse    = await _artifactPersistence.LoadArtifactAsync<AIMappingResponse>(jobId, ArtifactType.AIMappingResponse);
            var overrides     = await _artifactPersistence.LoadArtifactAsync<TransformationOverrideConfig>(jobId, ArtifactType.TransformationOverrides);
            var auditLog      = await _artifactPersistence.LoadArtifactByNameAsync<GovernanceAuditLog>(jobId, "governance_audit_log.json");
            var steps         = (await _stepRepo.GetByJobIdAsync(jobId)).ToList();
            var aiAudits      = (await _auditRepo.GetByJobIdAsync(jobId)).ToList();

            var summary = new EvaluationSummary
            {
                JobId        = jobId,
                GeneratedAt  = DateTime.UtcNow,
                ReviewerName = job?.ReviewerName ?? string.Empty
            };

            // ── Mapping Coverage ─────────────────────────────────────────────────
            int totalTarget = targetProfile?.Fields.Count ?? 0;

            if (finalMapping != null && targetProfile != null)
            {
                var mappedTargets = finalMapping.Mappings
                    .Where(m => m.Status != MappingStatus.UNRESOLVED)
                    .Select(m => m.TargetField)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                summary.MappedFields         = mappedTargets.Count;
                summary.UnresolvedFieldNames = targetProfile.Fields
                    .Where(f => !mappedTargets.Contains(f.FieldName))
                    .Select(f => f.FieldName)
                    .ToList();
            }

            summary.TotalTargetFields          = totalTarget;
            summary.UnresolvedFields           = totalTarget - summary.MappedFields;
            summary.MappingCoveragePercent     = totalTarget > 0 ? Math.Round((double)summary.MappedFields / totalTarget * 100, 1) : 0;
            summary.UnresolvedFieldRatePercent = totalTarget > 0 ? Math.Round((double)summary.UnresolvedFields / totalTarget * 100, 1) : 0;

            // ── Mapping Source Breakdown ─────────────────────────────────────────
            summary.MappingSourceBreakdown = finalMapping?.Mappings
                .Where(m => m.Status != MappingStatus.UNRESOLVED && !string.IsNullOrWhiteSpace(m.MatchSource))
                .GroupBy(m => m.MatchSource!)
                .ToDictionary(g => g.Key, g => g.Count())
                ?? new Dictionary<string, int>();

            // ── AI Suggestions & Confidence Calibration ──────────────────────────
            summary.TotalAiSuggestions = aiResponse?.AiMappings.Count ?? 0;

            if (aiResponse != null)
            {
                summary.ConfidenceCalibration = new ConfidenceCalibrationMetrics
                {
                    HighConfidenceCount   = aiResponse.AiMappings.Count(m => m.Confidence > 0.90),
                    MediumConfidenceCount = aiResponse.AiMappings.Count(m => m.Confidence >= 0.70 && m.Confidence <= 0.90),
                    LowConfidenceCount    = aiResponse.AiMappings.Count(m => m.Confidence < 0.70)
                };
            }

            // ── Transformation Accuracy (from transformation_overrides.json) ──────
            if (overrides?.Overrides.Any() == true)
            {
                var allOverrides      = overrides.Overrides;
                var reviewed          = allOverrides.Where(o => o.Status != "AI_Suggested").ToList();
                var overridden        = reviewed.Where(o => !string.IsNullOrWhiteSpace(o.OverrideTransformation)).ToList();
                var acceptedAsIs      = reviewed.Count - overridden.Count;

                summary.TransformationsTotalCount     = allOverrides.Count;
                summary.TransformationsReviewedCount  = reviewed.Count;
                summary.TransformationsOverriddenCount= overridden.Count;
                summary.TransformationAccuracyPercent = reviewed.Count > 0
                    ? Math.Round((double)acceptedAsIs / reviewed.Count * 100, 1) : 0;
            }

            // ── Human Validation (from Phase 2 audit log) ────────────────────────
            if (auditLog?.Entries.Any() == true)
            {
                var acceptedTypes = new[] { AuditActionType.MappingAccepted, AuditActionType.TransformationAccepted };
                var modifiedTypes = new[] { AuditActionType.MappingModified, AuditActionType.TransformationOverridden };
                var rejectedTypes = new[] { AuditActionType.MappingRejected, AuditActionType.TransformationRejected };

                summary.AcceptedWithoutChange = auditLog.Entries.Count(e => acceptedTypes.Contains(e.ActionType));
                summary.HumanModified         = auditLog.Entries.Count(e => modifiedTypes.Contains(e.ActionType));
                summary.HumanRejected         = auditLog.Entries.Count(e => rejectedTypes.Contains(e.ActionType));

                int humanTotal = summary.AcceptedWithoutChange + summary.HumanModified + summary.HumanRejected;
                if (humanTotal > 0)
                {
                    summary.AiAcceptanceRatePercent  = Math.Round((double)summary.AcceptedWithoutChange / humanTotal * 100, 1);
                    summary.HumanOverrideRatePercent = Math.Round((double)(summary.HumanModified + summary.HumanRejected) / humanTotal * 100, 1);

                    if (aiResponse != null)
                    {
                        var accepted = auditLog.Entries.Where(e => acceptedTypes.Contains(e.ActionType)).ToList();
                        summary.ConfidenceCalibration.HighConfidenceAcceptanceRate   = ComputeTierAcceptanceRate(accepted, aiResponse, 0.90, 1.01);
                        summary.ConfidenceCalibration.MediumConfidenceAcceptanceRate = ComputeTierAcceptanceRate(accepted, aiResponse, 0.70, 0.90);
                        summary.ConfidenceCalibration.LowConfidenceAcceptanceRate    = ComputeTierAcceptanceRate(accepted, aiResponse, 0.0, 0.70);
                    }
                }
            }

            // ── Artifact Generation Success Rate (from workflow step statuses) ────
            if (steps.Any())
            {
                var attempted  = steps.Count(s => s.Status != StepStatus.Pending && s.Status != StepStatus.Running);
                var successful = steps.Count(s => s.Status == StepStatus.Completed || s.Status == StepStatus.Skipped);
                summary.ArtifactGenerationSuccessRatePercent = attempted > 0
                    ? Math.Round((double)successful / attempted * 100, 1) : 0;
            }

            // ── Gap Analysis ─────────────────────────────────────────────────────
            summary.GapAnalysis = new GapAnalysis
            {
                UnmappedTargetFields      = summary.UnresolvedFields,
                UnmappedFieldNames        = summary.UnresolvedFieldNames,
                ManualReviewRequiredCount = finalMapping?.Mappings.Count(m => m.Status == MappingStatus.MANUAL_REVIEW_REQUIRED) ?? 0,
                ManualReviewFieldNames    = finalMapping?.Mappings
                    .Where(m => m.Status == MappingStatus.MANUAL_REVIEW_REQUIRED)
                    .Select(m => m.TargetField).ToList() ?? new List<string>(),
                UnreviewedTransformationsCount = overrides?.Overrides.Count(o => o.Status == "AI_Suggested") ?? 0,
                UnreviewedTransformationFields = overrides?.Overrides
                    .Where(o => o.Status == "AI_Suggested")
                    .Select(o => o.TargetField).ToList() ?? new List<string>()
            };

            // ── Processing Metrics ───────────────────────────────────────────────
            int promptTotal     = aiAudits.Sum(a => a.PromptTokens ?? 0);
            int completionTotal = aiAudits.Sum(a => a.CompletionTokens ?? 0);
            double? costTotal   = aiAudits.Any(a => a.EstimatedCostUsd.HasValue)
                                  ? aiAudits.Sum(a => a.EstimatedCostUsd ?? 0) : null;

            var settings = _aiSettings.Load();
            summary.Processing = new ProcessingMetrics
            {
                LlmProviderUsed         = settings.LlmProvider,
                LlmModelUsed            = settings.SemanticMappingModel,
                TotalWorkflowDurationMs = steps.Sum(s => s.DurationMs ?? 0),
                TotalPromptTokens       = promptTotal > 0 ? promptTotal : null,
                TotalCompletionTokens   = completionTotal > 0 ? completionTotal : null,
                TotalTokens             = (promptTotal + completionTotal) > 0 ? (promptTotal + completionTotal) : null,
                TotalEstimatedCostUsd   = costTotal,
                StepMetrics             = steps.Select(s => new StepMetric
                {
                    Step        = s.Step.ToString(),
                    DurationMs  = s.DurationMs ?? 0,
                    Status      = s.Status.ToString(),
                    StartedAt   = s.StartedAt,
                    CompletedAt = s.CompletedAt
                }).ToList()
            };

            // ── Mapping Readiness Score ──────────────────────────────────────────
            // Coverage × Acceptance Rate; if no reviews yet, assume full acceptance so score = coverage %
            double acceptanceFactor = (summary.AcceptedWithoutChange + summary.HumanModified + summary.HumanRejected) > 0
                ? summary.AiAcceptanceRatePercent / 100.0
                : 1.0;
            summary.MappingReadinessScore = Math.Round(summary.MappingCoveragePercent * acceptanceFactor, 1);

            await _artifactPersistence.PersistArtifactAsync(jobId, summary, ArtifactType.EvaluationSummary, "evaluation_summary.json");
            await AppendRunHistoryAsync(jobId, summary);

            _logger.LogInformation(
                "Evaluation summary written. JobId={JobId} ReadinessScore={Score} Coverage={Coverage}% TransformAccuracy={TA}%",
                jobId, summary.MappingReadinessScore, summary.MappingCoveragePercent, summary.TransformationAccuracyPercent);

            return summary;
        }

        public async Task<EvaluationSummary?> LoadEvaluationAsync(string jobId) =>
            await _artifactPersistence.LoadArtifactByNameAsync<EvaluationSummary>(jobId, "evaluation_summary.json");

        public async Task<EvaluationRunHistory> LoadRunHistoryAsync(string jobId)
        {
            var history = await _artifactPersistence.LoadArtifactAsync<EvaluationRunHistory>(jobId, ArtifactType.EvaluationHistory);
            return history ?? new EvaluationRunHistory { JobId = jobId };
        }

        private async Task AppendRunHistoryAsync(string jobId, EvaluationSummary summary)
        {
            try
            {
                var history = await LoadRunHistoryAsync(jobId);
                history.JobId = jobId;
                history.Runs.Add(new EvaluationRunSnapshot
                {
                    RunIndex                          = history.Runs.Count + 1,
                    ComputedAt                        = summary.GeneratedAt,
                    ReviewerName                      = summary.ReviewerName,
                    LlmProvider                       = summary.Processing.LlmProviderUsed,
                    LlmModel                          = summary.Processing.LlmModelUsed,
                    MappingReadinessScore             = summary.MappingReadinessScore,
                    MappingCoveragePercent            = summary.MappingCoveragePercent,
                    AiAcceptanceRatePercent           = summary.AiAcceptanceRatePercent,
                    HumanOverrideRatePercent          = summary.HumanOverrideRatePercent,
                    TransformationAccuracyPercent     = summary.TransformationAccuracyPercent,
                    ArtifactGenerationSuccessRatePercent = summary.ArtifactGenerationSuccessRatePercent,
                    TotalTargetFields                 = summary.TotalTargetFields,
                    MappedFields                      = summary.MappedFields,
                    UnresolvedFields                  = summary.UnresolvedFields,
                    AcceptedWithoutChange             = summary.AcceptedWithoutChange,
                    HumanModified                     = summary.HumanModified,
                    HumanRejected                     = summary.HumanRejected,
                    GapUnmappedCount                  = summary.GapAnalysis.UnmappedTargetFields,
                    GapManualReviewCount              = summary.GapAnalysis.ManualReviewRequiredCount,
                    GapUnreviewedTransformCount       = summary.GapAnalysis.UnreviewedTransformationsCount
                });
                await _artifactPersistence.PersistArtifactAsync(jobId, history, ArtifactType.EvaluationHistory, "evaluation_history.json");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed appending to run history. JobId={JobId}", jobId);
            }
        }

        private static double ComputeTierAcceptanceRate(
            List<AuditEntry> acceptedEntries,
            AIMappingResponse aiResponse,
            double confMin, double confMax)
        {
            var tierFields = aiResponse.AiMappings
                .Where(m => m.Confidence >= confMin && m.Confidence < confMax)
                .Select(m => m.TargetField)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tierFields.Count == 0) return 0;

            int acceptedInTier = acceptedEntries.Count(e => tierFields.Contains(e.TargetField));
            return Math.Round((double)acceptedInTier / tierFields.Count * 100, 1);
        }
    }
}
