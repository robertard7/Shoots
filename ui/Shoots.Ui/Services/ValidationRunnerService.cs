using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Settings;

namespace Shoots.UI.Services;

public enum ValidationAction
{
    BuildUiProject,
    RunUiTests,
    RunSmokeValidation,
    RunIntegrityValidation,
    RunFullValidationLoop
}

public interface IValidationRunnerService
{
    string RepoRoot { get; }

    string ValidationRunsRoot { get; }

    IReadOnlyList<string> GetStageLabels(ValidationAction action, bool includeValidateBuild);

    IReadOnlyList<ValidationRunSummary> LoadRecentRuns(int maxCount);

    Task<ValidationRunResult> RunAsync(
        ValidationAction action,
        ValidationSettings settings,
        Action<ValidationProgressEvent>? progress = null,
        CancellationToken ct = default);
}

public sealed record ValidationProgressEvent(
    string EventType,
    string StageId,
    string StageLabel,
    string Status,
    string Message,
    string? OutputLine,
    string? LogPath,
    string? OutputFolder,
    DateTimeOffset TimestampUtc);

public sealed record ValidationStageResult(
    string StageId,
    string StageLabel,
    string Status,
    string Summary,
    string LogPath,
    int ExitCode,
    long DurationMs,
    string StabilityClassification = "passed",
    int RetryCount = 0,
    string? RetryLogPath = null);

public sealed record ValidationFirstFailure(
    string StageId,
    string StageLabel,
    string ProjectOrFile,
    string FailingTestName,
    string ErrorExcerpt,
    string LogPath,
    string Summary,
    int ExitCode);

public sealed record ValidationRetryAudit(
    string StageId,
    string StageLabel,
    string CommandLine,
    string RetryLogPath,
    string Result,
    string FinalClassification,
    string Summary,
    int ExitCode,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record ValidationStabilityReport(
    string RunId,
    string ActionLabel,
    string Classification,
    string ConfidenceStatus,
    string OutputFolder,
    ValidationFirstFailure? FirstFailure,
    IReadOnlyList<ValidationRetryAudit> RetryAudits,
    IReadOnlyList<ValidationStageResult> StageResults,
    DateTimeOffset RecordedUtc);

public sealed record ValidationHistoryStageOutcome(
    string StageId,
    string StageLabel,
    string Status,
    string StabilityClassification,
    bool RetryUsed);

public sealed record ValidationHistoryEntry(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    string ResultArtifactPath,
    string StabilityArtifactPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string OverallResult,
    string StabilityClassification,
    string StabilityStatus,
    string FirstFailureSummary,
    string FirstFailureStage,
    string FailingTestName,
    bool RetryUsed,
    int RetryCount,
    IReadOnlyList<ValidationHistoryStageOutcome> StageOutcomes);

public sealed record ValidationHistoryLedger(
    int RetentionCount,
    IReadOnlyList<ValidationHistoryEntry> Entries);

public sealed record ValidationTrendSummary(
    int HistoryCount,
    int PassCount,
    int RecentPassRatePercent,
    int StablePassCount,
    int StablePassRatePercent,
    bool CountRetryPassesAsStableInSummaries,
    int PassedOnRetryCount,
    int FlakySuspectedCount,
    string MostCommonFailingStage,
    DateTimeOffset? LastCleanPassUtc,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationRegressionSummary(
    string Classification,
    IReadOnlyList<string> Reasons,
    int ComparisonWindow,
    string FailureNovelty,
    string CurrentFailingStage,
    string CurrentFailingTestName,
    string LatestRunId,
    string LatestValidationResultPath,
    string LatestStabilityArtifactPath,
    string HistoryLedgerPath,
    string TrendSummaryPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationReleaseBaseline(
    string BaselineId,
    DateTimeOffset CapturedUtc,
    string CommitHash,
    string SourceRunId,
    string SourceResultArtifactPath,
    string SourceStabilityArtifactPath,
    string SourceOutputFolder,
    string OverallResult,
    string StabilityClassification,
    IReadOnlyList<ValidationHistoryStageOutcome> StageOutcomes,
    IReadOnlyDictionary<string, string> StageOutcomeMap,
    ValidationTrendSummary TrendSnapshot,
    string Status,
    string SourceSummary);

public sealed record ValidationReleaseBaselineHistory(
    int RetentionCount,
    string ActiveBaselineId,
    IReadOnlyList<ValidationReleaseBaseline> Entries);

public sealed record ValidationBaselineStageChange(
    string StageId,
    string StageLabel,
    string BaselineStatus,
    string LatestStatus,
    string BaselineStabilityClassification,
    string LatestStabilityClassification);

public sealed record ValidationBaselineComparison(
    string BaselineId,
    string BaselineSourceRunId,
    string BaselineArtifactPath,
    string BaselineCommitHash,
    string LatestRunId,
    string LatestResultArtifactPath,
    string LatestStabilityArtifactPath,
    string DriftClassification,
    IReadOnlyList<string> DriftReasons,
    IReadOnlyList<string> ChangedFailingStages,
    IReadOnlyList<ValidationBaselineStageChange> StageChanges,
    string ReadinessClassification,
    IReadOnlyList<string> ReadinessReasons,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationHandoffFirstFailure(
    string StageLabel,
    string ErrorExcerpt,
    string LogPath,
    string ProjectOrFile,
    string FailingTestName);

public sealed record ValidationHandoffRetryUsage(
    int RetryCount,
    IReadOnlyList<string> RetriedStages);

public sealed record ValidationHandoffArtifactReference(
    string Label,
    string Path);

public sealed record ValidationHandoffBundleComparison(
    string PreviousRunId,
    string ResultChange,
    string ReadinessChange,
    string StabilityChange,
    string FirstFailureStageChange,
    string Summary);

public sealed record ValidationHandoffBundle(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string OverallResult,
    string Summary,
    string StabilityClassification,
    string StabilityStatus,
    string ReadinessClassification,
    string ReadinessSummary,
    ValidationHandoffFirstFailure? FirstFailure,
    ValidationHandoffRetryUsage RetryUsage,
    IReadOnlyList<string> BlockedStageNotes,
    IReadOnlyList<ValidationHandoffArtifactReference> ArtifactPaths,
    string ResultArtifactPath,
    string StabilityArtifactPath,
    string TrendArtifactPath,
    string RegressionArtifactPath,
    string OrchestrationArtifactPath,
    string OrchestrationNotePath,
    string BaselineComparisonArtifactPath,
    string ActiveBaselineArtifactPath,
    string BundlePath,
    string SummaryPath,
    ValidationHandoffBundleComparison? PreviousBundleComparison);

public sealed record ValidationHandoffHistoryEntry(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    string BundlePath,
    string SummaryPath,
    DateTimeOffset CompletedUtc,
    string OverallResult,
    string StabilityClassification,
    string ReadinessClassification,
    string FirstFailureStage,
    string FirstFailureExcerpt);

public sealed record ValidationHandoffHistory(
    int RetentionCount,
    IReadOnlyList<ValidationHandoffHistoryEntry> Entries);

public sealed record ValidationFollowupIntake(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    DateTimeOffset CompletedUtc,
    string OverallResult,
    string ReadinessClassification,
    string StabilityClassification,
    string StabilityStatus,
    ValidationHandoffFirstFailure? FirstFailure,
    IReadOnlyList<string> BlockedStageNotes,
    string FollowupCategory,
    string NextStep,
    bool HasRecentRepeatedIssue,
    string RepeatedIssueSummary,
    string IssueFingerprint,
    string HandoffBundlePath,
    string HandoffSummaryPath,
    string IntakePath,
    string PromptPath,
    IReadOnlyList<ValidationHandoffArtifactReference> ArtifactPaths);

public sealed record ValidationFollowupHistoryEntry(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    DateTimeOffset CompletedUtc,
    string OverallResult,
    string ReadinessClassification,
    string StabilityClassification,
    string FollowupCategory,
    string NextStep,
    string FirstFailureStage,
    string FirstFailureExcerpt,
    bool HasRecentRepeatedIssue,
    string RepeatedIssueSummary,
    string IssueFingerprint,
    string IntakePath,
    string PromptPath,
    string HandoffBundlePath);

public sealed record ValidationFollowupHistory(
    int RetentionCount,
    IReadOnlyList<ValidationFollowupHistoryEntry> Entries);

public sealed record ValidationFollowupPlanStep(
    int Order,
    string StepType,
    string Title,
    string Summary,
    string TargetScope,
    string ScopeConfidence,
    IReadOnlyList<string> EvidenceArtifactPaths,
    string InteractionMode = "manual_only",
    string ActionKind = "",
    string ActionTarget = "",
    string CommandSummary = "");

public sealed record ValidationFollowupPlan(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    DateTimeOffset GeneratedUtc,
    string SourceIntakePath,
    string FollowupCategory,
    IReadOnlyList<ValidationFollowupPlanStep> Steps,
    IReadOnlyList<string> TargetScopes,
    string TargetScopeSummary,
    string ScopeConfidence,
    IReadOnlyList<string> RequiredEvidencePaths,
    string RerunScopeRecommendation,
    IReadOnlyList<string> RelatedArtifactPaths,
    string EscalationHint,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    string PlanPath);

public sealed record ValidationRepairPrepSuggestion(
    string SuggestionKind,
    string ContextKind,
    string Title,
    string Summary,
    string Outcome,
    string RankingLabel,
    string MatchExplanation,
    string SourceRunId,
    string PrimaryArtifactPath,
    IReadOnlyList<string> LinkedArtifactPaths);

public sealed record ValidationRepairPrepBundle(
    string RunId,
    string OutputFolder,
    string FollowupCategory,
    string SourceIntakePath,
    string SourcePlanPath,
    string HandoffBundlePath,
    string FirstFailureStage,
    string FirstFailureExcerpt,
    IReadOnlyList<string> TargetScopes,
    string TargetScopeSummary,
    string ScopeConfidence,
    string EscalationHint,
    IReadOnlyList<ValidationHandoffArtifactReference> KeyArtifactPaths,
    IReadOnlyList<ValidationRepairPrepSuggestion> SimilarCaseSuggestions,
    IReadOnlyList<ValidationRepairPrepSuggestion> PlaybookSuggestions,
    string BundlePath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationFollowupPlanStepState(
    int Order,
    string StepType,
    string CompletionState,
    string LastActionKind,
    string Detail,
    string EvidencePath,
    DateTimeOffset UpdatedUtc);

public sealed record ValidationFollowupRerunLinkage(
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceFollowupIntakePath,
    string SourceFollowupPlanPath,
    string SourceOutputFolder,
    int StepOrder,
    string StepType,
    string RerunAction,
    string RerunActionLabel,
    string RerunCommandSummary,
    string RerunValidationRunId,
    string RerunValidationOutputFolder,
    string ResultArtifactPath,
    string StabilityArtifactPath,
    string OutcomeClassification,
    string OutcomeSummary,
    DateTimeOffset RecordedUtc);

public sealed record ValidationFollowupExecutionState(
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceFollowupIntakePath,
    string SourceFollowupPlanPath,
    string SourceOutputFolder,
    DateTimeOffset RecordedUtc,
    IReadOnlyList<ValidationFollowupPlanStepState> Steps,
    ValidationFollowupRerunLinkage? LatestRerun);

public sealed record ValidationFollowupExecutionOutcome(
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceFollowupIntakePath,
    string SourceFollowupPlanPath,
    string SourceOutputFolder,
    string IssueKey,
    string SourceStepKey,
    int SourceStepOrder,
    string SourceStepType,
    string SourceStepTitle,
    string SourceStageLabel,
    string SourceStageStatus,
    string RerunValidationRunId,
    string RerunValidationOutputFolder,
    string RerunStageLabel,
    string RerunStageStatus,
    string RerunResultSummary,
    string ComparisonScope,
    string ComparisonSummary,
    string OutcomeClassification,
    string OutcomeSummary,
    string RecommendedNextState,
    string RecommendedNextAction,
    bool HasRecordedRerun,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    IReadOnlyList<string> ExecutedStepKeys,
    IReadOnlyList<ValidationHandoffArtifactReference> LinkedArtifactPaths,
    string OutcomePath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationFollowupEscalationEvidence(
    string SourceValidationRunId,
    string RerunValidationRunId,
    string OutcomeClassification,
    string OutcomeSummary,
    string OutcomePath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationFollowupEscalation(
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceOutputFolder,
    string IssueKey,
    string CurrentOutcomeClassification,
    string CurrentOutcomeSummary,
    string CurrentRecommendedNextState,
    string CurrentRecommendedNextAction,
    int RepeatedUnresolvedCount,
    IReadOnlyList<ValidationFollowupEscalationEvidence> RepeatedEvidence,
    string EscalationClassification,
    string EscalationSummary,
    string SuggestedNextState,
    string SuggestedNextAction,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    IReadOnlyList<ValidationHandoffArtifactReference> LinkedArtifactPaths,
    string EscalationPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationFollowupResolutionReview(
    string ReviewId,
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceFollowupIntakePath,
    string SourceFollowupPlanPath,
    string SourceGuidedOutcomePath,
    string SourceOutputFolder,
    string IssueKey,
    string OriginalFailureStage,
    string OriginalFailureExcerpt,
    string OriginalFailureSummary,
    string GuidedOutcomeClassification,
    string GuidedOutcomeSummary,
    string ResolutionClassification,
    string CurrentResolutionState,
    string IssueClosureStatus,
    string ResolutionSummary,
    string ReopenStatus,
    string ReopenSummary,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    IReadOnlyList<ValidationHandoffArtifactReference> EvidenceChain,
    string ReviewPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationResolutionHandoff(
    string HandoffId,
    string ReviewId,
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceOutputFolder,
    string ResolutionClassification,
    string CurrentResolutionState,
    string IssueClosureStatus,
    string GuidedOutcomeClassification,
    string CandidateState,
    IReadOnlyList<string> Reasons,
    string CandidateSummary,
    string ReopenStatus,
    string ReopenSummary,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    string BaselineComparisonArtifactPath,
    string HandoffBundlePath,
    string HandoffSummaryPath,
    string FollowupIntakePath,
    string FollowupPlanPath,
    string GuidedOutcomePath,
    string ResolutionReviewPath,
    IReadOnlyList<ValidationHandoffArtifactReference> LinkedArtifactPaths,
    string HandoffPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationResolutionPromotionReview(
    string PromotionReviewId,
    string SourceValidationRunId,
    string SourceFollowupCategory,
    string SourceFollowupIntakePath,
    string SourceFollowupPlanPath,
    string SourceGuidedOutcomePath,
    string SourceResolutionReviewId,
    string SourceResolutionReviewPath,
    string SourceResolutionHandoffPath,
    string SourceOutputFolder,
    string CurrentResolutionState,
    string ResolutionClassification,
    string HandoffCandidateState,
    string LatestValidationRunId,
    string LatestValidationResultPath,
    string ActiveBaselineArtifactPath,
    string BaselineComparisonArtifactPath,
    string CurrentReadinessClassification,
    string CurrentDriftClassification,
    string PromotionRecommendationState,
    string PromotionRecommendationSummary,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    IReadOnlyList<ValidationHandoffArtifactReference> EvidenceChain,
    string PromotionReviewPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationReleaseDecisionSummary(
    string DecisionSummaryId,
    string SourceValidationRunId,
    string SourceOutputFolder,
    string SourcePromotionReviewPath,
    string SourceResolutionReviewPath,
    string SourceResolutionHandoffPath,
    string LatestValidationRunId,
    string LatestValidationResultPath,
    string ActiveBaselineId,
    string ActiveBaselineArtifactPath,
    string BaselineComparisonArtifactPath,
    string ResolutionState,
    string HandoffCandidateState,
    string PromotionRecommendationState,
    string CurrentReadinessClassification,
    string CurrentDriftClassification,
    string DecisionState,
    string DecisionSummaryText,
    IReadOnlyList<string> ContradictionNotes,
    IReadOnlyList<string> DeferralNotes,
    bool IsLatestForRepo,
    string FreshnessStatus,
    string FreshnessSummary,
    IReadOnlyList<ValidationHandoffArtifactReference> LinkedArtifactPaths,
    string DecisionSummaryPath,
    DateTimeOffset GeneratedUtc);

public sealed record ValidationWorkspaceImpactMetadata(
    bool TouchesBuildOutputs,
    bool ClearsCaches,
    bool RewritesArtifacts,
    bool ReadsOnly);

public sealed record ValidationStageOrchestrationEntry(
    string StageId,
    string StageLabel,
    IReadOnlyList<string> DependsOnStageIds,
    IReadOnlyList<string> ConcurrencyClassifications,
    bool CanRunIndependently,
    ValidationWorkspaceImpactMetadata WorkspaceImpact,
    string WorkingDirectory,
    bool SupportsIsolatedWorkspace,
    string IsolationSupportStatus,
    string IsolationSupportReason);

public sealed record ValidationOrchestrationDecision(
    string DecisionType,
    string StageId,
    string StageLabel,
    string Summary);

public sealed record ValidationOrchestrationReport(
    string RunId,
    string ActionLabel,
    string RunMode,
    string RepoRoot,
    string OutputFolder,
    string PolicyNotePath,
    string IsolatedWorkspacePath,
    IReadOnlyList<ValidationStageOrchestrationEntry> Stages,
    IReadOnlyList<ValidationOrchestrationDecision> Decisions,
    DateTimeOffset RecordedUtc);

public sealed record ValidationActionOrchestrationPolicy(
    ValidationAction Action,
    string ActionLabel,
    string RunMode,
    IReadOnlyList<ValidationCommandSpec> Stages,
    IReadOnlyList<string> ConcurrencyClassifications,
    ValidationWorkspaceImpactMetadata WorkspaceImpact,
    bool SupportsIsolatedWorkspace,
    string IsolationSupportStatus,
    string IsolationSupportReason);

public sealed record ValidationRunResult(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    bool Success,
    string Summary,
    string? FirstFailureText,
    string? FirstFailureLogPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<ValidationStageResult> Stages,
    string StabilityClassification = "passed",
    string StabilityStatus = "Passed cleanly",
    ValidationFirstFailure? FirstFailure = null,
    IReadOnlyList<ValidationRetryAudit>? RetryAudits = null,
    string? StabilityArtifactPath = null,
    string RunMode = "sequential_standard_mode",
    string? OrchestrationArtifactPath = null,
    string? IsolatedWorkspacePath = null);

public sealed record ValidationRunSummary(
    string RunId,
    string ActionLabel,
    string OutputFolder,
    bool Success,
    string Summary,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string StabilityClassification = "passed",
    string StabilityStatus = "Passed cleanly");

public sealed record ValidationCommandSpec(
    string StageId,
    string StageLabel,
    string FileName,
    IReadOnlyList<string> Arguments,
    string LogFileName,
    IReadOnlyList<string>? DependsOnStageIds = null,
    IReadOnlyList<string>? ConcurrencyClassifications = null,
    bool CanRunIndependently = true,
    bool TouchesBuildOutputs = false,
    bool ClearsCaches = false,
    bool RewritesArtifacts = false,
    bool ReadsOnly = false,
    bool SupportsIsolatedWorkspace = false,
    string IsolationSupportStatus = "not_requested",
    string IsolationSupportReason = "");

public sealed record ValidationCommandExecutionResult(
    int ExitCode,
    IReadOnlyList<string> OutputLines);

public interface IValidationCommandExecutor
{
    Task<ValidationCommandExecutionResult> ExecuteAsync(
        ValidationCommandSpec command,
        string workingDirectory,
        string logPath,
        Action<string> onOutput,
        CancellationToken ct);
}

public sealed class ValidationRunnerService : IValidationRunnerService
{
    private const string ValidationArtifactsFolderName = "validation-ui";
    private const string ResultFileName = "validation_result.json";
    private const string StabilityFileName = "validation_stability.json";
    private const string HistoryLedgerFileName = "validation_history_ledger.json";
    private const string TrendSummaryFileName = "validation_trends.json";
    private const string RegressionSummaryFileName = "validation_regression_summary.json";
    private const string ActiveBaselineFileName = "validation_release_baseline.json";
    private const string BaselineHistoryFileName = "validation_release_baseline_history.json";
    private const string BaselineComparisonFileName = "validation_baseline_comparison.json";
    private const string OrchestrationFileName = "validation_orchestration.json";
    private const string OrchestrationPolicyNoteFileName = "validation_orchestration_policy.md";
    private const string HandoffBundleFileName = "validation_handoff_bundle.json";
    private const string HandoffSummaryFileName = "validation_handoff_summary.md";
    private const string HandoffHistoryFileName = "validation_handoff_history.json";
    private const string FollowupIntakeFileName = "validation_followup_intake.json";
    private const string FollowupPromptFileName = "validation_followup_prompt.txt";
    private const string FollowupHistoryFileName = "validation_followup_intake_history.json";
    private const string FollowupPlanFileName = "validation_followup_plan.json";
    private const string RepairPrepBundleFileName = "validation_repair_prep_bundle.json";
    private const string FollowupExecutionFileName = "validation_followup_execution.json";
    private const string FollowupExecutionOutcomeFileName = "validation_followup_execution_outcome.json";
    private const string FollowupEscalationFileName = "validation_followup_escalation.json";
    private const string FollowupResolutionReviewFileName = "validation_followup_resolution_review.json";
    private const string ResolutionHandoffFileName = "validation_resolution_handoff.json";
    private const string ResolutionPromotionReviewFileName = "validation_resolution_promotion_review.json";
    private const string ReleaseDecisionSummaryFileName = "validation_release_decision_summary.json";
    private static readonly Regex XunitFailurePattern = new(@"\]\s+(?<name>\S+)\s+\[FAIL\]", RegexOptions.Compiled);
    private static readonly Regex VstestFailurePattern = new(@"^\s*Failed\s+(?<name>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TestRunPattern = new(@"^Test run for (?<path>.+?) \(", RegexOptions.Compiled);
    private static readonly Regex ErrorPattern = new(@"error [A-Z]{2,}[0-9]+|: error |Unhandled exception|Exception:|\[FAIL\]|^Failed!?|^\[xUnit\.net", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IValidationCommandExecutor _executor;

    private sealed record ValidationFailureAnalysis(
        string ProjectOrFile,
        string FailingTestName,
        string ErrorExcerpt,
        string Summary);

    private sealed record FollowupTargetScopeAssessment(
        IReadOnlyList<string> Scopes,
        string ScopeSummary,
        string ScopeConfidence);

    private sealed record RankedFollowupSuggestion(
        ValidationRepairPrepSuggestion Suggestion,
        double Score);

    public ValidationRunnerService(string? repoRoot, IValidationCommandExecutor? executor)
    {
        RepoRoot = ResolveRepoRoot(repoRoot);
        ValidationRunsRoot = Path.Combine(ValidationArtifactsRootForRepo(RepoRoot), "runs");
        _executor = executor ?? new ValidationCommandExecutor();
    }

    public ValidationRunnerService(string? repoRoot = null)
        : this(repoRoot, null)
    {
    }

    public string RepoRoot { get; }

    public string ValidationRunsRoot { get; }

    public static string ValidationArtifactsRootForRepo(string repoRoot)
        => Path.Combine(ResolveRepoRoot(repoRoot), ".codex", ValidationArtifactsFolderName);

    public static string HistoryLedgerPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), HistoryLedgerFileName);

    public static string TrendSummaryPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), TrendSummaryFileName);

    public static string RegressionSummaryPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), RegressionSummaryFileName);

    public static string ActiveBaselinePathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), ActiveBaselineFileName);

    public static string BaselineHistoryPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), BaselineHistoryFileName);

    public static string BaselineComparisonPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), BaselineComparisonFileName);

    public static string OrchestrationPolicyNotePathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), OrchestrationPolicyNoteFileName);

    public static string OrchestrationPathForRun(string outputFolder)
        => Path.Combine(outputFolder, OrchestrationFileName);

    public static string HandoffBundlePathForRun(string outputFolder)
        => Path.Combine(outputFolder, HandoffBundleFileName);

    public static string HandoffSummaryPathForRun(string outputFolder)
        => Path.Combine(outputFolder, HandoffSummaryFileName);

    public static string HandoffHistoryPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), HandoffHistoryFileName);

    public static string FollowupIntakePathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupIntakeFileName);

    public static string FollowupPromptPathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupPromptFileName);

    public static string FollowupHistoryPathForRepo(string repoRoot)
        => Path.Combine(ValidationArtifactsRootForRepo(repoRoot), FollowupHistoryFileName);

    public static string FollowupPlanPathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupPlanFileName);

    public static string RepairPrepBundlePathForRun(string outputFolder)
        => Path.Combine(outputFolder, RepairPrepBundleFileName);

    public static string FollowupExecutionPathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupExecutionFileName);

    public static string FollowupExecutionOutcomePathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupExecutionOutcomeFileName);

    public static string FollowupEscalationPathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupEscalationFileName);

    public static string FollowupResolutionReviewPathForRun(string outputFolder)
        => Path.Combine(outputFolder, FollowupResolutionReviewFileName);

    public static string ResolutionHandoffPathForRun(string outputFolder)
        => Path.Combine(outputFolder, ResolutionHandoffFileName);

    public static string ResolutionPromotionReviewPathForRun(string outputFolder)
        => Path.Combine(outputFolder, ResolutionPromotionReviewFileName);

    public static string ReleaseDecisionSummaryPathForRun(string outputFolder)
        => Path.Combine(outputFolder, ReleaseDecisionSummaryFileName);

    public static ValidationActionOrchestrationPolicy DescribeAction(ValidationAction action, ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var runMode = DetermineRunMode(action, normalized);
        var stages = BuildCommands(action, normalized.IncludeValidateBuild, runMode).ToArray();
        return new ValidationActionOrchestrationPolicy(
            action,
            DisplayLabel(action),
            runMode,
            stages,
            stages.SelectMany(stage => stage.ConcurrencyClassifications ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            new ValidationWorkspaceImpactMetadata(
                stages.Any(stage => stage.TouchesBuildOutputs),
                stages.Any(stage => stage.ClearsCaches),
                stages.Any(stage => stage.RewritesArtifacts),
                stages.All(stage => stage.ReadsOnly)),
            stages.All(stage => stage.SupportsIsolatedWorkspace),
            stages.All(stage => stage.SupportsIsolatedWorkspace) ? "supported" : "deferred",
            BuildIsolationSummary(action, runMode, stages));
    }

    public static ValidationHistoryLedger LoadHistoryLedger(string repoRoot)
        => TryLoadArtifact(HistoryLedgerPathForRepo(repoRoot), new ValidationHistoryLedger(0, Array.Empty<ValidationHistoryEntry>()));

    public static ValidationTrendSummary LoadTrendSummary(string repoRoot)
        => TryLoadArtifact(
            TrendSummaryPathForRepo(repoRoot),
            new ValidationTrendSummary(0, 0, 0, 0, 0, false, 0, 0, string.Empty, null, DateTimeOffset.MinValue));

    public static ValidationRegressionSummary LoadRegressionSummary(string repoRoot)
        => TryLoadArtifact(
            RegressionSummaryPathForRepo(repoRoot),
            new ValidationRegressionSummary(
                "no_history",
                new[] { "No validation history recorded." },
                0,
                "none",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                HistoryLedgerPathForRepo(repoRoot),
                TrendSummaryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static ValidationReleaseBaseline? LoadActiveReleaseBaseline(string repoRoot)
        => TryLoadArtifact<ValidationReleaseBaseline?>(ActiveBaselinePathForRepo(repoRoot), null);

    public static ValidationReleaseBaselineHistory LoadBaselineHistory(string repoRoot)
        => TryLoadArtifact(
            BaselineHistoryPathForRepo(repoRoot),
            new ValidationReleaseBaselineHistory(0, string.Empty, Array.Empty<ValidationReleaseBaseline>()));

    public static ValidationBaselineComparison LoadBaselineComparison(string repoRoot)
        => TryLoadArtifact(
            BaselineComparisonPathForRepo(repoRoot),
            new ValidationBaselineComparison(
                string.Empty,
                string.Empty,
                ActiveBaselinePathForRepo(repoRoot),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "no_baseline",
                new[] { "No active release baseline recorded." },
                Array.Empty<string>(),
                Array.Empty<ValidationBaselineStageChange>(),
                "not_ready",
                new[] { "No release readiness assessment recorded." },
                DateTimeOffset.MinValue));

    public static ValidationHandoffHistory LoadHandoffHistory(string repoRoot)
        => TryLoadArtifact(
            HandoffHistoryPathForRepo(repoRoot),
            new ValidationHandoffHistory(0, Array.Empty<ValidationHandoffHistoryEntry>()));

    public static ValidationHandoffBundle? LoadHandoffBundleForRun(string outputFolder)
        => TryLoadArtifact<ValidationHandoffBundle?>(HandoffBundlePathForRun(outputFolder), null);

    public static ValidationHandoffBundle? LoadLatestHandoffBundle(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadHandoffBundleForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupHistory LoadFollowupHistory(string repoRoot)
        => TryLoadArtifact(
            FollowupHistoryPathForRepo(repoRoot),
            new ValidationFollowupHistory(0, Array.Empty<ValidationFollowupHistoryEntry>()));

    public static ValidationFollowupIntake? LoadFollowupIntakeForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupIntake?>(FollowupIntakePathForRun(outputFolder), null);

    public static ValidationFollowupIntake? LoadLatestFollowupIntake(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupIntakeForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupPlan? LoadFollowupPlanForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupPlan?>(FollowupPlanPathForRun(outputFolder), null);

    public static ValidationFollowupPlan? LoadLatestFollowupPlan(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupPlanForRun(latestRun.OutputFolder);
    }

    public static ValidationRepairPrepBundle? LoadRepairPrepBundleForRun(string outputFolder)
        => TryLoadArtifact<ValidationRepairPrepBundle?>(RepairPrepBundlePathForRun(outputFolder), null);

    public static ValidationRepairPrepBundle? LoadLatestRepairPrepBundle(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadRepairPrepBundleForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupExecutionState? LoadFollowupExecutionStateForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupExecutionState?>(FollowupExecutionPathForRun(outputFolder), null);

    public static ValidationFollowupExecutionState? LoadLatestFollowupExecutionState(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupExecutionStateForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupExecutionOutcome? LoadFollowupExecutionOutcomeForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupExecutionOutcome?>(FollowupExecutionOutcomePathForRun(outputFolder), null);

    public static ValidationFollowupExecutionOutcome? LoadLatestFollowupExecutionOutcome(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupExecutionOutcomeForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupEscalation? LoadFollowupEscalationForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupEscalation?>(FollowupEscalationPathForRun(outputFolder), null);

    public static ValidationFollowupEscalation? LoadLatestFollowupEscalation(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupEscalationForRun(latestRun.OutputFolder);
    }

    public static ValidationFollowupResolutionReview? LoadFollowupResolutionReviewForRun(string outputFolder)
        => TryLoadArtifact<ValidationFollowupResolutionReview?>(FollowupResolutionReviewPathForRun(outputFolder), null);

    public static ValidationFollowupResolutionReview? LoadLatestFollowupResolutionReview(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadFollowupResolutionReviewForRun(latestRun.OutputFolder);
    }

    public static ValidationResolutionHandoff? LoadResolutionHandoffForRun(string outputFolder)
        => TryLoadArtifact<ValidationResolutionHandoff?>(ResolutionHandoffPathForRun(outputFolder), null);

    public static ValidationResolutionHandoff? LoadLatestResolutionHandoff(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadResolutionHandoffForRun(latestRun.OutputFolder);
    }

    public static ValidationResolutionPromotionReview? LoadResolutionPromotionReviewForRun(string outputFolder)
        => TryLoadArtifact<ValidationResolutionPromotionReview?>(ResolutionPromotionReviewPathForRun(outputFolder), null);

    public static ValidationResolutionPromotionReview? LoadLatestResolutionPromotionReview(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadResolutionPromotionReviewForRun(latestRun.OutputFolder);
    }

    public static ValidationReleaseDecisionSummary? LoadReleaseDecisionSummaryForRun(string outputFolder)
        => TryLoadArtifact<ValidationReleaseDecisionSummary?>(ReleaseDecisionSummaryPathForRun(outputFolder), null);

    public static ValidationReleaseDecisionSummary? LoadLatestReleaseDecisionSummary(string repoRoot)
    {
        var latestRun = LoadLatestRunResult(repoRoot);
        return latestRun is null
            ? null
            : LoadReleaseDecisionSummaryForRun(latestRun.OutputFolder);
    }

    public static ValidationRunResult? LoadRunResultForOutputFolder(string outputFolder)
        => TryLoadArtifact<ValidationRunResult?>(Path.Combine(outputFolder, ResultFileName), null);

    public static ValidationRunResult? LoadLatestRunResult(string repoRoot)
    {
        var runsRoot = Path.Combine(ValidationArtifactsRootForRepo(repoRoot), "runs");
        if (!Directory.Exists(runsRoot))
            return null;

        foreach (var directory in Directory.GetDirectories(runsRoot).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var resultPath = Path.Combine(directory, ResultFileName);
            try
            {
                if (!File.Exists(resultPath))
                    continue;

                var run = JsonSerializer.Deserialize<ValidationRunResult>(File.ReadAllText(resultPath), JsonOptions());
                if (run is not null)
                    return run;
            }
            catch
            {
                // Keep malformed run history non-blocking.
            }
        }

        return null;
    }

    public static void RefreshTrendArtifacts(string repoRoot, ValidationSettings settings)
    {
        var artifactsRoot = ValidationArtifactsRootForRepo(repoRoot);
        var historyPath = HistoryLedgerPathForRepo(repoRoot);
        if (!File.Exists(historyPath) && !Directory.Exists(artifactsRoot))
            return;

        var normalized = settings.Normalize();
        var entries = NormalizeHistoryEntries(LoadHistoryLedger(repoRoot).Entries)
            .TakeLast(normalized.HistoryRetentionCount)
            .ToArray();
        if (entries.Length == 0 && !File.Exists(historyPath))
            return;

        Directory.CreateDirectory(artifactsRoot);
        var ledger = new ValidationHistoryLedger(normalized.HistoryRetentionCount, entries);
        File.WriteAllText(historyPath, JsonSerializer.Serialize(ledger, JsonOptions()));
        WriteDerivedTrendArtifacts(repoRoot, normalized, entries);
        RefreshHandoffArtifacts(repoRoot, normalized);
    }

    public static ValidationReleaseBaseline SetActiveReleaseBaseline(
        string repoRoot,
        ValidationRunResult latestResult,
        ValidationSettings settings)
    {
        if (latestResult is null)
            throw new ArgumentNullException(nameof(latestResult));

        var normalized = settings.Normalize();
        var latestClassification = NormalizeRunClassification(latestResult);
        if (!latestResult.Success || !string.Equals(latestClassification, "passed", StringComparison.Ordinal))
            throw new InvalidOperationException("Release baselines can only be created from a clean validation result.");

        var artifactsRoot = ValidationArtifactsRootForRepo(repoRoot);
        Directory.CreateDirectory(artifactsRoot);

        var trendSnapshot = LoadTrendSummary(repoRoot);
        var baseline = new ValidationReleaseBaseline(
            latestResult.RunId,
            latestResult.CompletedUtc,
            TryGetCommitHash(repoRoot),
            latestResult.RunId,
            Path.Combine(latestResult.OutputFolder, ResultFileName),
            ResolveStabilityArtifactPath(latestResult),
            latestResult.OutputFolder,
            latestResult.Success ? "passed" : "failed",
            latestClassification,
            latestResult.Stages.Select(BuildStageOutcomeSnapshot).ToArray(),
            BuildStageOutcomeMap(latestResult.Stages),
            trendSnapshot,
            "active",
            latestResult.Summary);

        var entries = LoadBaselineHistory(repoRoot).Entries
            .Where(entry => !string.Equals(entry.BaselineId, baseline.BaselineId, StringComparison.Ordinal))
            .Select(entry => entry with { Status = "superseded" })
            .Append(baseline)
            .OrderBy(entry => entry.CapturedUtc)
            .ThenBy(entry => entry.BaselineId, StringComparer.Ordinal)
            .TakeLast(normalized.BaselineHistoryRetentionCount)
            .Select(entry => entry with
            {
                Status = string.Equals(entry.BaselineId, baseline.BaselineId, StringComparison.Ordinal)
                    ? "active"
                    : "superseded"
            })
            .ToArray();

        var history = new ValidationReleaseBaselineHistory(normalized.BaselineHistoryRetentionCount, baseline.BaselineId, entries);
        File.WriteAllText(ActiveBaselinePathForRepo(repoRoot), JsonSerializer.Serialize(baseline, JsonOptions()));
        File.WriteAllText(BaselineHistoryPathForRepo(repoRoot), JsonSerializer.Serialize(history, JsonOptions()));
        RefreshReleaseBaselineArtifacts(repoRoot, normalized, latestResult);
        return baseline;
    }

    public static void RefreshReleaseBaselineArtifacts(
        string repoRoot,
        ValidationSettings settings,
        ValidationRunResult? latestResult = null)
    {
        var normalized = settings.Normalize();
        var activeBaseline = LoadActiveReleaseBaseline(repoRoot);
        latestResult ??= LoadLatestRunResult(repoRoot);
        if (activeBaseline is null && latestResult is null)
            return;

        var comparison = BuildBaselineComparison(repoRoot, activeBaseline, latestResult, normalized);
        var artifactsRoot = ValidationArtifactsRootForRepo(repoRoot);
        Directory.CreateDirectory(artifactsRoot);
        File.WriteAllText(BaselineComparisonPathForRepo(repoRoot), JsonSerializer.Serialize(comparison, JsonOptions()));

        if (activeBaseline is not null)
        {
            var normalizedEntries = LoadBaselineHistory(repoRoot).Entries
                .Where(entry => !string.Equals(entry.BaselineId, activeBaseline.BaselineId, StringComparison.Ordinal))
                .Select(entry => entry with { Status = "superseded" })
                .Append(activeBaseline with { Status = "active" })
                .GroupBy(entry => entry.BaselineId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(entry => entry.CapturedUtc)
                .ThenBy(entry => entry.BaselineId, StringComparer.Ordinal)
                .TakeLast(normalized.BaselineHistoryRetentionCount)
                .Select(entry => entry with
                {
                    Status = string.Equals(entry.BaselineId, activeBaseline.BaselineId, StringComparison.Ordinal)
                        ? "active"
                        : "superseded"
                })
                .ToArray();
            var history = new ValidationReleaseBaselineHistory(normalized.BaselineHistoryRetentionCount, activeBaseline.BaselineId, normalizedEntries);
            File.WriteAllText(BaselineHistoryPathForRepo(repoRoot), JsonSerializer.Serialize(history, JsonOptions()));
            File.WriteAllText(ActiveBaselinePathForRepo(repoRoot), JsonSerializer.Serialize(activeBaseline with { Status = "active" }, JsonOptions()));
        }
    }

    public static void RefreshOrchestrationPolicyArtifacts(string repoRoot, ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var path = OrchestrationPolicyNotePathForRepo(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildOrchestrationPolicyNote(normalized));
    }

    public static void RefreshHandoffArtifacts(string repoRoot, ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var retainedResults = LoadRetainedRunResults(repoRoot, normalized.KeepLastRuns);
        if (retainedResults.Count == 0)
            return;

        Directory.CreateDirectory(ValidationArtifactsRootForRepo(repoRoot));

        ValidationHandoffBundle? previousBundle = null;
        var historyEntries = new List<ValidationHandoffHistoryEntry>();
        var latestRunId = retainedResults[^1].RunId;
        foreach (var result in retainedResults)
        {
            var existing = LoadHandoffBundleForRun(result.OutputFolder);
            var shouldRewrite = existing is null || string.Equals(result.RunId, latestRunId, StringComparison.Ordinal);
            var bundle = shouldRewrite
                ? CreateHandoffBundle(result, repoRoot, previousBundle)
                : existing!;
            if (shouldRewrite)
            {
                WriteHandoffBundleArtifacts(bundle);
            }

            previousBundle = bundle;
            historyEntries.Add(new ValidationHandoffHistoryEntry(
                bundle.RunId,
                bundle.ActionLabel,
                bundle.OutputFolder,
                bundle.BundlePath,
                bundle.SummaryPath,
                bundle.CompletedUtc,
                bundle.OverallResult,
                bundle.StabilityClassification,
                bundle.ReadinessClassification,
                bundle.FirstFailure?.StageLabel ?? string.Empty,
                bundle.FirstFailure?.ErrorExcerpt ?? string.Empty));
        }

        var history = new ValidationHandoffHistory(
            normalized.KeepLastRuns,
            historyEntries
                .OrderBy(entry => entry.CompletedUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray());
        File.WriteAllText(HandoffHistoryPathForRepo(repoRoot), JsonSerializer.Serialize(history, JsonOptions()));
        RefreshFollowupArtifacts(repoRoot, normalized);
    }

    public static void RefreshFollowupArtifacts(string repoRoot, ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var handoffHistory = LoadHandoffHistory(repoRoot);
        if (handoffHistory.Entries.Count == 0)
            return;

        var historyEntries = new List<ValidationFollowupHistoryEntry>();
        foreach (var handoffEntry in handoffHistory.Entries
                     .OrderBy(entry => entry.CompletedUtc)
                     .ThenBy(entry => entry.RunId, StringComparer.Ordinal))
        {
            var bundle = LoadHandoffBundleForRun(handoffEntry.OutputFolder);
            if (bundle is null)
                continue;

            var intake = CreateFollowupIntake(bundle, historyEntries);
            WriteFollowupArtifacts(intake);
            historyEntries.Add(new ValidationFollowupHistoryEntry(
                intake.RunId,
                intake.ActionLabel,
                intake.OutputFolder,
                intake.CompletedUtc,
                intake.OverallResult,
                intake.ReadinessClassification,
                intake.StabilityClassification,
                intake.FollowupCategory,
                intake.NextStep,
                intake.FirstFailure?.StageLabel ?? string.Empty,
                intake.FirstFailure?.ErrorExcerpt ?? string.Empty,
                intake.HasRecentRepeatedIssue,
                intake.RepeatedIssueSummary,
                intake.IssueFingerprint,
                intake.IntakePath,
                intake.PromptPath,
                intake.HandoffBundlePath));
        }

        var history = new ValidationFollowupHistory(
            normalized.KeepLastRuns,
            historyEntries
                .OrderBy(entry => entry.CompletedUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray());
        File.WriteAllText(FollowupHistoryPathForRepo(repoRoot), JsonSerializer.Serialize(history, JsonOptions()));
        RefreshFollowupPlanArtifacts(repoRoot, normalized);
    }

    public IReadOnlyList<string> GetStageLabels(ValidationAction action, bool includeValidateBuild)
        => BuildCommands(
                action,
                includeValidateBuild,
                action == ValidationAction.RunFullValidationLoop
                    ? "sequential_standard_mode"
                    : "single_stage_manual_mode")
            .Select(command => command.StageLabel)
            .ToArray();

    public IReadOnlyList<ValidationRunSummary> LoadRecentRuns(int maxCount)
    {
        if (maxCount <= 0 || !Directory.Exists(ValidationRunsRoot))
            return Array.Empty<ValidationRunSummary>();

        var summaries = new List<ValidationRunSummary>();
        foreach (var directory in Directory.GetDirectories(ValidationRunsRoot).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var resultPath = Path.Combine(directory, ResultFileName);
            if (!File.Exists(resultPath))
                continue;

            try
            {
                var run = JsonSerializer.Deserialize<ValidationRunResult>(File.ReadAllText(resultPath));
                if (run is null)
                    continue;

                summaries.Add(new ValidationRunSummary(
                    run.RunId,
                    run.ActionLabel,
                    run.OutputFolder,
                    run.Success,
                    run.Summary,
                    run.StartedUtc,
                    run.CompletedUtc,
                    string.IsNullOrWhiteSpace(run.StabilityClassification)
                        ? (run.Success ? "passed" : "failed")
                        : run.StabilityClassification,
                    string.IsNullOrWhiteSpace(run.StabilityStatus)
                        ? ToStabilityStatus(string.IsNullOrWhiteSpace(run.StabilityClassification)
                            ? (run.Success ? "passed" : "failed")
                            : run.StabilityClassification)
                        : run.StabilityStatus));
            }
            catch
            {
                // Keep malformed history non-blocking.
            }

            if (summaries.Count >= maxCount)
                break;
        }

        return summaries;
    }

    public async Task<ValidationRunResult> RunAsync(
        ValidationAction action,
        ValidationSettings settings,
        Action<ValidationProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        EnsureRepoRoot();

        var normalized = settings.Normalize();
        RefreshOrchestrationPolicyArtifacts(RepoRoot, normalized);
        var startedUtc = DateTimeOffset.UtcNow;
        var runId = $"{startedUtc:yyyyMMdd-HHmmssfffZ}-{ActionToken(action)}";
        var outputFolder = Path.Combine(ValidationRunsRoot, runId);
        Directory.CreateDirectory(outputFolder);
        var runMode = DetermineRunMode(action, normalized);
        var commands = BuildCommands(action, normalized.IncludeValidateBuild, runMode).ToArray();
        var isolatedWorkspacePath = ShouldUseIsolatedWorkspace(action, normalized)
            ? CreateIsolatedWorkspace(RepoRoot, Path.Combine(outputFolder, "isolated-workspace"))
            : string.Empty;
        var orchestrationPath = OrchestrationPathForRun(outputFolder);
        var orchestrationPolicyPath = OrchestrationPolicyNotePathForRepo(RepoRoot);
        var orchestration = BuildOrchestrationReport(
            runId,
            action,
            RepoRoot,
            outputFolder,
            runMode,
            isolatedWorkspacePath,
            orchestrationPolicyPath,
            commands);
        File.WriteAllText(orchestrationPath, JsonSerializer.Serialize(orchestration, JsonOptions()));

        progress?.Invoke(new ValidationProgressEvent(
            "run_started",
            string.Empty,
            DisplayLabel(action),
            "active",
            BuildRunStartedMessage(outputFolder, runMode, isolatedWorkspacePath, orchestration.Decisions),
            null,
            null,
            outputFolder,
            startedUtc));

        var stages = new List<ValidationStageResult>();
        var retryAudits = new List<ValidationRetryAudit>();
        ValidationFirstFailure? firstFailure = null;
        string? firstFailureText = null;
        string? firstFailureLogPath = null;

        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();

            var logPath = Path.Combine(outputFolder, command.LogFileName);
            var workingDirectory = ResolveWorkingDirectory(RepoRoot, isolatedWorkspacePath, runMode, command);
            var stageStartedUtc = DateTimeOffset.UtcNow;
            progress?.Invoke(new ValidationProgressEvent(
                "stage_started",
                command.StageId,
                command.StageLabel,
                "active",
                BuildStageStartedMessage(command, runMode, workingDirectory),
                null,
                logPath,
                outputFolder,
                stageStartedUtc));

            var execution = await _executor.ExecuteAsync(
                command,
                workingDirectory,
                logPath,
                line => progress?.Invoke(new ValidationProgressEvent(
                    "output",
                    command.StageId,
                    command.StageLabel,
                    "active",
                    $"{command.StageLabel}: {line}",
                    line,
                    logPath,
                    outputFolder,
                    DateTimeOffset.UtcNow)),
                ct).ConfigureAwait(false);

            var durationMs = Math.Max(0L, (long)(DateTimeOffset.UtcNow - stageStartedUtc).TotalMilliseconds);
            var failureAnalysis = execution.ExitCode == 0
                ? null
                : AnalyzeFailure(command, execution);
            var summary = execution.ExitCode == 0
                ? SummarizeSuccess(command.StageLabel, execution)
                : failureAnalysis?.Summary ?? SummarizeFailure(command.StageLabel, execution);
            var succeeded = execution.ExitCode == 0;
            var finalExitCode = execution.ExitCode;
            var stabilityClassification = succeeded ? "passed" : "failed";
            var retryCount = 0;
            string? retryLogPath = null;

            if (!succeeded && firstFailure is null)
            {
                firstFailure = new ValidationFirstFailure(
                    command.StageId,
                    command.StageLabel,
                    failureAnalysis?.ProjectOrFile ?? ResolveCommandTarget(command),
                    failureAnalysis?.FailingTestName ?? string.Empty,
                    failureAnalysis?.ErrorExcerpt ?? summary,
                    logPath,
                    failureAnalysis?.Summary ?? summary,
                    execution.ExitCode);
                firstFailureText = firstFailure.ErrorExcerpt;
                firstFailureLogPath = logPath;
            }

            if (!succeeded && normalized.EnableStabilityRetry)
            {
                retryCount = 1;
                retryLogPath = Path.Combine(
                    outputFolder,
                    $"{Path.GetFileNameWithoutExtension(command.LogFileName)}.retry1.log");
                progress?.Invoke(new ValidationProgressEvent(
                    "stage_retry_started",
                    command.StageId,
                    command.StageLabel,
                    "active",
                    $"{command.StageLabel} failed; retrying once for stability classification.",
                    null,
                    retryLogPath,
                    outputFolder,
                    DateTimeOffset.UtcNow));

                var retryStartedUtc = DateTimeOffset.UtcNow;
                var retryExecution = await _executor.ExecuteAsync(
                    command,
                    workingDirectory,
                    retryLogPath,
                    line => progress?.Invoke(new ValidationProgressEvent(
                        "output",
                        command.StageId,
                        command.StageLabel,
                        "active",
                        $"{command.StageLabel} retry 1: {line}",
                        line,
                        retryLogPath,
                        outputFolder,
                        DateTimeOffset.UtcNow)),
                    ct).ConfigureAwait(false);
                var retryCompletedUtc = DateTimeOffset.UtcNow;
                durationMs += Math.Max(0L, (long)(retryCompletedUtc - retryStartedUtc).TotalMilliseconds);

                var retryAnalysis = retryExecution.ExitCode == 0
                    ? null
                    : AnalyzeFailure(command, retryExecution);
                var retrySummary = retryExecution.ExitCode == 0
                    ? SummarizeSuccess(command.StageLabel, retryExecution)
                    : retryAnalysis?.Summary ?? SummarizeFailure(command.StageLabel, retryExecution);
                var retryClassification = retryExecution.ExitCode == 0
                    ? ClassifyRetrySuccess(command)
                    : "failed";

                retryAudits.Add(new ValidationRetryAudit(
                    command.StageId,
                    command.StageLabel,
                    BuildCommandLine(command),
                    retryLogPath,
                    retryExecution.ExitCode == 0 ? "passed" : "failed",
                    retryClassification,
                    retrySummary,
                    retryExecution.ExitCode,
                    retryStartedUtc,
                    retryCompletedUtc));

                progress?.Invoke(new ValidationProgressEvent(
                    "stage_retry_completed",
                    command.StageId,
                    command.StageLabel,
                    retryExecution.ExitCode == 0 ? "completed" : "failed",
                    retryExecution.ExitCode == 0
                        ? $"{command.StageLabel} passed on retry."
                        : $"{command.StageLabel} failed again on retry.",
                    null,
                    retryLogPath,
                    outputFolder,
                    retryCompletedUtc));

                if (retryExecution.ExitCode == 0)
                {
                    succeeded = true;
                    finalExitCode = 0;
                    stabilityClassification = retryClassification;
                    summary = BuildRetrySuccessSummary(command.StageLabel, failureAnalysis?.ErrorExcerpt ?? firstFailure?.ErrorExcerpt ?? summary, retryClassification);
                }
                else
                {
                    finalExitCode = retryExecution.ExitCode;
                    summary = retrySummary;
                }
            }

            var stageResult = new ValidationStageResult(
                command.StageId,
                command.StageLabel,
                succeeded ? "passed" : "failed",
                summary,
                logPath,
                finalExitCode,
                durationMs,
                stabilityClassification,
                retryCount,
                retryLogPath);
            stages.Add(stageResult);

            progress?.Invoke(new ValidationProgressEvent(
                "stage_completed",
                command.StageId,
                command.StageLabel,
                succeeded ? "completed" : "failed",
                summary,
                null,
                logPath,
                outputFolder,
                DateTimeOffset.UtcNow));

            if (!succeeded && !normalized.ContinueOnFailure)
                break;
        }

        var completedUtc = DateTimeOffset.UtcNow;
        var success = stages.Count > 0 && stages.All(stage => string.Equals(stage.Status, "passed", StringComparison.Ordinal));
        var runStabilityClassification = DetermineRunClassification(success, stages);
        var stabilityStatus = ToStabilityStatus(runStabilityClassification);
        var summaryText = BuildRunSummary(runStabilityClassification, stages.Count, firstFailureText);
        var stabilityArtifactPath = Path.Combine(outputFolder, StabilityFileName);

        var result = new ValidationRunResult(
            runId,
            DisplayLabel(action),
            outputFolder,
            success,
            summaryText,
            firstFailureText,
            firstFailureLogPath,
            startedUtc,
            completedUtc,
            stages,
            runStabilityClassification,
            stabilityStatus,
            firstFailure,
            retryAudits.ToArray(),
            stabilityArtifactPath,
            runMode,
            orchestrationPath,
            string.IsNullOrWhiteSpace(isolatedWorkspacePath) ? null : isolatedWorkspacePath);

        File.WriteAllText(Path.Combine(outputFolder, ResultFileName), JsonSerializer.Serialize(result, JsonOptions()));
        var stability = new ValidationStabilityReport(
            runId,
            DisplayLabel(action),
            runStabilityClassification,
            stabilityStatus,
            outputFolder,
            firstFailure,
            retryAudits.ToArray(),
            stages,
            completedUtc);
        File.WriteAllText(stabilityArtifactPath, JsonSerializer.Serialize(stability, JsonOptions()));
        UpdateTrendArtifacts(result, normalized);
        RefreshReleaseBaselineArtifacts(RepoRoot, normalized, result);
        PruneOldRuns(normalized.KeepLastRuns);
        WriteLatestHandoffArtifacts(result, normalized);

        progress?.Invoke(new ValidationProgressEvent(
            "run_completed",
            string.Empty,
            DisplayLabel(action),
            success ? "completed" : "failed",
            summaryText,
            null,
            firstFailureLogPath,
            outputFolder,
            completedUtc));

        return result;
    }

    private void EnsureRepoRoot()
    {
        if (!Directory.Exists(RepoRoot) || !File.Exists(Path.Combine(RepoRoot, "Shoots.sln")))
            throw new DirectoryNotFoundException($"Could not resolve Shoots repo root from '{RepoRoot}'.");
    }

    private void PruneOldRuns(int keepLastRuns)
    {
        if (!Directory.Exists(ValidationRunsRoot))
            return;

        var directories = Directory
            .GetDirectories(ValidationRunsRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        foreach (var directory in directories.Skip(Math.Max(1, keepLastRuns)))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Keep pruning non-blocking; stale artifacts should not fail the validation run.
            }
        }
    }

    private void UpdateTrendArtifacts(ValidationRunResult result, ValidationSettings settings)
    {
        var historyPath = HistoryLedgerPathForRepo(RepoRoot);
        var artifactsRoot = Path.GetDirectoryName(historyPath)!;
        Directory.CreateDirectory(artifactsRoot);

        var entries = NormalizeHistoryEntries(LoadHistoryLedger(RepoRoot).Entries.Append(BuildHistoryEntry(result)))
            .TakeLast(settings.HistoryRetentionCount)
            .ToArray();
        var ledger = new ValidationHistoryLedger(settings.HistoryRetentionCount, entries);
        File.WriteAllText(historyPath, JsonSerializer.Serialize(ledger, JsonOptions()));
        WriteDerivedTrendArtifacts(RepoRoot, settings, entries);
    }

    private static void WriteDerivedTrendArtifacts(string repoRoot, ValidationSettings settings, IReadOnlyList<ValidationHistoryEntry> entries)
    {
        var trend = BuildTrendSummary(entries, settings.CountRetryPassesAsStableInSummaries);
        File.WriteAllText(TrendSummaryPathForRepo(repoRoot), JsonSerializer.Serialize(trend, JsonOptions()));

        var regression = BuildRegressionSummary(
            entries,
            settings,
            HistoryLedgerPathForRepo(repoRoot),
            TrendSummaryPathForRepo(repoRoot));
        File.WriteAllText(RegressionSummaryPathForRepo(repoRoot), JsonSerializer.Serialize(regression, JsonOptions()));
    }

    private void WriteLatestHandoffArtifacts(ValidationRunResult result, ValidationSettings settings)
        => RefreshHandoffArtifacts(RepoRoot, settings);

    private static IReadOnlyList<ValidationRunResult> LoadRetainedRunResults(string repoRoot, int maxCount)
    {
        var runsRoot = Path.Combine(ValidationArtifactsRootForRepo(repoRoot), "runs");
        if (maxCount <= 0 || !Directory.Exists(runsRoot))
            return Array.Empty<ValidationRunResult>();

        return Directory.GetDirectories(runsRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(directory => TryLoadArtifact<ValidationRunResult?>(Path.Combine(directory, ResultFileName), null))
            .Where(result => result is not null)
            .Cast<ValidationRunResult>()
            .Take(maxCount)
            .OrderBy(result => result.CompletedUtc)
            .ThenBy(result => result.RunId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ValidationHandoffBundle CreateHandoffBundle(
        ValidationRunResult result,
        string repoRoot,
        ValidationHandoffBundle? previousBundle)
    {
        var outputFolder = result.OutputFolder;
        var resultArtifactPath = Path.Combine(outputFolder, ResultFileName);
        var stabilityArtifactPath = ResolveStabilityArtifactPath(result);
        var orchestrationArtifactPath = !string.IsNullOrWhiteSpace(result.OrchestrationArtifactPath)
            ? result.OrchestrationArtifactPath!
            : OrchestrationPathForRun(outputFolder);
        var trendArtifactPath = SnapshotArtifactForRun(TrendSummaryPathForRepo(repoRoot), outputFolder, TrendSummaryFileName);
        var regressionArtifactPath = SnapshotArtifactForRun(RegressionSummaryPathForRepo(repoRoot), outputFolder, RegressionSummaryFileName);
        var baselineComparisonArtifactPath = SnapshotArtifactForRun(BaselineComparisonPathForRepo(repoRoot), outputFolder, BaselineComparisonFileName);
        var activeBaselineArtifactPath = SnapshotArtifactForRun(ActiveBaselinePathForRepo(repoRoot), outputFolder, ActiveBaselineFileName);
        var readiness = TryLoadArtifact(
            baselineComparisonArtifactPath,
            new ValidationBaselineComparison(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                result.RunId,
                resultArtifactPath,
                stabilityArtifactPath,
                "no_baseline",
                new[] { "No baseline comparison recorded for this validation run." },
                Array.Empty<string>(),
                Array.Empty<ValidationBaselineStageChange>(),
                "not_ready",
                new[] { "No release readiness assessment recorded." },
                result.CompletedUtc));
        var orchestration = TryLoadArtifact(
            orchestrationArtifactPath,
            new ValidationOrchestrationReport(
                result.RunId,
                result.ActionLabel,
                result.RunMode,
                ResolveRepoRoot(repoRoot),
                outputFolder,
                OrchestrationPolicyNotePathForRepo(repoRoot),
                result.IsolatedWorkspacePath ?? string.Empty,
                Array.Empty<ValidationStageOrchestrationEntry>(),
                Array.Empty<ValidationOrchestrationDecision>(),
                result.CompletedUtc));
        var blockedStageNotes = orchestration.Decisions
            .Where(decision =>
                string.Equals(decision.DecisionType, "workspace_conflict", StringComparison.Ordinal) ||
                string.Equals(decision.DecisionType, "serialization", StringComparison.Ordinal))
            .Select(decision => decision.Summary)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var retriedStages = result.Stages
            .Where(stage => stage.RetryCount > 0)
            .Select(stage => stage.StageLabel)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
        var bundlePath = HandoffBundlePathForRun(outputFolder);
        var summaryPath = HandoffSummaryPathForRun(outputFolder);
        var bundle = new ValidationHandoffBundle(
            result.RunId,
            result.ActionLabel,
            outputFolder,
            result.StartedUtc,
            result.CompletedUtc,
            result.Success ? "passed" : "failed",
            result.Summary,
            NormalizeRunClassification(result),
            string.IsNullOrWhiteSpace(result.StabilityStatus)
                ? ToStabilityStatus(NormalizeRunClassification(result))
                : result.StabilityStatus,
            string.IsNullOrWhiteSpace(readiness.ReadinessClassification) ? "not_ready" : readiness.ReadinessClassification,
            readiness.ReadinessReasons.Count == 0
                ? "No release readiness assessment recorded."
                : string.Join(" ", readiness.ReadinessReasons),
            result.FirstFailure is null
                ? null
                : new ValidationHandoffFirstFailure(
                    result.FirstFailure.StageLabel,
                    result.FirstFailure.ErrorExcerpt,
                    result.FirstFailure.LogPath,
                    result.FirstFailure.ProjectOrFile,
                    result.FirstFailure.FailingTestName),
            new ValidationHandoffRetryUsage(
                result.Stages.Sum(stage => Math.Max(0, stage.RetryCount)),
                retriedStages),
            blockedStageNotes,
            BuildHandoffArtifactReferences(
                resultArtifactPath,
                stabilityArtifactPath,
                trendArtifactPath,
                regressionArtifactPath,
                orchestrationArtifactPath,
                OrchestrationPolicyNotePathForRepo(repoRoot),
                baselineComparisonArtifactPath,
                activeBaselineArtifactPath,
                result.FirstFailureLogPath),
            resultArtifactPath,
            stabilityArtifactPath,
            trendArtifactPath,
            regressionArtifactPath,
            orchestrationArtifactPath,
            OrchestrationPolicyNotePathForRepo(repoRoot),
            baselineComparisonArtifactPath,
            activeBaselineArtifactPath,
            bundlePath,
            summaryPath,
            BuildHandoffComparison(previousBundle, result, readiness));
        return bundle;
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildHandoffArtifactReferences(
        string resultArtifactPath,
        string stabilityArtifactPath,
        string trendArtifactPath,
        string regressionArtifactPath,
        string orchestrationArtifactPath,
        string orchestrationNotePath,
        string baselineComparisonArtifactPath,
        string activeBaselineArtifactPath,
        string? firstFailureLogPath)
    {
        var references = new List<ValidationHandoffArtifactReference>
        {
            new("validation_result.json", resultArtifactPath),
            new("validation_stability.json", stabilityArtifactPath),
            new("validation_trends.json", trendArtifactPath),
            new("validation_regression_summary.json", regressionArtifactPath),
            new("validation_orchestration.json", orchestrationArtifactPath),
            new("validation_orchestration_note.md", orchestrationNotePath)
        };

        if (!string.IsNullOrWhiteSpace(baselineComparisonArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_baseline_comparison.json", baselineComparisonArtifactPath));
        if (!string.IsNullOrWhiteSpace(activeBaselineArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_release_baseline.json", activeBaselineArtifactPath));
        if (!string.IsNullOrWhiteSpace(firstFailureLogPath))
            references.Add(new ValidationHandoffArtifactReference("first_failure.log", firstFailureLogPath!));

        return references
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string SnapshotArtifactForRun(string sourcePath, string outputFolder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return string.Empty;

        var targetPath = Path.Combine(outputFolder, fileName);
        if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return targetPath;
    }

    private static ValidationHandoffBundleComparison? BuildHandoffComparison(
        ValidationHandoffBundle? previousBundle,
        ValidationRunResult currentResult,
        ValidationBaselineComparison readiness)
    {
        if (previousBundle is null)
            return null;

        var currentOverallResult = currentResult.Success ? "passed" : "failed";
        var currentStability = NormalizeRunClassification(currentResult);
        var currentReadiness = string.IsNullOrWhiteSpace(readiness.ReadinessClassification)
            ? "not_ready"
            : readiness.ReadinessClassification;
        var currentFailureStage = currentResult.FirstFailure?.StageLabel ?? string.Empty;
        var resultChange = BuildComparisonChange(previousBundle.OverallResult, currentOverallResult);
        var readinessChange = BuildComparisonChange(previousBundle.ReadinessClassification, currentReadiness);
        var stabilityChange = BuildComparisonChange(previousBundle.StabilityClassification, currentStability);
        var failureChange = BuildComparisonChange(previousBundle.FirstFailure?.StageLabel ?? string.Empty, currentFailureStage, emptyLabel: "none");

        return new ValidationHandoffBundleComparison(
            previousBundle.RunId,
            resultChange,
            readinessChange,
            stabilityChange,
            failureChange,
            $"Result {resultChange}; readiness {readinessChange}; stability {stabilityChange}; first-failure stage {failureChange}.");
    }

    private static string BuildComparisonChange(string previousValue, string currentValue, string emptyLabel = "none")
    {
        var normalizedPrevious = string.IsNullOrWhiteSpace(previousValue) ? emptyLabel : previousValue;
        var normalizedCurrent = string.IsNullOrWhiteSpace(currentValue) ? emptyLabel : currentValue;
        return string.Equals(normalizedPrevious, normalizedCurrent, StringComparison.Ordinal)
            ? $"unchanged ({normalizedCurrent})"
            : $"{normalizedPrevious} -> {normalizedCurrent}";
    }

    private static void WriteHandoffBundleArtifacts(ValidationHandoffBundle bundle)
    {
        File.WriteAllText(bundle.BundlePath, JsonSerializer.Serialize(bundle, JsonOptions()));
        File.WriteAllText(bundle.SummaryPath, BuildHandoffSummaryMarkdown(bundle));
    }

    private static string BuildHandoffSummaryMarkdown(ValidationHandoffBundle bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Validation Handoff Summary");
        builder.AppendLine();
        builder.AppendLine($"- Run: `{bundle.RunId}`");
        builder.AppendLine($"- Action: {bundle.ActionLabel}");
        builder.AppendLine($"- Completed: {bundle.CompletedUtc:O}");
        builder.AppendLine($"- Overall result: {bundle.OverallResult}");
        builder.AppendLine($"- Stability: {bundle.StabilityStatus}");
        builder.AppendLine($"- Release readiness: {bundle.ReadinessClassification.Replace('_', ' ')}");
        builder.AppendLine($"- Retry usage: {BuildRetryUsageSummary(bundle.RetryUsage)}");
        if (bundle.FirstFailure is not null)
        {
            builder.AppendLine($"- First failure: {bundle.FirstFailure.StageLabel}: {bundle.FirstFailure.ErrorExcerpt}");
        }

        if (bundle.BlockedStageNotes.Count > 0)
        {
            builder.AppendLine($"- Workspace notes: {string.Join(" ", bundle.BlockedStageNotes)}");
        }

        if (bundle.PreviousBundleComparison is not null)
        {
            builder.AppendLine($"- Previous bundle: {bundle.PreviousBundleComparison.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("## Key Artifacts");
        foreach (var artifact in bundle.ArtifactPaths)
        {
            builder.AppendLine($"- {artifact.Label}: `{artifact.Path}`");
        }

        builder.AppendLine($"- validation_handoff_bundle.json: `{bundle.BundlePath}`");
        builder.AppendLine($"- validation_handoff_summary.md: `{bundle.SummaryPath}`");
        return builder.ToString().TrimEnd() + System.Environment.NewLine;
    }

    private static string BuildRetryUsageSummary(ValidationHandoffRetryUsage retryUsage)
        => retryUsage.RetryCount <= 0
            ? "No retries recorded."
            : $"{retryUsage.RetryCount} retr{(retryUsage.RetryCount == 1 ? "y" : "ies")} across {string.Join(", ", retryUsage.RetriedStages)}.";

    private static ValidationFollowupIntake CreateFollowupIntake(
        ValidationHandoffBundle bundle,
        IReadOnlyList<ValidationFollowupHistoryEntry> previousEntries)
    {
        var followupCategory = ClassifyFollowupCategory(bundle);
        var nextStep = BuildFollowupNextStep(followupCategory);
        var issueFingerprint = BuildFollowupIssueFingerprint(bundle, followupCategory);
        var repeatedEntry = SupportsRepeatedIssueTracking(followupCategory)
            ? previousEntries
                .Where(entry => SupportsRepeatedIssueTracking(entry.FollowupCategory))
                .LastOrDefault(entry => string.Equals(entry.IssueFingerprint, issueFingerprint, StringComparison.Ordinal))
            : null;
        var repeatedSummary = repeatedEntry is null
            ? "No recent repeated follow-up detected."
            : $"Matches recent unresolved follow-up from {repeatedEntry.RunId}: {BuildFollowupCategoryLabel(repeatedEntry.FollowupCategory)}.";

        return new ValidationFollowupIntake(
            bundle.RunId,
            bundle.ActionLabel,
            bundle.OutputFolder,
            bundle.CompletedUtc,
            bundle.OverallResult,
            bundle.ReadinessClassification,
            bundle.StabilityClassification,
            bundle.StabilityStatus,
            bundle.FirstFailure,
            bundle.BlockedStageNotes,
            followupCategory,
            nextStep,
            repeatedEntry is not null,
            repeatedSummary,
            issueFingerprint,
            bundle.BundlePath,
            bundle.SummaryPath,
            FollowupIntakePathForRun(bundle.OutputFolder),
            FollowupPromptPathForRun(bundle.OutputFolder),
            bundle.ArtifactPaths);
    }

    private static string ClassifyFollowupCategory(ValidationHandoffBundle bundle)
    {
        if (string.Equals(bundle.StabilityClassification, "passed_on_retry", StringComparison.Ordinal) ||
            string.Equals(bundle.StabilityClassification, "flaky_suspected", StringComparison.Ordinal))
        {
            return "review_flaky_behavior";
        }

        if (string.Equals(bundle.OverallResult, "passed", StringComparison.Ordinal))
        {
            var hasActiveBaseline = !string.IsNullOrWhiteSpace(bundle.ActiveBaselineArtifactPath) && File.Exists(bundle.ActiveBaselineArtifactPath);
            if (!hasActiveBaseline || !string.Equals(bundle.ReadinessClassification, "ready", StringComparison.Ordinal))
                return "baseline_update_candidate";

            return "no_action_needed";
        }

        var stageLabel = bundle.FirstFailure?.StageLabel ?? string.Empty;
        if (MatchesFollowupStage(stageLabel, "build") || MatchesFollowupStage(bundle.ActionLabel, "build"))
            return "fix_build";
        if (MatchesFollowupStage(stageLabel, "test") || MatchesFollowupStage(bundle.ActionLabel, "test"))
            return "fix_tests";
        if (MatchesFollowupStage(stageLabel, "smoke") || MatchesFollowupStage(bundle.ActionLabel, "smoke"))
            return "investigate_smoke";
        if (MatchesFollowupStage(stageLabel, "integrity") ||
            MatchesFollowupStage(stageLabel, "repository validation") ||
            MatchesFollowupStage(bundle.ActionLabel, "integrity"))
        {
            return "investigate_integrity";
        }

        return bundle.BlockedStageNotes.Any(note =>
                note.Contains("integrity", StringComparison.OrdinalIgnoreCase) ||
                note.Contains("workspace", StringComparison.OrdinalIgnoreCase))
            ? "investigate_integrity"
            : "fix_tests";
    }

    private static bool MatchesFollowupStage(string source, string marker)
        => !string.IsNullOrWhiteSpace(source) &&
           source.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string BuildFollowupNextStep(string followupCategory)
        => followupCategory switch
        {
            "fix_build" => "Fix compile blockers first, then rerun the build stage before broader validation.",
            "fix_tests" => "Isolate the first failing test or test project, fix it, and rerun UI tests deterministically.",
            "investigate_smoke" => "Inspect smoke artifacts and logs before code edits, then rerun smoke validation.",
            "investigate_integrity" => "Inspect integrity and orchestration artifacts first, then rerun the integrity gate.",
            "review_flaky_behavior" => "Inspect retry and stability artifacts before code edits, then rerun the affected stage once in a controlled way.",
            "baseline_update_candidate" => "Review the clean handoff and set or refresh the release baseline if this run is the new trusted state.",
            _ => "No immediate follow-up is required. Use the handoff bundle for review or handoff."
        };

    private static bool SupportsRepeatedIssueTracking(string followupCategory)
        => !string.Equals(followupCategory, "no_action_needed", StringComparison.Ordinal) &&
           !string.Equals(followupCategory, "baseline_update_candidate", StringComparison.Ordinal);

    private static string BuildFollowupIssueFingerprint(ValidationHandoffBundle bundle, string followupCategory)
    {
        if (!SupportsRepeatedIssueTracking(followupCategory))
            return string.Empty;

        var stage = bundle.FirstFailure?.StageLabel ?? string.Empty;
        var excerpt = bundle.FirstFailure?.ErrorExcerpt
            ?? (bundle.BlockedStageNotes.Count == 0 ? string.Empty : string.Join(" ", bundle.BlockedStageNotes));
        return string.Join(
            "|",
            new[]
            {
                NormalizeFollowupToken(followupCategory),
                NormalizeFollowupToken(stage),
                NormalizeFollowupToken(excerpt)
            });
    }

    private static string NormalizeFollowupToken(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().ToLowerInvariant();

    private static string BuildFollowupCategoryLabel(string followupCategory)
        => followupCategory switch
        {
            "fix_build" => "fix build",
            "fix_tests" => "fix tests",
            "investigate_smoke" => "investigate smoke",
            "investigate_integrity" => "investigate integrity",
            "review_flaky_behavior" => "review flaky behavior",
            "baseline_update_candidate" => "baseline update candidate",
            _ => "no action needed"
        };

    private static void WriteFollowupArtifacts(ValidationFollowupIntake intake)
    {
        File.WriteAllText(intake.IntakePath, JsonSerializer.Serialize(intake, JsonOptions()));
        File.WriteAllText(intake.PromptPath, BuildFollowupPromptText(intake));
    }

    private static string BuildFollowupPromptText(ValidationFollowupIntake intake)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Validation follow-up intake");
        builder.AppendLine();
        builder.AppendLine($"Run: {intake.RunId}");
        builder.AppendLine($"Action: {intake.ActionLabel}");
        builder.AppendLine($"Follow-up category: {intake.FollowupCategory}");
        builder.AppendLine($"Recommended next step: {intake.NextStep}");
        builder.AppendLine($"Overall result: {intake.OverallResult}");
        builder.AppendLine($"Stability: {intake.StabilityStatus}");
        builder.AppendLine($"Release readiness: {intake.ReadinessClassification.Replace('_', ' ')}");
        builder.AppendLine($"Repeated issue: {intake.RepeatedIssueSummary}");
        if (intake.FirstFailure is not null)
        {
            builder.AppendLine($"First failure: {intake.FirstFailure.StageLabel}: {intake.FirstFailure.ErrorExcerpt}");
        }

        if (intake.BlockedStageNotes.Count > 0)
        {
            builder.AppendLine("Blocked or conflicting stage notes:");
            foreach (var note in intake.BlockedStageNotes)
            {
                builder.AppendLine($"- {note}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Key artifacts:");
        builder.AppendLine($"- validation_followup_intake.json: {intake.IntakePath}");
        builder.AppendLine($"- validation_followup_prompt.txt: {intake.PromptPath}");
        builder.AppendLine($"- validation_handoff_bundle.json: {intake.HandoffBundlePath}");
        builder.AppendLine($"- validation_handoff_summary.md: {intake.HandoffSummaryPath}");
        foreach (var artifact in intake.ArtifactPaths)
        {
            builder.AppendLine($"- {artifact.Label}: {artifact.Path}");
        }

        return builder.ToString().TrimEnd() + System.Environment.NewLine;
    }

    private static void RefreshFollowupPlanArtifacts(string repoRoot, ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var followupHistory = LoadFollowupHistory(repoRoot);
        if (followupHistory.Entries.Count == 0)
            return;

        var latestEntry = followupHistory.Entries
            .OrderBy(entry => entry.CompletedUtc)
            .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
            .Last();
        var semanticIndex = File.Exists(SemanticReuseService.IndexPathForRepo(repoRoot))
            ? SemanticReuseService.LoadIndexLedger(repoRoot)
            : new SemanticReuseIndexLedger(0, DateTimeOffset.MinValue, Array.Empty<SemanticReuseIndexedCase>());
        var playbookCatalog = File.Exists(SemanticReuseService.PlaybookPathForRepo(repoRoot))
            ? SemanticReuseService.LoadPlaybookCatalog(repoRoot)
            : new SemanticReusePlaybookCatalog(
                normalized.MinimumPlaybookEvidenceCount,
                DateTimeOffset.MinValue,
                Array.Empty<SemanticReusePlaybook>());

        foreach (var entry in followupHistory.Entries
                     .OrderBy(item => item.CompletedUtc)
                     .ThenBy(item => item.RunId, StringComparer.Ordinal))
        {
            var intake = LoadFollowupIntakeForRun(entry.OutputFolder);
            if (intake is null)
                continue;

            var handoff = LoadHandoffBundleForRun(entry.OutputFolder);
            var similarCaseSuggestions = BuildFollowupSimilarCaseSuggestions(intake, semanticIndex, normalized);
            var playbookSuggestions = BuildFollowupPlaybookSuggestions(intake, playbookCatalog, normalized);
            var targetScope = BuildFollowupTargetScopeAssessment(repoRoot, intake, similarCaseSuggestions);
            var plan = CreateFollowupPlan(
                intake,
                handoff,
                targetScope,
                similarCaseSuggestions,
                playbookSuggestions,
                string.Equals(entry.RunId, latestEntry.RunId, StringComparison.Ordinal),
                latestEntry.RunId);
            WriteFollowupPlanArtifacts(plan);

            var prepBundle = CreateRepairPrepBundle(
                intake,
                handoff,
                plan,
                targetScope,
                similarCaseSuggestions,
                playbookSuggestions);
            WriteRepairPrepBundle(prepBundle);
            var execution = MergeFollowupExecutionState(plan, LoadFollowupExecutionStateForRun(entry.OutputFolder));
            WriteFollowupExecutionState(execution);
        }

        RefreshFollowupExecutionOutcomeArtifacts(repoRoot);
    }

    private static ValidationFollowupPlan CreateFollowupPlan(
        ValidationFollowupIntake intake,
        ValidationHandoffBundle? handoff,
        FollowupTargetScopeAssessment targetScope,
        IReadOnlyList<ValidationRepairPrepSuggestion> similarCaseSuggestions,
        IReadOnlyList<ValidationRepairPrepSuggestion> playbookSuggestions,
        bool isLatestForRepo,
        string latestRunId)
    {
        var requiredEvidencePaths = BuildFollowupRequiredEvidencePaths(intake, handoff);
        var rerunRecommendation = BuildFollowupRerunRecommendation(intake.FollowupCategory);
        var escalationHint = BuildFollowupEscalationHint(intake);
        var steps = BuildFollowupPlanSteps(
            intake,
            targetScope,
            requiredEvidencePaths,
            rerunRecommendation,
            similarCaseSuggestions,
            playbookSuggestions);
        var relatedArtifactPaths = NormalizePaths(
            requiredEvidencePaths
                .Append(intake.HandoffBundlePath)
                .Append(intake.HandoffSummaryPath)
                .Append(intake.IntakePath)
                .Append(intake.PromptPath));
        var freshnessStatus = isLatestForRepo ? "latest" : "superseded";
        var freshnessSummary = isLatestForRepo
            ? "Current plan for the latest validation run."
            : $"Superseded by newer validation run {latestRunId}.";
        return new ValidationFollowupPlan(
            intake.RunId,
            intake.ActionLabel,
            intake.OutputFolder,
            DateTimeOffset.UtcNow,
            intake.IntakePath,
            intake.FollowupCategory,
            steps,
            targetScope.Scopes,
            targetScope.ScopeSummary,
            targetScope.ScopeConfidence,
            requiredEvidencePaths,
            rerunRecommendation,
            relatedArtifactPaths,
            escalationHint,
            isLatestForRepo,
            freshnessStatus,
            freshnessSummary,
            FollowupPlanPathForRun(intake.OutputFolder));
    }

    private static ValidationRepairPrepBundle CreateRepairPrepBundle(
        ValidationFollowupIntake intake,
        ValidationHandoffBundle? handoff,
        ValidationFollowupPlan plan,
        FollowupTargetScopeAssessment targetScope,
        IReadOnlyList<ValidationRepairPrepSuggestion> similarCaseSuggestions,
        IReadOnlyList<ValidationRepairPrepSuggestion> playbookSuggestions)
    {
        var keyArtifacts = BuildRepairPrepArtifactReferences(intake, handoff, plan);
        return new ValidationRepairPrepBundle(
            intake.RunId,
            intake.OutputFolder,
            intake.FollowupCategory,
            intake.IntakePath,
            plan.PlanPath,
            intake.HandoffBundlePath,
            intake.FirstFailure?.StageLabel ?? string.Empty,
            intake.FirstFailure?.ErrorExcerpt ?? string.Empty,
            targetScope.Scopes,
            targetScope.ScopeSummary,
            targetScope.ScopeConfidence,
            plan.EscalationHint,
            keyArtifacts,
            similarCaseSuggestions,
            playbookSuggestions,
            RepairPrepBundlePathForRun(intake.OutputFolder),
            DateTimeOffset.UtcNow);
    }

    private static void WriteFollowupPlanArtifacts(ValidationFollowupPlan plan)
        => File.WriteAllText(plan.PlanPath, JsonSerializer.Serialize(plan, JsonOptions()));

    private static void WriteRepairPrepBundle(ValidationRepairPrepBundle bundle)
        => File.WriteAllText(bundle.BundlePath, JsonSerializer.Serialize(bundle, JsonOptions()));

    private static ValidationFollowupExecutionState MergeFollowupExecutionState(
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionState? existing)
    {
        var existingSteps = (existing?.Steps ?? Array.Empty<ValidationFollowupPlanStepState>())
            .ToDictionary(
                step => BuildFollowupStepKey(step.Order, step.StepType),
                step => step,
                StringComparer.Ordinal);
        var steps = plan.Steps
            .Select(step =>
            {
                var key = BuildFollowupStepKey(step.Order, step.StepType);
                if (existingSteps.TryGetValue(key, out var preserved))
                {
                    return preserved;
                }

                return new ValidationFollowupPlanStepState(
                    step.Order,
                    step.StepType,
                    "not_started",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    DateTimeOffset.UtcNow);
            })
            .OrderBy(step => step.Order)
            .ThenBy(step => step.StepType, StringComparer.Ordinal)
            .ToArray();
        return new ValidationFollowupExecutionState(
            plan.RunId,
            plan.FollowupCategory,
            plan.SourceIntakePath,
            plan.PlanPath,
            plan.OutputFolder,
            DateTimeOffset.UtcNow,
            steps,
            existing?.LatestRerun);
    }

    private static void WriteFollowupExecutionState(ValidationFollowupExecutionState state)
        => File.WriteAllText(FollowupExecutionPathForRun(state.SourceOutputFolder), JsonSerializer.Serialize(state, JsonOptions()));

    private static void WriteFollowupExecutionOutcome(ValidationFollowupExecutionOutcome outcome)
        => File.WriteAllText(outcome.OutcomePath, JsonSerializer.Serialize(outcome, JsonOptions()));

    private static void WriteFollowupEscalation(ValidationFollowupEscalation escalation)
        => File.WriteAllText(escalation.EscalationPath, JsonSerializer.Serialize(escalation, JsonOptions()));

    private static void WriteFollowupResolutionReview(ValidationFollowupResolutionReview review)
        => File.WriteAllText(review.ReviewPath, JsonSerializer.Serialize(review, JsonOptions()));

    private static void WriteResolutionHandoff(ValidationResolutionHandoff handoff)
        => File.WriteAllText(handoff.HandoffPath, JsonSerializer.Serialize(handoff, JsonOptions()));

    private static void WriteResolutionPromotionReview(ValidationResolutionPromotionReview review)
        => File.WriteAllText(review.PromotionReviewPath, JsonSerializer.Serialize(review, JsonOptions()));

    private static void WriteReleaseDecisionSummary(ValidationReleaseDecisionSummary summary)
        => File.WriteAllText(summary.DecisionSummaryPath, JsonSerializer.Serialize(summary, JsonOptions()));

    private static void RefreshFollowupExecutionOutcomeArtifacts(string repoRoot)
    {
        var history = LoadFollowupHistory(repoRoot);
        if (history.Entries.Count == 0)
            return;

        var latestRun = LoadLatestRunResult(repoRoot);
        var outcomes = new List<ValidationFollowupExecutionOutcome>();
        foreach (var entry in history.Entries
                     .OrderBy(item => item.CompletedUtc)
                     .ThenBy(item => item.RunId, StringComparer.Ordinal))
        {
            var plan = LoadFollowupPlanForRun(entry.OutputFolder);
            var intake = LoadFollowupIntakeForRun(entry.OutputFolder);
            if (plan is null || intake is null)
                continue;

            var execution = LoadFollowupExecutionStateForRun(entry.OutputFolder);
            var sourceResult = LoadRunResultForOutputFolder(entry.OutputFolder);
            var outcome = BuildFollowupExecutionOutcome(repoRoot, plan, intake, execution, sourceResult, latestRun);
            outcomes.Add(outcome);
            WriteFollowupExecutionOutcome(outcome);
        }

        foreach (var outcome in outcomes.OrderBy(item => item.GeneratedUtc).ThenBy(item => item.SourceValidationRunId, StringComparer.Ordinal))
        {
            var plan = LoadFollowupPlanForRun(outcome.SourceOutputFolder);
            var intake = LoadFollowupIntakeForRun(outcome.SourceOutputFolder);
            if (plan is null || intake is null)
                continue;

            var escalation = BuildFollowupEscalation(repoRoot, plan, intake, outcome, latestRun, outcomes);
            WriteFollowupEscalation(escalation);
        }

        RefreshFollowupResolutionArtifacts(repoRoot);
    }

    private static ValidationFollowupExecutionOutcome BuildFollowupExecutionOutcome(
        string repoRoot,
        ValidationFollowupPlan plan,
        ValidationFollowupIntake intake,
        ValidationFollowupExecutionState? execution,
        ValidationRunResult? sourceResult,
        ValidationRunResult? latestRun)
    {
        var issueKey = BuildFollowupIssueKey(intake);
        var rerun = execution?.LatestRerun;
        var sourceStep = ResolveOutcomeSourceStep(plan, rerun);
        var rerunResult = !string.IsNullOrWhiteSpace(rerun?.RerunValidationOutputFolder)
            ? LoadRunResultForOutputFolder(rerun.RerunValidationOutputFolder)
            : null;
        var sourceStage = SelectSourceComparisonStage(sourceResult, rerunResult);
        var rerunStage = SelectPrimaryComparisonStage(rerunResult);
        var comparisonScope = DetermineGuidedComparisonScope(sourceStep, rerun);
        var outcomeClassification = DetermineGuidedOutcomeClassification(sourceStep, rerun, sourceResult, rerunResult);
        var (recommendedNextState, recommendedNextAction) = DetermineGuidedNextState(plan, intake, outcomeClassification, rerun);
        var executedStepKeys = (execution?.Steps ?? Array.Empty<ValidationFollowupPlanStepState>())
            .Where(step => !string.Equals(step.CompletionState, "not_started", StringComparison.Ordinal))
            .Select(step => BuildFollowupStepKey(step.Order, step.StepType))
            .OrderBy(stepKey => stepKey, StringComparer.Ordinal)
            .ToArray();
        var outcomePath = FollowupExecutionOutcomePathForRun(plan.OutputFolder);
        var isLatestForRepo = latestRun is not null && string.Equals(latestRun.RunId, plan.RunId, StringComparison.Ordinal);
        var freshnessStatus = isLatestForRepo ? "latest" : "superseded";
        var freshnessSummary = isLatestForRepo
            ? "Latest guided outcome for the current validation run."
            : latestRun is null
                ? "Guided outcome is detached from the current validation run."
                : $"Superseded by validation run {latestRun.RunId}.";

        return new ValidationFollowupExecutionOutcome(
            plan.RunId,
            plan.FollowupCategory,
            plan.SourceIntakePath,
            plan.PlanPath,
            plan.OutputFolder,
            issueKey,
            sourceStep is null ? string.Empty : BuildFollowupStepKey(sourceStep.Order, sourceStep.StepType),
            sourceStep?.Order ?? 0,
            sourceStep?.StepType ?? string.Empty,
            sourceStep?.Title ?? string.Empty,
            sourceStage?.StageLabel ?? string.Empty,
            sourceStage?.Status ?? string.Empty,
            rerun?.RerunValidationRunId ?? string.Empty,
            rerun?.RerunValidationOutputFolder ?? string.Empty,
            rerunStage?.StageLabel ?? string.Empty,
            rerunStage?.Status ?? string.Empty,
            rerunResult?.Summary ?? rerun?.OutcomeSummary ?? string.Empty,
            comparisonScope,
            BuildGuidedComparisonSummary(sourceStep, sourceStage, rerunStage, rerun),
            outcomeClassification,
            BuildGuidedOutcomeSummary(outcomeClassification, sourceStep, sourceStage, rerunStage, rerunResult, rerun),
            recommendedNextState,
            recommendedNextAction,
            rerun is not null && rerunResult is not null,
            isLatestForRepo,
            freshnessStatus,
            freshnessSummary,
            executedStepKeys,
            BuildFollowupOutcomeArtifactReferences(plan, intake, sourceResult, rerun, rerunResult),
            outcomePath,
            DateTimeOffset.UtcNow);
    }

    private static ValidationFollowupEscalation BuildFollowupEscalation(
        string repoRoot,
        ValidationFollowupPlan plan,
        ValidationFollowupIntake intake,
        ValidationFollowupExecutionOutcome outcome,
        ValidationRunResult? latestRun,
        IReadOnlyList<ValidationFollowupExecutionOutcome> outcomes)
    {
        var repeatedEvidence = outcomes
            .Where(item => string.Equals(item.IssueKey, outcome.IssueKey, StringComparison.Ordinal) &&
                           item.HasRecordedRerun &&
                           (string.Equals(item.OutcomeClassification, "unchanged", StringComparison.Ordinal) ||
                            string.Equals(item.OutcomeClassification, "regressed", StringComparison.Ordinal) ||
                            string.Equals(item.OutcomeClassification, "inconclusive", StringComparison.Ordinal)))
            .OrderBy(item => item.GeneratedUtc)
            .ThenBy(item => item.SourceValidationRunId, StringComparer.Ordinal)
            .Select(item => new ValidationFollowupEscalationEvidence(
                item.SourceValidationRunId,
                item.RerunValidationRunId,
                item.OutcomeClassification,
                item.OutcomeSummary,
                item.OutcomePath,
                item.GeneratedUtc))
            .ToArray();
        var repeatedUnresolvedCount = repeatedEvidence.Length;
        var escalationClassification = DetermineEscalationClassification(intake, outcome, repeatedUnresolvedCount);
        var (suggestedNextState, suggestedNextAction) = DetermineEscalationNextState(plan, outcome, escalationClassification);
        var isLatestForRepo = latestRun is not null && string.Equals(latestRun.RunId, plan.RunId, StringComparison.Ordinal);
        var freshnessStatus = isLatestForRepo ? "latest" : "superseded";
        var freshnessSummary = isLatestForRepo
            ? "Latest escalation guidance for the current validation run."
            : latestRun is null
                ? "Escalation guidance is detached from the current validation run."
                : $"Superseded by validation run {latestRun.RunId}.";
        var escalationPath = FollowupEscalationPathForRun(plan.OutputFolder);

        return new ValidationFollowupEscalation(
            plan.RunId,
            plan.FollowupCategory,
            plan.OutputFolder,
            outcome.IssueKey,
            outcome.OutcomeClassification,
            outcome.OutcomeSummary,
            outcome.RecommendedNextState,
            outcome.RecommendedNextAction,
            repeatedUnresolvedCount,
            repeatedEvidence,
            escalationClassification,
            BuildEscalationSummary(escalationClassification, repeatedUnresolvedCount),
            suggestedNextState,
            suggestedNextAction,
            isLatestForRepo,
            freshnessStatus,
            freshnessSummary,
            BuildFollowupEscalationArtifactReferences(plan, intake, outcome, repeatedEvidence),
            escalationPath,
            DateTimeOffset.UtcNow);
    }

    private static void RefreshFollowupResolutionArtifacts(string repoRoot)
    {
        var history = LoadFollowupHistory(repoRoot);
        if (history.Entries.Count == 0)
            return;

        var contexts = history.Entries
            .OrderBy(entry => entry.CompletedUtc)
            .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
            .Select(entry => new
            {
                Entry = entry,
                Intake = LoadFollowupIntakeForRun(entry.OutputFolder),
                Plan = LoadFollowupPlanForRun(entry.OutputFolder),
                Outcome = LoadFollowupExecutionOutcomeForRun(entry.OutputFolder),
                Handoff = LoadHandoffBundleForRun(entry.OutputFolder)
            })
            .Where(item => item.Intake is not null && item.Plan is not null && item.Outcome is not null)
            .Select(item => new
            {
                item.Entry,
                Intake = item.Intake!,
                Plan = item.Plan!,
                Outcome = item.Outcome!,
                item.Handoff
            })
            .ToArray();
        if (contexts.Length == 0)
            return;

        var reviews = new List<ValidationFollowupResolutionReview>(contexts.Length);
        for (var index = 0; index < contexts.Length; index++)
        {
            var context = contexts[index];
            var laterFollowup = contexts.Skip(index + 1).FirstOrDefault();
            var laterSameIssue = contexts
                .Skip(index + 1)
                .FirstOrDefault(item => string.Equals(item.Outcome.IssueKey, context.Outcome.IssueKey, StringComparison.Ordinal));
            var review = BuildFollowupResolutionReview(
                context.Handoff,
                context.Intake,
                context.Plan,
                context.Outcome,
                laterFollowup?.Entry.RunId ?? string.Empty,
                laterSameIssue?.Entry.RunId ?? string.Empty);
            reviews.Add(review);
            WriteFollowupResolutionReview(review);
        }

        foreach (var context in contexts)
        {
            var review = reviews.FirstOrDefault(item => string.Equals(item.SourceValidationRunId, context.Plan.RunId, StringComparison.Ordinal));
            if (review is null)
                continue;

            var handoff = BuildResolutionHandoff(
                context.Handoff,
                context.Intake,
                context.Plan,
                context.Outcome,
                review);
            WriteResolutionHandoff(handoff);
        }

        RefreshResolutionDecisionArtifacts(repoRoot);
    }

    private static ValidationFollowupResolutionReview BuildFollowupResolutionReview(
        ValidationHandoffBundle? handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome,
        string laterFollowupRunId,
        string laterSameIssueRunId)
    {
        var reviewId = $"{plan.RunId}:resolution-review";
        var resolutionClassification = DetermineResolutionClassification(outcome);
        var currentResolutionState = DetermineCurrentResolutionState(resolutionClassification, laterFollowupRunId);
        var reopenStatus = DetermineResolutionReopenStatus(resolutionClassification, laterSameIssueRunId);
        var reopenSummary = BuildResolutionReopenSummary(reopenStatus, laterSameIssueRunId);
        var issueClosureStatus = DetermineIssueClosureStatus(resolutionClassification, reopenStatus);
        var reviewPath = FollowupResolutionReviewPathForRun(plan.OutputFolder);
        var originalFailureStage = intake.FirstFailure?.StageLabel ?? string.Empty;
        var originalFailureExcerpt = intake.FirstFailure?.ErrorExcerpt ?? string.Empty;
        var originalFailureSummary = BuildOriginalFailureSummary(intake, handoff);
        var isLatestForRepo = string.IsNullOrWhiteSpace(laterFollowupRunId);
        var freshnessStatus = isLatestForRepo ? "latest" : "superseded";
        var freshnessSummary = isLatestForRepo
            ? "Current resolution review for the latest validation issue context."
            : $"Superseded by later follow-up from validation run {laterFollowupRunId}.";

        return new ValidationFollowupResolutionReview(
            reviewId,
            plan.RunId,
            plan.FollowupCategory,
            intake.IntakePath,
            plan.PlanPath,
            outcome.OutcomePath,
            plan.OutputFolder,
            outcome.IssueKey,
            originalFailureStage,
            originalFailureExcerpt,
            originalFailureSummary,
            outcome.OutcomeClassification,
            outcome.OutcomeSummary,
            resolutionClassification,
            currentResolutionState,
            issueClosureStatus,
            BuildResolutionSummary(currentResolutionState, issueClosureStatus, outcome, reopenSummary),
            reopenStatus,
            reopenSummary,
            isLatestForRepo,
            freshnessStatus,
            freshnessSummary,
            BuildFollowupResolutionEvidenceChain(handoff, intake, plan, outcome),
            reviewPath,
            DateTimeOffset.UtcNow);
    }

    private static ValidationResolutionHandoff BuildResolutionHandoff(
        ValidationHandoffBundle? handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome,
        ValidationFollowupResolutionReview review)
    {
        var handoffId = $"{plan.RunId}:resolution-handoff";
        var baselineComparisonPath = handoff?.BaselineComparisonArtifactPath ?? string.Empty;
        var baselineComparison = string.IsNullOrWhiteSpace(baselineComparisonPath)
            ? null
            : TryLoadArtifact<ValidationBaselineComparison?>(baselineComparisonPath, null);
        var (candidateState, reasons) = DetermineResolutionHandoffCandidate(review, intake, baselineComparison);
        var handoffPath = ResolutionHandoffPathForRun(plan.OutputFolder);

        return new ValidationResolutionHandoff(
            handoffId,
            review.ReviewId,
            plan.RunId,
            plan.FollowupCategory,
            plan.OutputFolder,
            review.ResolutionClassification,
            review.CurrentResolutionState,
            review.IssueClosureStatus,
            outcome.OutcomeClassification,
            candidateState,
            reasons,
            BuildResolutionHandoffSummary(candidateState, reasons),
            review.ReopenStatus,
            review.ReopenSummary,
            review.IsLatestForRepo,
            review.FreshnessStatus,
            review.FreshnessSummary,
            baselineComparisonPath,
            handoff?.BundlePath ?? string.Empty,
            handoff?.SummaryPath ?? string.Empty,
            intake.IntakePath,
            plan.PlanPath,
            outcome.OutcomePath,
            review.ReviewPath,
            BuildResolutionHandoffArtifactReferences(handoff, intake, plan, outcome, review, baselineComparisonPath),
            handoffPath,
            DateTimeOffset.UtcNow);
    }

    private static void RefreshResolutionDecisionArtifacts(string repoRoot)
    {
        var history = LoadFollowupHistory(repoRoot);
        if (history.Entries.Count == 0)
            return;

        var latestRun = LoadLatestRunResult(repoRoot);
        var activeBaseline = LoadActiveReleaseBaseline(repoRoot);
        var baselineComparison = LoadBaselineComparison(repoRoot);
        var contexts = history.Entries
            .OrderBy(entry => entry.CompletedUtc)
            .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
            .Select(entry => new
            {
                Entry = entry,
                Review = LoadFollowupResolutionReviewForRun(entry.OutputFolder),
                Handoff = LoadResolutionHandoffForRun(entry.OutputFolder),
                Intake = LoadFollowupIntakeForRun(entry.OutputFolder),
                Plan = LoadFollowupPlanForRun(entry.OutputFolder),
                Outcome = LoadFollowupExecutionOutcomeForRun(entry.OutputFolder)
            })
            .Where(item => item.Review is not null && item.Handoff is not null && item.Intake is not null && item.Plan is not null && item.Outcome is not null)
            .Select(item => new
            {
                item.Entry,
                Review = item.Review!,
                Handoff = item.Handoff!,
                Intake = item.Intake!,
                Plan = item.Plan!,
                Outcome = item.Outcome!
            })
            .ToArray();
        if (contexts.Length == 0)
            return;

        var promotionReviews = new List<ValidationResolutionPromotionReview>(contexts.Length);
        foreach (var context in contexts)
        {
            var review = BuildResolutionPromotionReview(
                repoRoot,
                latestRun,
                activeBaseline,
                baselineComparison,
                context.Review,
                context.Handoff,
                context.Intake,
                context.Plan,
                context.Outcome);
            promotionReviews.Add(review);
            WriteResolutionPromotionReview(review);
        }

        foreach (var context in contexts)
        {
            var promotionReview = promotionReviews.FirstOrDefault(item => string.Equals(item.SourceValidationRunId, context.Plan.RunId, StringComparison.Ordinal));
            if (promotionReview is null)
                continue;

            var summary = BuildReleaseDecisionSummary(
                repoRoot,
                latestRun,
                activeBaseline,
                baselineComparison,
                context.Review,
                context.Handoff,
                promotionReview);
            WriteReleaseDecisionSummary(summary);
        }
    }

    private static ValidationResolutionPromotionReview BuildResolutionPromotionReview(
        string repoRoot,
        ValidationRunResult? latestRun,
        ValidationReleaseBaseline? activeBaseline,
        ValidationBaselineComparison baselineComparison,
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome)
    {
        var promotionReviewId = $"{plan.RunId}:promotion-review";
        var recommendationState = DeterminePromotionRecommendationState(review, handoff, baselineComparison);
        var promotionPath = ResolutionPromotionReviewPathForRun(plan.OutputFolder);
        return new ValidationResolutionPromotionReview(
            promotionReviewId,
            plan.RunId,
            plan.FollowupCategory,
            intake.IntakePath,
            plan.PlanPath,
            outcome.OutcomePath,
            review.ReviewId,
            review.ReviewPath,
            handoff.HandoffPath,
            plan.OutputFolder,
            review.CurrentResolutionState,
            review.ResolutionClassification,
            handoff.CandidateState,
            latestRun?.RunId ?? string.Empty,
            latestRun is null ? string.Empty : Path.Combine(latestRun.OutputFolder, ResultFileName),
            activeBaseline?.SourceResultArtifactPath ?? ActiveBaselinePathForRepo(repoRoot),
            BaselineComparisonPathForRepo(repoRoot),
            baselineComparison.ReadinessClassification,
            baselineComparison.DriftClassification,
            recommendationState,
            BuildPromotionRecommendationSummary(recommendationState, review, handoff, baselineComparison),
            review.IsLatestForRepo,
            review.FreshnessStatus,
            review.FreshnessSummary,
            BuildResolutionPromotionEvidenceChain(repoRoot, latestRun, activeBaseline, baselineComparison, review, handoff, intake, plan, outcome),
            promotionPath,
            DateTimeOffset.UtcNow);
    }

    private static ValidationReleaseDecisionSummary BuildReleaseDecisionSummary(
        string repoRoot,
        ValidationRunResult? latestRun,
        ValidationReleaseBaseline? activeBaseline,
        ValidationBaselineComparison baselineComparison,
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationResolutionPromotionReview promotionReview)
    {
        var contradictions = BuildReleaseDecisionContradictions(review, handoff, baselineComparison)
            .OrderBy(note => note, StringComparer.Ordinal)
            .ToArray();
        var deferralNotes = BuildReleaseDecisionDeferralNotes(review, handoff, promotionReview, baselineComparison)
            .OrderBy(note => note, StringComparer.Ordinal)
            .ToArray();
        var decisionState = DetermineReleaseDecisionState(review, promotionReview, contradictions, baselineComparison);
        var summaryPath = ReleaseDecisionSummaryPathForRun(review.SourceOutputFolder);
        return new ValidationReleaseDecisionSummary(
            $"{review.SourceValidationRunId}:release-decision",
            review.SourceValidationRunId,
            review.SourceOutputFolder,
            promotionReview.PromotionReviewPath,
            review.ReviewPath,
            handoff.HandoffPath,
            latestRun?.RunId ?? string.Empty,
            latestRun is null ? string.Empty : Path.Combine(latestRun.OutputFolder, ResultFileName),
            activeBaseline?.BaselineId ?? string.Empty,
            ActiveBaselinePathForRepo(repoRoot),
            BaselineComparisonPathForRepo(repoRoot),
            review.CurrentResolutionState,
            handoff.CandidateState,
            promotionReview.PromotionRecommendationState,
            baselineComparison.ReadinessClassification,
            baselineComparison.DriftClassification,
            decisionState,
            BuildReleaseDecisionSummaryText(review, handoff, promotionReview, baselineComparison, decisionState, contradictions, deferralNotes),
            contradictions,
            deferralNotes,
            review.IsLatestForRepo,
            review.FreshnessStatus,
            review.FreshnessSummary,
            BuildReleaseDecisionArtifactReferences(repoRoot, latestRun, activeBaseline, baselineComparison, review, handoff, promotionReview),
            summaryPath,
            DateTimeOffset.UtcNow);
    }

    private static string BuildFollowupIssueKey(ValidationFollowupIntake intake)
    {
        if (!string.IsNullOrWhiteSpace(intake.IssueFingerprint))
            return intake.IssueFingerprint;

        return string.Join(
            "|",
            new[]
            {
                intake.FollowupCategory,
                intake.FirstFailure?.StageLabel ?? string.Empty,
                intake.FirstFailure?.FailingTestName ?? string.Empty,
                intake.FirstFailure?.ErrorExcerpt ?? string.Empty
            }).Trim('|');
    }

    private static ValidationFollowupPlanStep? ResolveOutcomeSourceStep(ValidationFollowupPlan plan, ValidationFollowupRerunLinkage? rerun)
    {
        if (rerun is not null)
        {
            var matched = plan.Steps.FirstOrDefault(step =>
                step.Order == rerun.StepOrder &&
                string.Equals(step.StepType, rerun.StepType, StringComparison.Ordinal));
            if (matched is not null)
                return matched;
        }

        return plan.Steps.FirstOrDefault(step => string.Equals(step.InteractionMode, "rerun_capable", StringComparison.Ordinal))
            ?? plan.Steps.FirstOrDefault();
    }

    private static ValidationStageResult? SelectPrimaryComparisonStage(ValidationRunResult? result)
        => result?.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))
            ?? result?.Stages.LastOrDefault();

    private static ValidationStageResult? SelectSourceComparisonStage(ValidationRunResult? sourceResult, ValidationRunResult? rerunResult)
    {
        if (sourceResult is null)
            return null;

        var rerunStage = SelectPrimaryComparisonStage(rerunResult);
        if (rerunStage is null)
            return SelectPrimaryComparisonStage(sourceResult);

        return sourceResult.Stages.FirstOrDefault(stage =>
                   string.Equals(stage.StageId, rerunStage.StageId, StringComparison.Ordinal) ||
                   string.Equals(stage.StageLabel, rerunStage.StageLabel, StringComparison.Ordinal))
               ?? SelectPrimaryComparisonStage(sourceResult);
    }

    private static string DetermineGuidedComparisonScope(ValidationFollowupPlanStep? step, ValidationFollowupRerunLinkage? rerun)
    {
        if (rerun is null)
            return "not_executed";

        return step?.StepType switch
        {
            "rerun_single_test_or_project" => "narrow_stage_scope",
            "rerun_single_stage" => "full_stage_scope",
            "rerun_build_scope" => "full_stage_scope",
            _ => "guided_step_scope"
        };
    }

    private static string DetermineGuidedOutcomeClassification(
        ValidationFollowupPlanStep? step,
        ValidationFollowupRerunLinkage? rerun,
        ValidationRunResult? sourceResult,
        ValidationRunResult? rerunResult)
    {
        if (rerun is null || rerunResult is null)
            return "inconclusive";

        if (rerunResult.Success)
        {
            return string.Equals(step?.StepType, "rerun_single_test_or_project", StringComparison.Ordinal)
                ? "improved"
                : "resolved";
        }

        return rerun.OutcomeClassification switch
        {
            "regressed" => "regressed",
            "unchanged" => "unchanged",
            "improved" => "improved",
            "passed" => string.Equals(step?.StepType, "rerun_single_test_or_project", StringComparison.Ordinal)
                ? "improved"
                : "resolved",
            _ => DetermineGuidedOutcomeClassificationFromStages(sourceResult, rerunResult)
        };
    }

    private static string DetermineGuidedOutcomeClassificationFromStages(ValidationRunResult? sourceResult, ValidationRunResult? rerunResult)
    {
        var sourceStage = SelectSourceComparisonStage(sourceResult, rerunResult);
        var rerunStage = SelectPrimaryComparisonStage(rerunResult);
        if (sourceStage is null || rerunStage is null)
            return "inconclusive";

        var sourceFailed = string.Equals(sourceStage.Status, "failed", StringComparison.Ordinal);
        var rerunFailed = string.Equals(rerunStage.Status, "failed", StringComparison.Ordinal);
        if (sourceFailed == rerunFailed)
            return "unchanged";

        return rerunFailed ? "regressed" : "improved";
    }

    private static (string RecommendedNextState, string RecommendedNextAction) DetermineGuidedNextState(
        ValidationFollowupPlan plan,
        ValidationFollowupIntake intake,
        string outcomeClassification,
        ValidationFollowupRerunLinkage? rerun)
    {
        var hasReviewStep = plan.Steps.Any(step => string.Equals(step.StepType, "review_playbook_or_similar_case", StringComparison.Ordinal));
        return outcomeClassification switch
        {
            "resolved" => ("no_further_action", "The guided rerun resolved the investigated stage. Keep the evidence trail visible and decide whether a broader confirmation rerun is still useful."),
            "improved" when string.Equals(rerun?.StepType, "rerun_single_test_or_project", StringComparison.Ordinal)
                => ("rerun_full_stage", "The narrow rerun improved the issue. Rerun the full affected stage before changing code again."),
            "improved" => ("rerun_full_stage", "The guided rerun improved the result. Rerun the full affected stage to confirm the change holds."),
            "unchanged" when intake.HasRecentRepeatedIssue => ("escalate_recurring_issue", "The guided rerun stayed unchanged and recent follow-up history shows the same issue. Escalate instead of repeating the same rerun."),
            "unchanged" when string.Equals(plan.FollowupCategory, "fix_build", StringComparison.Ordinal) || string.Equals(plan.FollowupCategory, "fix_tests", StringComparison.Ordinal)
                => ("prepare_repair", "The guided rerun stayed unchanged. Prepare a repair bundle with the current evidence before the next code change."),
            "unchanged" when hasReviewStep => ("review_playbook", "The guided rerun stayed unchanged. Review similar cases or playbooks before the next bounded action."),
            "unchanged" => ("inspect_artifacts_more", "The guided rerun stayed unchanged. Inspect the linked artifacts again before choosing the next step."),
            "regressed" when intake.HasRecentRepeatedIssue => ("escalate_recurring_issue", "The guided rerun regressed and recent follow-up history repeats the same issue. Escalate instead of retrying again."),
            "regressed" => ("inspect_artifacts_more", "The guided rerun regressed. Inspect the rerun artifacts before deciding whether to repair or rerun again."),
            _ when hasReviewStep => ("review_playbook", "No conclusive guided rerun result is recorded yet. Review similar cases or playbooks before the next bounded action."),
            _ => ("inspect_artifacts_more", "No conclusive guided rerun result is recorded yet. Inspect the linked artifacts before choosing the next step.")
        };
    }

    private static string DetermineEscalationClassification(
        ValidationFollowupIntake intake,
        ValidationFollowupExecutionOutcome outcome,
        int repeatedUnresolvedCount)
    {
        if (!outcome.HasRecordedRerun)
            return "no_escalation";

        if (string.Equals(outcome.OutcomeClassification, "resolved", StringComparison.Ordinal))
            return "no_escalation";

        if (repeatedUnresolvedCount >= 2 || (intake.HasRecentRepeatedIssue && repeatedUnresolvedCount >= 1))
            return "escalate_recurring_issue";

        return string.Equals(outcome.OutcomeClassification, "unchanged", StringComparison.Ordinal) ||
               string.Equals(outcome.OutcomeClassification, "regressed", StringComparison.Ordinal) ||
               string.Equals(outcome.OutcomeClassification, "inconclusive", StringComparison.Ordinal)
            ? "watch_recurring_issue"
            : "no_escalation";
    }

    private static (string SuggestedNextState, string SuggestedNextAction) DetermineEscalationNextState(
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome,
        string escalationClassification)
    {
        return escalationClassification switch
        {
            "escalate_recurring_issue" => ("escalate_recurring_issue", "Recurring unresolved guided outcomes match the same issue key. Escalate the issue instead of repeating the same rerun path."),
            "watch_recurring_issue" when plan.Steps.Any(step => string.Equals(step.StepType, "review_playbook_or_similar_case", StringComparison.Ordinal))
                => ("review_playbook", "The issue is still unresolved. Review similar cases or playbooks before the next bounded action."),
            "watch_recurring_issue" => (outcome.RecommendedNextState, outcome.RecommendedNextAction),
            _ => ("no_further_action", "No recurring escalation signal is recorded for this guided outcome.")
        };
    }

    private static string BuildGuidedComparisonSummary(
        ValidationFollowupPlanStep? step,
        ValidationStageResult? sourceStage,
        ValidationStageResult? rerunStage,
        ValidationFollowupRerunLinkage? rerun)
    {
        if (rerun is null)
            return "No guided rerun has been recorded for this follow-up plan.";

        var sourceLabel = sourceStage?.StageLabel ?? "source stage";
        var rerunLabel = rerunStage?.StageLabel ?? "rerun stage";
        var stepLabel = step?.Title ?? rerun.StepType;
        return $"Compared {sourceLabel} against {rerunLabel} for step {stepLabel}.";
    }

    private static string BuildGuidedOutcomeSummary(
        string outcomeClassification,
        ValidationFollowupPlanStep? step,
        ValidationStageResult? sourceStage,
        ValidationStageResult? rerunStage,
        ValidationRunResult? rerunResult,
        ValidationFollowupRerunLinkage? rerun)
    {
        if (rerun is null || rerunResult is null)
            return "No guided rerun result is recorded yet.";

        var stepLabel = step?.Title ?? rerun.StepType;
        var sourceLabel = sourceStage?.StageLabel ?? "source stage";
        var rerunLabel = rerunStage?.StageLabel ?? "rerun stage";
        return outcomeClassification switch
        {
            "resolved" => $"Guided rerun {rerun.RerunValidationRunId} resolved {rerunLabel} for step {stepLabel}.",
            "improved" => $"Guided rerun {rerun.RerunValidationRunId} improved {rerunLabel} compared with {sourceLabel}.",
            "unchanged" => $"Guided rerun {rerun.RerunValidationRunId} stayed unchanged for {rerunLabel} compared with {sourceLabel}.",
            "regressed" => $"Guided rerun {rerun.RerunValidationRunId} regressed {rerunLabel} compared with {sourceLabel}.",
            _ => $"Guided rerun {rerun.RerunValidationRunId} did not produce enough evidence to classify {rerunLabel}."
        };
    }

    private static string BuildEscalationSummary(string escalationClassification, int repeatedUnresolvedCount)
        => escalationClassification switch
        {
            "escalate_recurring_issue" => $"Recurring unresolved guided outcomes recorded: {repeatedUnresolvedCount}. Escalation is recommended.",
            "watch_recurring_issue" => $"Unresolved guided outcome recorded. Repeated unresolved outcomes so far: {repeatedUnresolvedCount}.",
            _ => "No recurring escalation signal is recorded for this guided outcome."
        };

    private static string DetermineResolutionClassification(ValidationFollowupExecutionOutcome outcome)
        => outcome.OutcomeClassification switch
        {
            "resolved" => "closed_by_guided_rerun",
            "improved" => "improved_but_open",
            "regressed" => "regressed",
            _ => "unresolved"
        };

    private static string DetermineCurrentResolutionState(string resolutionClassification, string laterFollowupRunId)
        => string.IsNullOrWhiteSpace(laterFollowupRunId)
            ? resolutionClassification
            : "superseded";

    private static string DetermineResolutionReopenStatus(string resolutionClassification, string laterSameIssueRunId)
    {
        if (string.IsNullOrWhiteSpace(laterSameIssueRunId))
            return "not_reopened";

        return string.Equals(resolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal)
            ? "reopened_by_later_validation"
            : "superseded_by_later_validation";
    }

    private static string BuildResolutionReopenSummary(string reopenStatus, string laterSameIssueRunId)
    {
        if (string.IsNullOrWhiteSpace(laterSameIssueRunId))
            return "No later validation run has reopened this issue.";

        return reopenStatus switch
        {
            "reopened_by_later_validation" => $"Later validation run {laterSameIssueRunId} reintroduced the same issue.",
            "superseded_by_later_validation" => $"Later validation run {laterSameIssueRunId} superseded this issue context before it was closed.",
            _ => "No later validation run has reopened this issue."
        };
    }

    private static string DetermineIssueClosureStatus(string resolutionClassification, string reopenStatus)
    {
        if (string.Equals(reopenStatus, "reopened_by_later_validation", StringComparison.Ordinal))
            return "still_open";

        return resolutionClassification switch
        {
            "closed_by_guided_rerun" => "closed",
            "improved_but_open" => "partially_resolved",
            _ => "still_open"
        };
    }

    private static string BuildOriginalFailureSummary(ValidationFollowupIntake intake, ValidationHandoffBundle? handoff)
    {
        if (intake.FirstFailure is not null)
            return $"{intake.FirstFailure.StageLabel}: {intake.FirstFailure.ErrorExcerpt}";

        if (handoff?.FirstFailure is not null)
            return $"{handoff.FirstFailure.StageLabel}: {handoff.FirstFailure.ErrorExcerpt}";

        return string.IsNullOrWhiteSpace(handoff?.Summary)
            ? "No original failure summary recorded."
            : handoff!.Summary;
    }

    private static string BuildResolutionSummary(
        string currentResolutionState,
        string issueClosureStatus,
        ValidationFollowupExecutionOutcome outcome,
        string reopenSummary)
    {
        var summary = currentResolutionState switch
        {
            "closed_by_guided_rerun" => $"Issue appears closed by guided rerun. {outcome.OutcomeSummary}",
            "improved_but_open" => $"Issue improved but remains open. {outcome.OutcomeSummary}",
            "regressed" => $"Issue regressed during guided follow-up. {outcome.OutcomeSummary}",
            "superseded" => $"This resolution review is superseded by newer validation evidence. {outcome.OutcomeSummary}",
            _ => $"Issue remains open. {outcome.OutcomeSummary}"
        };

        if (!string.IsNullOrWhiteSpace(reopenSummary) &&
            !string.Equals(reopenSummary, "No later validation run has reopened this issue.", StringComparison.Ordinal))
        {
            summary = $"{summary} {reopenSummary}";
        }

        return $"{summary} Current closure view: {issueClosureStatus.Replace('_', ' ')}.".Trim();
    }

    private static (string CandidateState, IReadOnlyList<string> Reasons) DetermineResolutionHandoffCandidate(
        ValidationFollowupResolutionReview review,
        ValidationFollowupIntake intake,
        ValidationBaselineComparison? baselineComparison)
    {
        var reasons = new List<string>();

        if (string.Equals(review.CurrentResolutionState, "superseded", StringComparison.Ordinal))
        {
            reasons.Add("Resolution review is superseded by newer validation evidence.");
            return ("no_handoff", reasons);
        }

        if (string.Equals(review.ReopenStatus, "reopened_by_later_validation", StringComparison.Ordinal))
        {
            reasons.Add("Later validation reintroduced the same issue.");
            return ("no_handoff", reasons);
        }

        if (!string.Equals(review.ResolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal))
        {
            reasons.Add("Guided follow-up has not closed the issue cleanly.");
            return ("no_handoff", reasons);
        }

        if (string.Equals(intake.FollowupCategory, "baseline_update_candidate", StringComparison.Ordinal))
        {
            reasons.Add("Guided follow-up closed a baseline update candidate.");
            if (baselineComparison is not null && !string.IsNullOrWhiteSpace(baselineComparison.ReadinessClassification))
                reasons.Add($"Baseline comparison currently reads {baselineComparison.ReadinessClassification.Replace('_', ' ')}.");

            return ("baseline_review_candidate", reasons);
        }

        reasons.Add("Guided follow-up closed the investigated issue cleanly.");
        if (baselineComparison is not null && !string.IsNullOrWhiteSpace(baselineComparison.ReadinessClassification))
            reasons.Add($"Baseline comparison currently reads {baselineComparison.ReadinessClassification.Replace('_', ' ')}.");
        else
            reasons.Add("Review release readiness against the latest validation evidence before updating a baseline.");

        return ("readiness_review_candidate", reasons);
    }

    private static string BuildResolutionHandoffSummary(string candidateState, IReadOnlyList<string> reasons)
    {
        var prefix = candidateState switch
        {
            "baseline_review_candidate" => "Baseline review candidate.",
            "readiness_review_candidate" => "Readiness review candidate.",
            _ => "No baseline or readiness handoff is recorded."
        };

        return reasons.Count == 0
            ? prefix
            : $"{prefix} {string.Join(" ", reasons)}".Trim();
    }

    private static string DeterminePromotionRecommendationState(
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationBaselineComparison baselineComparison)
    {
        if (string.Equals(review.CurrentResolutionState, "superseded", StringComparison.Ordinal) ||
            string.Equals(review.ReopenStatus, "reopened_by_later_validation", StringComparison.Ordinal))
        {
            return "do_not_promote";
        }

        if (!string.Equals(review.ResolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal))
            return "do_not_promote";

        var contradictions = BuildReleaseDecisionContradictions(review, handoff, baselineComparison);
        if (string.Equals(handoff.CandidateState, "baseline_review_candidate", StringComparison.Ordinal))
        {
            return contradictions.Count == 0 && string.Equals(baselineComparison.DriftClassification, "no_drift", StringComparison.Ordinal)
                ? "recommend_baseline_consideration"
                : "recommend_review_only";
        }

        if (string.Equals(handoff.CandidateState, "readiness_review_candidate", StringComparison.Ordinal))
        {
            return string.Equals(baselineComparison.ReadinessClassification, "not_ready", StringComparison.Ordinal) || contradictions.Count > 0
                ? "recommend_review_only"
                : "recommend_readiness_consideration";
        }

        return "do_not_promote";
    }

    private static string BuildPromotionRecommendationSummary(
        string recommendationState,
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationBaselineComparison baselineComparison)
    {
        return recommendationState switch
        {
            "recommend_baseline_consideration" => $"Baseline consideration is suggested. Resolution is {review.CurrentResolutionState.Replace('_', ' ')} and current drift is {baselineComparison.DriftClassification.Replace('_', ' ')}.",
            "recommend_readiness_consideration" => $"Readiness consideration is suggested. Resolution is {review.CurrentResolutionState.Replace('_', ' ')} and current readiness is {baselineComparison.ReadinessClassification.Replace('_', ' ')}.",
            "recommend_review_only" => $"Review only. Resolution handoff is {handoff.CandidateState.Replace('_', ' ')} but the current baseline/readiness context still needs operator review.",
            _ => $"Do not promote. Resolution state is {review.CurrentResolutionState.Replace('_', ' ')} and handoff is {handoff.CandidateState.Replace('_', ' ')}."
        };
    }

    private static IReadOnlyList<string> BuildReleaseDecisionContradictions(
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationBaselineComparison baselineComparison)
    {
        var contradictions = new List<string>();
        if (string.Equals(review.ResolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal) &&
            string.Equals(baselineComparison.ReadinessClassification, "not_ready", StringComparison.Ordinal))
        {
            contradictions.Add("Issue appears resolved, but current release readiness is still not ready.");
        }

        if (string.Equals(handoff.CandidateState, "baseline_review_candidate", StringComparison.Ordinal) &&
            !string.Equals(baselineComparison.DriftClassification, "no_drift", StringComparison.Ordinal))
        {
            contradictions.Add("Baseline consideration is contradicted by current drift above the baseline.");
        }

        if (string.Equals(review.ReopenStatus, "reopened_by_later_validation", StringComparison.Ordinal))
        {
            contradictions.Add("Apparent closure conflicts with later validation that reopened the same issue.");
        }

        return contradictions;
    }

    private static IReadOnlyList<string> BuildReleaseDecisionDeferralNotes(
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationResolutionPromotionReview promotionReview,
        ValidationBaselineComparison baselineComparison)
    {
        var notes = new List<string>();
        if (string.Equals(review.CurrentResolutionState, "superseded", StringComparison.Ordinal))
            notes.Add("A newer follow-up issue context already supersedes this resolution review.");

        if (!string.Equals(review.ResolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal))
            notes.Add("Guided follow-up has not produced a clean closure for this issue.");

        if (string.Equals(baselineComparison.ReadinessClassification, "caution", StringComparison.Ordinal))
            notes.Add("Current release readiness is still caution, so release posture needs explicit review.");

        if (string.Equals(baselineComparison.ReadinessClassification, "not_ready", StringComparison.Ordinal))
            notes.Add("Current release readiness still needs more validation evidence.");

        if (string.Equals(promotionReview.PromotionRecommendationState, "recommend_review_only", StringComparison.Ordinal))
            notes.Add("Promotion is limited to review only until the current baseline/readiness context settles.");

        if (string.Equals(handoff.CandidateState, "no_handoff", StringComparison.Ordinal))
            notes.Add("No baseline or readiness handoff candidate is available from this resolution.");

        return notes;
    }

    private static string DetermineReleaseDecisionState(
        ValidationFollowupResolutionReview review,
        ValidationResolutionPromotionReview promotionReview,
        IReadOnlyList<string> contradictions,
        ValidationBaselineComparison baselineComparison)
    {
        if (string.Equals(review.CurrentResolutionState, "superseded", StringComparison.Ordinal) ||
            string.Equals(review.ReopenStatus, "reopened_by_later_validation", StringComparison.Ordinal) ||
            !string.Equals(review.ResolutionClassification, "closed_by_guided_rerun", StringComparison.Ordinal))
        {
            return "resolution_not_stable_enough";
        }

        if (contradictions.Count > 0 ||
            string.Equals(baselineComparison.ReadinessClassification, "not_ready", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.DriftClassification, "stage_regression_drift", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.DriftClassification, "failure_drift", StringComparison.Ordinal))
        {
            return "needs_more_validation_evidence";
        }

        if (string.Equals(promotionReview.PromotionRecommendationState, "recommend_review_only", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.ReadinessClassification, "caution", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.DriftClassification, "retry_drift", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.DriftClassification, "flaky_drift", StringComparison.Ordinal) ||
            string.Equals(baselineComparison.DriftClassification, "no_baseline", StringComparison.Ordinal))
        {
            return "defer_release_decision";
        }

        return "ready_for_operator_review";
    }

    private static string BuildReleaseDecisionSummaryText(
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationResolutionPromotionReview promotionReview,
        ValidationBaselineComparison baselineComparison,
        string decisionState,
        IReadOnlyList<string> contradictions,
        IReadOnlyList<string> deferralNotes)
    {
        var builder = new StringBuilder();
        builder.Append($"Latest readiness: {baselineComparison.ReadinessClassification.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"Current drift: {baselineComparison.DriftClassification.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"Resolution: {review.CurrentResolutionState.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"Handoff: {handoff.CandidateState.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"Promotion recommendation: {promotionReview.PromotionRecommendationState.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"Decision state: {decisionState.Replace('_', ' ')}.");
        if (contradictions.Count > 0)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", contradictions));
        }

        if (deferralNotes.Count > 0)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", deferralNotes));
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildResolutionPromotionEvidenceChain(
        string repoRoot,
        ValidationRunResult? latestRun,
        ValidationReleaseBaseline? activeBaseline,
        ValidationBaselineComparison baselineComparison,
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome)
    {
        var references = new List<ValidationHandoffArtifactReference>();
        references.AddRange(review.EvidenceChain);
        references.Add(new ValidationHandoffArtifactReference("validation_followup_resolution_review.json", review.ReviewPath));
        references.Add(new ValidationHandoffArtifactReference("validation_resolution_handoff.json", handoff.HandoffPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_intake.json", intake.IntakePath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_plan.json", plan.PlanPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_execution_outcome.json", outcome.OutcomePath));

        if (latestRun is not null)
        {
            references.Add(new ValidationHandoffArtifactReference("validation_result.json", Path.Combine(latestRun.OutputFolder, ResultFileName)));
            references.Add(new ValidationHandoffArtifactReference("validation_stability.json", ResolveStabilityArtifactPath(latestRun)));
        }

        if (activeBaseline is not null && !string.IsNullOrWhiteSpace(activeBaseline.SourceResultArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_release_baseline.json", ActiveBaselinePathForRepo(repoRoot)));
        else if (!string.IsNullOrWhiteSpace(activeBaseline?.SourceResultArtifactPath) || File.Exists(ActiveBaselinePathForRepo(repoRoot)))
            references.Add(new ValidationHandoffArtifactReference("validation_release_baseline.json", ActiveBaselinePathForRepo(repoRoot)));

        if (!string.IsNullOrWhiteSpace(baselineComparison.BaselineArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_baseline_comparison.json", BaselineComparisonPathForRepo(repoRoot)));
        else if (File.Exists(BaselineComparisonPathForRepo(repoRoot)))
            references.Add(new ValidationHandoffArtifactReference("validation_baseline_comparison.json", BaselineComparisonPathForRepo(repoRoot)));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildReleaseDecisionArtifactReferences(
        string repoRoot,
        ValidationRunResult? latestRun,
        ValidationReleaseBaseline? activeBaseline,
        ValidationBaselineComparison baselineComparison,
        ValidationFollowupResolutionReview review,
        ValidationResolutionHandoff handoff,
        ValidationResolutionPromotionReview promotionReview)
    {
        var references = new List<ValidationHandoffArtifactReference>();
        references.AddRange(promotionReview.EvidenceChain);
        references.Add(new ValidationHandoffArtifactReference("validation_followup_resolution_review.json", review.ReviewPath));
        references.Add(new ValidationHandoffArtifactReference("validation_resolution_handoff.json", handoff.HandoffPath));
        references.Add(new ValidationHandoffArtifactReference("validation_resolution_promotion_review.json", promotionReview.PromotionReviewPath));

        if (latestRun is not null)
        {
            references.Add(new ValidationHandoffArtifactReference("validation_result.json", Path.Combine(latestRun.OutputFolder, ResultFileName)));
            references.Add(new ValidationHandoffArtifactReference("validation_stability.json", ResolveStabilityArtifactPath(latestRun)));
        }

        if (activeBaseline is not null || File.Exists(ActiveBaselinePathForRepo(repoRoot)))
            references.Add(new ValidationHandoffArtifactReference("validation_release_baseline.json", ActiveBaselinePathForRepo(repoRoot)));

        if (baselineComparison is not null || File.Exists(BaselineComparisonPathForRepo(repoRoot)))
            references.Add(new ValidationHandoffArtifactReference("validation_baseline_comparison.json", BaselineComparisonPathForRepo(repoRoot)));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildFollowupOutcomeArtifactReferences(
        ValidationFollowupPlan plan,
        ValidationFollowupIntake intake,
        ValidationRunResult? sourceResult,
        ValidationFollowupRerunLinkage? rerun,
        ValidationRunResult? rerunResult)
    {
        var references = new List<ValidationHandoffArtifactReference>
        {
            new("Follow-up intake", intake.IntakePath),
            new("Follow-up prompt", intake.PromptPath),
            new("Follow-up plan", plan.PlanPath),
            new("Follow-up execution", FollowupExecutionPathForRun(plan.OutputFolder))
        };

        if (sourceResult is not null)
        {
            references.Add(new("Source validation result", Path.Combine(sourceResult.OutputFolder, ResultFileName)));
            references.Add(new("Source validation stability", ResolveStabilityArtifactPath(sourceResult)));
        }

        if (rerun is not null)
        {
            references.Add(new("Guided rerun result", rerun.ResultArtifactPath));
            references.Add(new("Guided rerun stability", rerun.StabilityArtifactPath));
        }

        if (rerunResult is not null && !string.IsNullOrWhiteSpace(rerunResult.FirstFailureLogPath))
            references.Add(new("Guided rerun log", rerunResult.FirstFailureLogPath!));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildFollowupEscalationArtifactReferences(
        ValidationFollowupPlan plan,
        ValidationFollowupIntake intake,
        ValidationFollowupExecutionOutcome outcome,
        IReadOnlyList<ValidationFollowupEscalationEvidence> repeatedEvidence)
    {
        var references = new List<ValidationHandoffArtifactReference>
        {
            new("Follow-up intake", intake.IntakePath),
            new("Follow-up plan", plan.PlanPath),
            new("Guided outcome", outcome.OutcomePath)
        };
        references.AddRange(outcome.LinkedArtifactPaths);
        references.AddRange(repeatedEvidence.Select((evidence, index) =>
            new ValidationHandoffArtifactReference($"Repeated outcome {index + 1}", evidence.OutcomePath)));
        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildFollowupResolutionEvidenceChain(
        ValidationHandoffBundle? handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome)
    {
        var references = new List<ValidationHandoffArtifactReference>();
        if (handoff is not null)
        {
            references.Add(new ValidationHandoffArtifactReference("validation_handoff_bundle.json", handoff.BundlePath));
            references.Add(new ValidationHandoffArtifactReference("validation_handoff_summary.md", handoff.SummaryPath));
            references.Add(new ValidationHandoffArtifactReference("validation_result.json", handoff.ResultArtifactPath));
            references.Add(new ValidationHandoffArtifactReference("validation_stability.json", handoff.StabilityArtifactPath));
            if (handoff.FirstFailure is not null && !string.IsNullOrWhiteSpace(handoff.FirstFailure.LogPath))
                references.Add(new ValidationHandoffArtifactReference("first_failure.log", handoff.FirstFailure.LogPath));
        }

        references.Add(new ValidationHandoffArtifactReference("validation_followup_intake.json", intake.IntakePath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_prompt.txt", intake.PromptPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_plan.json", plan.PlanPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_execution.json", FollowupExecutionPathForRun(plan.OutputFolder)));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_execution_outcome.json", outcome.OutcomePath));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildResolutionHandoffArtifactReferences(
        ValidationHandoffBundle? handoff,
        ValidationFollowupIntake intake,
        ValidationFollowupPlan plan,
        ValidationFollowupExecutionOutcome outcome,
        ValidationFollowupResolutionReview review,
        string baselineComparisonPath)
    {
        var references = new List<ValidationHandoffArtifactReference>();
        references.AddRange(review.EvidenceChain);
        references.Add(new ValidationHandoffArtifactReference("validation_followup_resolution_review.json", review.ReviewPath));
        if (!string.IsNullOrWhiteSpace(baselineComparisonPath))
            references.Add(new ValidationHandoffArtifactReference("validation_baseline_comparison.json", baselineComparisonPath));
        if (handoff is not null && !string.IsNullOrWhiteSpace(handoff.ActiveBaselineArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_release_baseline.json", handoff.ActiveBaselineArtifactPath));
        if (handoff is not null && !string.IsNullOrWhiteSpace(handoff.OrchestrationArtifactPath))
            references.Add(new ValidationHandoffArtifactReference("validation_orchestration.json", handoff.OrchestrationArtifactPath));
        if (handoff is not null && !string.IsNullOrWhiteSpace(handoff.OrchestrationNotePath))
            references.Add(new ValidationHandoffArtifactReference("validation_orchestration_note.md", handoff.OrchestrationNotePath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_intake.json", intake.IntakePath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_plan.json", plan.PlanPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_execution_outcome.json", outcome.OutcomePath));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveRepoRootFromOutputFolder(string outputFolder)
        => Path.GetFullPath(Path.Combine(outputFolder, "..", "..", "..", ".."));

    public static ValidationFollowupExecutionState RecordFollowupStepInteraction(
        string outputFolder,
        int stepOrder,
        string stepType,
        string completionState,
        string actionKind,
        string detail,
        string evidencePath)
    {
        var plan = LoadFollowupPlanForRun(outputFolder);
        if (plan is null)
        {
            return new ValidationFollowupExecutionState(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                outputFolder,
                DateTimeOffset.UtcNow,
                Array.Empty<ValidationFollowupPlanStepState>(),
                null);
        }

        var merged = MergeFollowupExecutionState(plan, LoadFollowupExecutionStateForRun(outputFolder));
        var updatedSteps = merged.Steps
            .Select(step =>
            {
                if (step.Order != stepOrder || !string.Equals(step.StepType, stepType, StringComparison.Ordinal))
                    return step;

                var nextState = MergeFollowupCompletionState(step.CompletionState, completionState);
                return step with
                {
                    CompletionState = nextState,
                    LastActionKind = string.IsNullOrWhiteSpace(actionKind) ? step.LastActionKind : actionKind,
                    Detail = string.IsNullOrWhiteSpace(detail) ? step.Detail : detail,
                    EvidencePath = string.IsNullOrWhiteSpace(evidencePath) ? step.EvidencePath : evidencePath,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            })
            .OrderBy(step => step.Order)
            .ThenBy(step => step.StepType, StringComparer.Ordinal)
            .ToArray();
        var updated = merged with
        {
            RecordedUtc = DateTimeOffset.UtcNow,
            Steps = updatedSteps
        };
        WriteFollowupExecutionState(updated);
        RefreshFollowupExecutionOutcomeArtifacts(ResolveRepoRootFromOutputFolder(outputFolder));
        return updated;
    }

    public static ValidationFollowupExecutionState RecordFollowupRerun(
        string outputFolder,
        int stepOrder,
        string stepType,
        string rerunAction,
        string rerunActionLabel,
        string rerunCommandSummary,
        ValidationRunResult rerunResult,
        string outcomeClassification)
    {
        var plan = LoadFollowupPlanForRun(outputFolder);
        if (plan is null)
        {
            return new ValidationFollowupExecutionState(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                outputFolder,
                DateTimeOffset.UtcNow,
                Array.Empty<ValidationFollowupPlanStepState>(),
                null);
        }

        var merged = MergeFollowupExecutionState(plan, LoadFollowupExecutionStateForRun(outputFolder));
        var resultArtifactPath = Path.Combine(rerunResult.OutputFolder, ResultFileName);
        var stabilityArtifactPath = !string.IsNullOrWhiteSpace(rerunResult.StabilityArtifactPath)
            ? rerunResult.StabilityArtifactPath!
            : Path.Combine(rerunResult.OutputFolder, StabilityFileName);
        var updatedSteps = merged.Steps
            .Select(step =>
            {
                if (step.Order != stepOrder || !string.Equals(step.StepType, stepType, StringComparison.Ordinal))
                    return step;

                return step with
                {
                    CompletionState = "completed_by_validation",
                    LastActionKind = rerunAction,
                    Detail = string.IsNullOrWhiteSpace(rerunResult.Summary) ? outcomeClassification : rerunResult.Summary,
                    EvidencePath = resultArtifactPath,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            })
            .OrderBy(step => step.Order)
            .ThenBy(step => step.StepType, StringComparer.Ordinal)
            .ToArray();
        var linkage = new ValidationFollowupRerunLinkage(
            plan.RunId,
            plan.FollowupCategory,
            plan.SourceIntakePath,
            plan.PlanPath,
            plan.OutputFolder,
            stepOrder,
            stepType,
            rerunAction,
            rerunActionLabel,
            rerunCommandSummary,
            rerunResult.RunId,
            rerunResult.OutputFolder,
            resultArtifactPath,
            stabilityArtifactPath,
            outcomeClassification,
            rerunResult.Summary,
            DateTimeOffset.UtcNow);
        var updated = merged with
        {
            RecordedUtc = DateTimeOffset.UtcNow,
            Steps = updatedSteps,
            LatestRerun = linkage
        };
        WriteFollowupExecutionState(updated);
        RefreshFollowupExecutionOutcomeArtifacts(ResolveRepoRootFromOutputFolder(outputFolder));
        return updated;
    }

    private static IReadOnlyList<ValidationRepairPrepSuggestion> BuildFollowupSimilarCaseSuggestions(
        ValidationFollowupIntake intake,
        SemanticReuseIndexLedger index,
        ValidationSettings settings)
    {
        if (!settings.EnableSemanticReuseSuggestions)
            return Array.Empty<ValidationRepairPrepSuggestion>();

        var allowedCaseTypes = GetFollowupAllowedCaseTypes(intake.FollowupCategory);
        var stage = intake.FirstFailure?.StageLabel ?? string.Empty;
        var failingTest = intake.FirstFailure?.FailingTestName ?? string.Empty;
        var excerpt = intake.FirstFailure?.ErrorExcerpt ?? string.Empty;
        var queryTokens = TokenizeFollowupText(string.Join(
            ' ',
            new[]
            {
                intake.FollowupCategory,
                intake.ActionLabel,
                stage,
                failingTest,
                excerpt
            }.Where(value => !string.IsNullOrWhiteSpace(value))));

        return (index.Entries ?? Array.Empty<SemanticReuseIndexedCase>())
            .Where(entry => !string.Equals(entry.SourceRunId, intake.RunId, StringComparison.Ordinal))
            .Where(entry => allowedCaseTypes.Contains(entry.CaseType, StringComparer.Ordinal))
            .Where(entry => settings.IncludePromotedRepairSuggestions || !string.Equals(entry.CaseType, "repair_promotion_outcome", StringComparison.Ordinal))
            .Where(entry => settings.IncludeProviderEpisodeSuggestions || !string.Equals(entry.CaseType, "provider_diagnostics_episode", StringComparison.Ordinal))
            .Where(entry => !settings.OnlyShowPassingOrImprovedReuseCases || IsPositiveReuseOutcome(entry.Outcome))
            .Select(entry => RankFollowupSuggestion(entry, stage, failingTest, excerpt, queryTokens))
            .Where(match => match.Score > 0d)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Suggestion.Title, StringComparer.Ordinal)
            .ThenBy(match => match.Suggestion.PrimaryArtifactPath, StringComparer.Ordinal)
            .Take(settings.MaxSemanticReuseCases)
            .Select(match => match.Suggestion)
            .ToArray();
    }

    private static RankedFollowupSuggestion RankFollowupSuggestion(
        SemanticReuseIndexedCase entry,
        string stage,
        string failingTest,
        string excerpt,
        IReadOnlyList<string> queryTokens)
    {
        var reasons = new List<string>();
        var score = 0d;
        var metadata = entry.Metadata ?? Array.Empty<SemanticReuseMetadataField>();
        var entryStage = FirstNonEmpty(
            GetMetadataValue(metadata, "failing_stage"),
            GetMetadataValue(metadata, "repaired_stage"));
        if (!string.IsNullOrWhiteSpace(stage) &&
            string.Equals(stage, entryStage, StringComparison.OrdinalIgnoreCase))
        {
            score += 4d;
            reasons.Add("same failing stage");
        }

        var entryFailingTest = GetMetadataValue(metadata, "failing_test_name");
        if (!string.IsNullOrWhiteSpace(failingTest) &&
            string.Equals(failingTest, entryFailingTest, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.5d;
            reasons.Add("same failing test");
        }

        var entryExcerpt = GetMetadataValue(metadata, "first_failure_excerpt");
        var excerptSimilarity = ComputeTokenOverlap(excerpt, entryExcerpt);
        if (excerptSimilarity >= 0.55d)
        {
            score += 3d;
            reasons.Add("similar first-failure text");
        }
        else if (excerptSimilarity >= 0.2d)
        {
            score += 1.5d;
            reasons.Add("overlapping failure text");
        }

        var searchSimilarity = ComputeTokenOverlap(queryTokens, TokenizeFollowupText(entry.SearchText));
        if (searchSimilarity >= 0.2d)
        {
            score += searchSimilarity >= 0.5d ? 1.5d : 1d;
            reasons.Add("similar validation context");
        }

        if (IsPositiveReuseOutcome(entry.Outcome))
        {
            score += 1.25d;
            reasons.Add($"later outcome {BuildReuseOutcomeLabel(entry.Outcome)}");
        }

        if (string.Equals(entry.CaseType, "repair_bundle_summary", StringComparison.Ordinal) ||
            string.Equals(entry.CaseType, "repair_promotion_outcome", StringComparison.Ordinal))
        {
            score += 0.5d;
        }

        var rankingLabel = reasons.Contains("same failing stage", StringComparer.Ordinal) &&
                           reasons.Contains("similar first-failure text", StringComparer.Ordinal)
            ? "Exact stage and failure match"
            : reasons.Contains("same failing stage", StringComparer.Ordinal)
                ? "Stage-aligned history"
                : reasons.Contains("similar first-failure text", StringComparer.Ordinal)
                    ? "Failure-text match"
                    : "Related historical case";
        var matchExplanation = reasons.Count == 0
            ? "Related historical case."
            : string.Join("; ", reasons.Take(3)) + ".";
        var suggestion = new ValidationRepairPrepSuggestion(
            "similar_case",
            MapIndexedCaseToFollowupContext(entry.CaseType),
            entry.Title,
            entry.Summary,
            entry.Outcome,
            rankingLabel,
            matchExplanation,
            entry.SourceRunId,
            entry.PrimaryArtifactPath,
            NormalizePaths(entry.ArtifactLinks.Select(link => link.Path)));
        return new RankedFollowupSuggestion(suggestion, score);
    }

    private static IReadOnlyList<ValidationRepairPrepSuggestion> BuildFollowupPlaybookSuggestions(
        ValidationFollowupIntake intake,
        SemanticReusePlaybookCatalog playbookCatalog,
        ValidationSettings settings)
    {
        if (!settings.EnablePlaybookSuggestions)
            return Array.Empty<ValidationRepairPrepSuggestion>();

        var allowedContexts = GetFollowupPlaybookContexts(intake.FollowupCategory);
        var stage = intake.FirstFailure?.StageLabel ?? string.Empty;
        return (playbookCatalog.Entries ?? Array.Empty<SemanticReusePlaybook>())
            .Where(playbook => allowedContexts.Contains(playbook.ContextKind, StringComparer.Ordinal))
            .Where(playbook => settings.ShowTentativePlaybooks || !string.Equals(playbook.Confidence, "tentative", StringComparison.Ordinal))
            .Where(playbook => playbook.EvidenceCount >= settings.MinimumPlaybookEvidenceCount)
            .Select(playbook => new
            {
                Playbook = playbook,
                Score = MapPlaybookConfidence(playbook.Confidence) * 10 +
                        (string.Equals(GetMetadataValue(playbook.MatchMetadata, "failing_stage"), stage, StringComparison.OrdinalIgnoreCase) ? 5 : 0)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Playbook.EvidenceCount)
            .ThenBy(item => item.Playbook.Title, StringComparer.Ordinal)
            .Take(settings.MaxPlaybooksPerContext)
            .Select(item => new ValidationRepairPrepSuggestion(
                "playbook",
                item.Playbook.ContextKind,
                item.Playbook.Title,
                item.Playbook.Summary,
                item.Playbook.Confidence,
                $"{BuildPlaybookConfidenceLabel(item.Playbook.Confidence)} playbook",
                item.Playbook.Explanation,
                string.Empty,
                item.Playbook.LinkedArtifactPaths.FirstOrDefault() ?? string.Empty,
                NormalizePaths((item.Playbook.LinkedArtifactPaths ?? Array.Empty<string>())
                    .Concat(item.Playbook.EvidenceArtifactPaths ?? Array.Empty<string>()))))
            .ToArray();
    }

    private static FollowupTargetScopeAssessment BuildFollowupTargetScopeAssessment(
        string repoRoot,
        ValidationFollowupIntake intake,
        IReadOnlyList<ValidationRepairPrepSuggestion> similarCaseSuggestions)
    {
        var scopes = new List<string>();
        if (!string.IsNullOrWhiteSpace(intake.FirstFailure?.ProjectOrFile))
        {
            scopes.Add(intake.FirstFailure.ProjectOrFile);
        }

        if (string.Equals(intake.FollowupCategory, "investigate_smoke", StringComparison.Ordinal))
        {
            scopes.Add(Path.Combine(repoRoot, "tools", "smoke", "windows", "ui_smoke.ps1"));
        }
        else if (string.Equals(intake.FollowupCategory, "investigate_integrity", StringComparison.Ordinal))
        {
            scopes.Add(Path.Combine(repoRoot, "tools", "verify", "windows_compile_runtime_integrity.ps1"));
        }
        else if (string.Equals(intake.FollowupCategory, "fix_build", StringComparison.Ordinal) && scopes.Count == 0)
        {
            scopes.Add(Path.Combine(repoRoot, "ui", "Shoots.Ui", "Shoots.Ui.csproj"));
        }
        else if (string.Equals(intake.FollowupCategory, "fix_tests", StringComparison.Ordinal) && scopes.Count == 0)
        {
            scopes.Add(Path.Combine(repoRoot, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"));
        }

        var historyLinkedFiles = similarCaseSuggestions
            .SelectMany(suggestion => suggestion.LinkedArtifactPaths)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        if (scopes.Count == 0 && historyLinkedFiles.Length > 0)
        {
            scopes.Add($"History-linked files: {string.Join(", ", historyLinkedFiles)}");
        }

        scopes = scopes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var confidence = scopes.Count == 0
            ? "repo-linked"
            : !string.IsNullOrWhiteSpace(intake.FirstFailure?.ProjectOrFile)
                ? "direct evidence"
                : scopes.Any(scope => scope.StartsWith("History-linked files:", StringComparison.Ordinal))
                    ? "history-linked"
                    : "stage-linked";
        var summary = scopes.Count == 0
            ? "Current repo-linked validation artifacts."
            : $"{string.Join("; ", scopes)} ({confidence}).";
        return new FollowupTargetScopeAssessment(scopes, summary, confidence);
    }

    private static IReadOnlyList<ValidationFollowupPlanStep> BuildFollowupPlanSteps(
        ValidationFollowupIntake intake,
        FollowupTargetScopeAssessment targetScope,
        IReadOnlyList<string> requiredEvidencePaths,
        string rerunRecommendation,
        IReadOnlyList<ValidationRepairPrepSuggestion> similarCaseSuggestions,
        IReadOnlyList<ValidationRepairPrepSuggestion> playbookSuggestions)
    {
        var steps = new List<ValidationFollowupPlanStep>();
        var suggestionEvidence = NormalizePaths(
            similarCaseSuggestions.Select(suggestion => suggestion.PrimaryArtifactPath)
                .Concat(similarCaseSuggestions.SelectMany(suggestion => suggestion.LinkedArtifactPaths))
                .Concat(playbookSuggestions.Select(suggestion => suggestion.PrimaryArtifactPath))
                .Concat(playbookSuggestions.SelectMany(suggestion => suggestion.LinkedArtifactPaths)));

        var firstEvidencePath = requiredEvidencePaths.FirstOrDefault() ?? string.Empty;
        var firstLogPath = requiredEvidencePaths.FirstOrDefault(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) ?? firstEvidencePath;
        var firstArtifactPath = requiredEvidencePaths.FirstOrDefault(path => !path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) ?? firstEvidencePath;

        void AddStep(
            string stepType,
            string title,
            string summary,
            IEnumerable<string>? evidence = null,
            string interactionMode = "manual_only",
            string actionKind = "",
            string actionTarget = "",
            string commandSummary = "")
        {
            steps.Add(new ValidationFollowupPlanStep(
                steps.Count + 1,
                stepType,
                title,
                summary,
                targetScope.ScopeSummary,
                targetScope.ScopeConfidence,
                NormalizePaths(evidence ?? Array.Empty<string>()),
                interactionMode,
                actionKind,
                actionTarget,
                commandSummary));
        }

        switch (intake.FollowupCategory)
        {
            case "fix_build":
                AddStep("inspect_build_error", "Inspect compile error", "Inspect the first compile error and linked build log before editing code.", requiredEvidencePaths, "view_only", "open_log", firstLogPath);
                AddStep("inspect_artifact", "Inspect affected scope", "Inspect the linked project, file, or build artifact scope before preparing any change.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the current build failure with similar prior cases before changing code.", suggestionEvidence);
                AddStep("rerun_build_scope", "Rerun build stage", rerunRecommendation, requiredEvidencePaths, "rerun_capable", "rerun_build_scope", targetScope.ScopeSummary, "dotnet build .\\ui\\Shoots.Ui\\Shoots.Ui.csproj -c Debug -v minimal");
                break;
            case "fix_tests":
                AddStep("inspect_test_failure", "Inspect first failing test", "Inspect the first failing test, excerpt, and linked test log before editing code.", requiredEvidencePaths, "view_only", "open_log", firstLogPath);
                AddStep("inspect_artifact", "Inspect linked test scope", "Inspect the failing project, test assembly, or history-linked scope before preparing a repair.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the current test failure with similar prior cases before preparing a repair.", suggestionEvidence);
                AddStep("rerun_single_test_or_project", "Rerun failing test scope", "Rerun the first failing test or test project once the failure is isolated.", requiredEvidencePaths, "rerun_capable", "rerun_single_test_or_project", firstArtifactPath, "dotnet test .\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj -c Debug -v minimal");
                AddStep("prepare_repair_bundle", "Prepare repair bundle", "Use the repair-prep bundle to gather current evidence and bounded historical hints.", requiredEvidencePaths, "view_only", "open_repair_prep_bundle");
                AddStep("rerun_single_stage", "Rerun UI tests", rerunRecommendation, requiredEvidencePaths, "rerun_capable", "rerun_single_stage", firstArtifactPath, "dotnet test .\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj -c Debug -v minimal");
                break;
            case "investigate_smoke":
                AddStep("inspect_smoke_output", "Inspect smoke output", "Inspect smoke artifacts and logs before code edits.", requiredEvidencePaths, "view_only", "open_log", firstLogPath);
                AddStep("inspect_artifact", "Inspect orchestration notes", "Inspect workspace and orchestration notes tied to the smoke stage.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the smoke failure with prior similar cases before rerunning it.", suggestionEvidence);
                AddStep("rerun_single_stage", "Rerun smoke stage", rerunRecommendation, requiredEvidencePaths, "rerun_capable", "rerun_single_stage", targetScope.ScopeSummary, "powershell -File .\\tools\\smoke\\windows\\ui_smoke.ps1");
                break;
            case "investigate_integrity":
                AddStep("inspect_integrity_output", "Inspect integrity output", "Inspect integrity artifacts, cleanup notes, and linked logs before code edits.", requiredEvidencePaths, "view_only", "open_log", firstLogPath);
                AddStep("inspect_artifact", "Inspect workspace sequencing notes", "Inspect workspace-impact notes before rerunning the integrity gate.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the integrity issue with prior similar cases before rerunning it.", suggestionEvidence);
                AddStep("rerun_single_stage", "Rerun integrity stage", rerunRecommendation, requiredEvidencePaths, "rerun_capable", "rerun_single_stage", targetScope.ScopeSummary, "powershell -File .\\tools\\verify\\windows_compile_runtime_integrity.ps1");
                break;
            case "review_flaky_behavior":
                AddStep("inspect_artifact", "Inspect retry and stability evidence", "Inspect stability, retry, and first-failure artifacts before rerunning the affected stage.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the flaky result with similar prior retry or repair outcomes.", suggestionEvidence);
                AddStep("rerun_single_stage", "Rerun affected stage", rerunRecommendation, requiredEvidencePaths, "rerun_capable", "rerun_single_stage", targetScope.ScopeSummary, BuildFlakyRerunCommandSummary(intake, firstArtifactPath));
                break;
            case "baseline_update_candidate":
                AddStep("inspect_artifact", "Inspect handoff summary", "Inspect the latest validation handoff summary before changing the active release baseline.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                AddStep("inspect_artifact", "Inspect baseline comparison", "Inspect the active baseline comparison and readiness notes before setting a new baseline.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the clean result with related prior validated outputs or playbooks.", suggestionEvidence);
                break;
            default:
                AddStep("inspect_artifact", "Inspect latest evidence", "Inspect the latest validation artifacts before taking follow-up action.", requiredEvidencePaths, "view_only", "open_artifact", firstArtifactPath);
                if (suggestionEvidence.Count > 0)
                    AddStep("review_playbook_or_similar_case", "Review similar history", "Compare the current result with related prior cases before taking the next step.", suggestionEvidence);
                break;
        }

        return steps;
    }

    private static IReadOnlyList<string> BuildFollowupRequiredEvidencePaths(
        ValidationFollowupIntake intake,
        ValidationHandoffBundle? handoff)
    {
        var prioritized = new List<string>();
        if (!string.IsNullOrWhiteSpace(intake.FirstFailure?.LogPath))
            prioritized.Add(intake.FirstFailure.LogPath);

        void AddArtifact(string label)
        {
            var path = intake.ArtifactPaths
                .FirstOrDefault(artifact => string.Equals(artifact.Label, label, StringComparison.Ordinal))
                ?.Path;
            if (!string.IsNullOrWhiteSpace(path))
                prioritized.Add(path!);
        }

        AddArtifact("validation_result.json");
        AddArtifact("validation_stability.json");
        AddArtifact("validation_orchestration.json");
        AddArtifact("validation_baseline_comparison.json");
        AddArtifact("first_failure.log");
        prioritized.Add(intake.HandoffSummaryPath);
        prioritized.Add(intake.HandoffBundlePath);
        prioritized.Add(intake.IntakePath);
        prioritized.Add(intake.PromptPath);
        if (handoff is not null)
        {
            prioritized.Add(handoff.SummaryPath);
            prioritized.Add(handoff.BundlePath);
        }

        return NormalizePaths(prioritized).Take(6).ToArray();
    }

    private static IReadOnlyList<ValidationHandoffArtifactReference> BuildRepairPrepArtifactReferences(
        ValidationFollowupIntake intake,
        ValidationHandoffBundle? handoff,
        ValidationFollowupPlan plan)
    {
        var references = new List<ValidationHandoffArtifactReference>();
        references.AddRange(intake.ArtifactPaths ?? Array.Empty<ValidationHandoffArtifactReference>());
        references.Add(new ValidationHandoffArtifactReference("validation_followup_intake.json", intake.IntakePath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_prompt.txt", intake.PromptPath));
        references.Add(new ValidationHandoffArtifactReference("validation_followup_plan.json", plan.PlanPath));
        references.Add(new ValidationHandoffArtifactReference("validation_handoff_bundle.json", intake.HandoffBundlePath));
        references.Add(new ValidationHandoffArtifactReference("validation_handoff_summary.md", intake.HandoffSummaryPath));
        if (handoff is not null && !string.IsNullOrWhiteSpace(handoff.SummaryPath))
            references.Add(new ValidationHandoffArtifactReference("validation_handoff_summary.md", handoff.SummaryPath));

        return references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Path))
            .GroupBy(reference => $"{reference.Label}|{reference.Path}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(reference => reference.Label, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildFollowupRerunRecommendation(string followupCategory)
        => followupCategory switch
        {
            "fix_build" => "Rerun the build stage only after inspecting the first compile failure.",
            "fix_tests" => "Rerun the first failing test or test project, then rerun the UI test stage.",
            "investigate_smoke" => "Rerun smoke validation only after reviewing the smoke output and orchestration notes.",
            "investigate_integrity" => "Rerun integrity validation only after reviewing integrity output and workspace sequencing notes.",
            "review_flaky_behavior" => "Rerun the affected stage once after reviewing retry and stability artifacts.",
            "baseline_update_candidate" => "No rerun is recommended before reviewing the latest handoff and baseline comparison.",
            _ => "Inspect the latest validation artifacts before deciding whether to rerun a stage."
        };

    private static string BuildFlakyRerunCommandSummary(ValidationFollowupIntake intake, string fallbackTarget)
    {
        var marker = intake.FirstFailure?.StageLabel ?? fallbackTarget;
        if (MatchesFollowupStage(marker, "build"))
            return "dotnet build .\\ui\\Shoots.Ui\\Shoots.Ui.csproj -c Debug -v minimal";
        if (MatchesFollowupStage(marker, "smoke"))
            return "powershell -File .\\tools\\smoke\\windows\\ui_smoke.ps1";
        if (MatchesFollowupStage(marker, "integrity") || MatchesFollowupStage(marker, "repository validation"))
            return "powershell -File .\\tools\\verify\\windows_compile_runtime_integrity.ps1";

        return "dotnet test .\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj -c Debug -v minimal";
    }

    private static string BuildFollowupEscalationHint(ValidationFollowupIntake intake)
    {
        if (!intake.HasRecentRepeatedIssue)
            return "No recurring follow-up signal detected.";

        return intake.FollowupCategory switch
        {
            "fix_tests" => "Recurring test failure across recent validation runs.",
            "investigate_smoke" => "Recurring smoke instability across recent validation runs.",
            "investigate_integrity" => "Recurring integrity or workspace sequencing issue across recent validation runs.",
            "fix_build" => "Recurring build failure across recent validation runs.",
            "review_flaky_behavior" => "Recurring flaky validation behavior across recent validation runs.",
            _ => "Recurring follow-up issue across recent validation runs."
        };
    }

    private static IReadOnlyList<string> GetFollowupAllowedCaseTypes(string followupCategory)
        => followupCategory switch
        {
            "baseline_update_candidate" => new[] { "generated_output_pattern", "baseline_drift_summary", "repair_promotion_outcome" },
            _ => new[] { "validation_failure_record", "repair_bundle_summary", "repair_promotion_outcome", "generated_output_pattern", "baseline_drift_summary", "replay_divergence_summary" }
        };

    private static IReadOnlyList<string> GetFollowupPlaybookContexts(string followupCategory)
        => followupCategory switch
        {
            "baseline_update_candidate" => new[] { "planning" },
            _ => new[] { "validation_failure", "repair_bundle_reference" }
        };

    private static string MapIndexedCaseToFollowupContext(string caseType)
        => caseType switch
        {
            "generated_output_pattern" => "planning",
            "repair_bundle_summary" => "repair_bundle_reference",
            "repair_promotion_outcome" => "repair_bundle_reference",
            _ => "validation_failure"
        };

    private static bool IsPositiveReuseOutcome(string outcome)
        => string.Equals(outcome, "passed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "passed_on_retry", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "improved", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "promoted", StringComparison.OrdinalIgnoreCase);

    private static string BuildReuseOutcomeLabel(string outcome)
        => outcome switch
        {
            "passed_on_retry" => "passed on retry",
            "improved" => "improved",
            "regressed" => "regressed",
            _ => string.IsNullOrWhiteSpace(outcome) ? "recorded" : outcome.Replace('_', ' ')
        };

    private static string BuildPlaybookConfidenceLabel(string confidence)
        => confidence switch
        {
            "trusted" => "Trusted",
            "corroborated" => "Corroborated",
            "tentative" => "Tentative",
            _ => "Evidence-backed"
        };

    private static int MapPlaybookConfidence(string confidence)
        => confidence switch
        {
            "trusted" => 3,
            "corroborated" => 2,
            "tentative" => 1,
            _ => 0
        };

    private static string GetMetadataValue(IEnumerable<SemanticReuseMetadataField>? metadata, string name)
        => (metadata ?? Array.Empty<SemanticReuseMetadataField>())
            .FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal))
            ?.Value
           ?? string.Empty;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static IReadOnlyList<string> TokenizeFollowupText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return Regex.Matches(value.ToLowerInvariant(), "[a-z0-9_./:-]+")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
    }

    private static double ComputeTokenOverlap(string? left, string? right)
        => ComputeTokenOverlap(TokenizeFollowupText(left), TokenizeFollowupText(right));

    private static double ComputeTokenOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0d;

        var leftSet = left.ToHashSet(StringComparer.Ordinal);
        var rightSet = right.ToHashSet(StringComparer.Ordinal);
        var intersection = leftSet.Intersect(rightSet, StringComparer.Ordinal).Count();
        if (intersection == 0)
            return 0d;

        var union = leftSet.Union(rightSet, StringComparer.Ordinal).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string?> paths)
        => (paths ?? Array.Empty<string?>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string BuildFollowupStepKey(int order, string stepType)
        => $"{order:D4}|{stepType}";

    private static string MergeFollowupCompletionState(string existing, string requested)
        => FollowupCompletionRank(requested) >= FollowupCompletionRank(existing)
            ? requested
            : existing;

    private static int FollowupCompletionRank(string state)
        => state switch
        {
            "completed_by_validation" => 5,
            "executed" => 4,
            "copied" => 3,
            "opened" => 2,
            "viewed" => 2,
            "not_started" => 1,
            _ => 0
        };

    private static ValidationHistoryEntry BuildHistoryEntry(ValidationRunResult result)
    {
        var normalizedClassification = string.IsNullOrWhiteSpace(result.StabilityClassification)
            ? (result.Success ? "passed" : "failed")
            : result.StabilityClassification;
        var normalizedStatus = string.IsNullOrWhiteSpace(result.StabilityStatus)
            ? ToStabilityStatus(normalizedClassification)
            : result.StabilityStatus;
        var firstFailedStage = result.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal));
        var firstFailure = result.FirstFailure;

        return new ValidationHistoryEntry(
            result.RunId,
            result.ActionLabel,
            result.OutputFolder,
            Path.Combine(result.OutputFolder, ResultFileName),
            !string.IsNullOrWhiteSpace(result.StabilityArtifactPath)
                ? result.StabilityArtifactPath!
                : Path.Combine(result.OutputFolder, StabilityFileName),
            result.StartedUtc,
            result.CompletedUtc,
            result.Success ? "passed" : "failed",
            normalizedClassification,
            normalizedStatus,
            firstFailure?.Summary ?? result.FirstFailureText ?? string.Empty,
            firstFailure?.StageLabel ?? firstFailedStage?.StageLabel ?? string.Empty,
            firstFailure?.FailingTestName ?? string.Empty,
            result.Stages.Any(stage => stage.RetryCount > 0),
            result.Stages.Sum(stage => Math.Max(0, stage.RetryCount)),
            result.Stages
                .Select(stage => new ValidationHistoryStageOutcome(
                    stage.StageId,
                    stage.StageLabel,
                    stage.Status,
                    string.IsNullOrWhiteSpace(stage.StabilityClassification)
                        ? (string.Equals(stage.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed")
                        : stage.StabilityClassification,
                    stage.RetryCount > 0))
                .ToArray());
    }

    private static ValidationHistoryStageOutcome BuildStageOutcomeSnapshot(ValidationStageResult stage)
        => new(
            stage.StageId,
            stage.StageLabel,
            stage.Status,
            string.IsNullOrWhiteSpace(stage.StabilityClassification)
                ? (string.Equals(stage.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed")
                : stage.StabilityClassification,
            stage.RetryCount > 0);

    private static IReadOnlyDictionary<string, string> BuildStageOutcomeMap(IReadOnlyList<ValidationStageResult> stages)
        => stages.ToDictionary(
            stage => stage.StageLabel,
            stage => $"{stage.Status}/{NormalizeStageClassification(stage)}",
            StringComparer.Ordinal);

    private static string NormalizeRunClassification(ValidationRunResult result)
        => string.IsNullOrWhiteSpace(result.StabilityClassification)
            ? (result.Success ? "passed" : "failed")
            : result.StabilityClassification;

    private static string ResolveStabilityArtifactPath(ValidationRunResult result)
        => !string.IsNullOrWhiteSpace(result.StabilityArtifactPath)
            ? result.StabilityArtifactPath!
            : Path.Combine(result.OutputFolder, StabilityFileName);

    private static string NormalizeStageClassification(ValidationStageResult stage)
        => string.IsNullOrWhiteSpace(stage.StabilityClassification)
            ? (string.Equals(stage.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed")
            : stage.StabilityClassification;

    private static ValidationBaselineComparison BuildBaselineComparison(
        string repoRoot,
        ValidationReleaseBaseline? activeBaseline,
        ValidationRunResult? latestResult,
        ValidationSettings settings)
    {
        var latestClassification = latestResult is null ? "not_run" : NormalizeRunClassification(latestResult);
        var latestResultPath = latestResult is null ? string.Empty : Path.Combine(latestResult.OutputFolder, ResultFileName);
        var latestStabilityArtifactPath = latestResult is null ? string.Empty : ResolveStabilityArtifactPath(latestResult);
        var stageChanges = activeBaseline is not null && latestResult is not null
            ? BuildBaselineStageChanges(activeBaseline.StageOutcomes, latestResult.Stages)
            : Array.Empty<ValidationBaselineStageChange>();
        var changedFailingStages = stageChanges
            .Where(change =>
                !string.Equals(change.LatestStatus, "passed", StringComparison.Ordinal) ||
                !string.Equals(change.LatestStabilityClassification, "passed", StringComparison.Ordinal))
            .Select(change => change.StageLabel)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string driftClassification;
        var driftReasons = new List<string>();
        if (activeBaseline is null)
        {
            driftClassification = "no_baseline";
            driftReasons.Add("No active release baseline recorded.");
        }
        else if (latestResult is null)
        {
            driftClassification = "failure_drift";
            driftReasons.Add("No latest validation result is available for comparison.");
        }
        else if (!latestResult.Success)
        {
            driftClassification = stageChanges.Length > 0 ? "stage_regression_drift" : "failure_drift";
            driftReasons.Add(stageChanges.Length > 0
                ? $"Latest validation diverged from the active baseline in {stageChanges.Length} stage(s)."
                : "Latest validation failed against the active release baseline.");
        }
        else if (string.Equals(latestClassification, "flaky_suspected", StringComparison.Ordinal))
        {
            driftClassification = "flaky_drift";
            driftReasons.Add("Latest validation passed only after a flaky-suspected retry.");
        }
        else if (string.Equals(latestClassification, "passed_on_retry", StringComparison.Ordinal))
        {
            driftClassification = "retry_drift";
            driftReasons.Add("Latest validation passed only after retry.");
        }
        else if (stageChanges.Length > 0)
        {
            driftClassification = "stage_regression_drift";
            driftReasons.Add($"Stage outcomes changed in {stageChanges.Length} stage(s) compared with the active baseline.");
        }
        else
        {
            driftClassification = "no_drift";
            driftReasons.Add("Latest validation matches the active release baseline.");
        }

        var regression = LoadRegressionSummary(repoRoot);
        var (readinessClassification, readinessReasons) = BuildReadinessClassification(
            activeBaseline,
            latestResult,
            latestClassification,
            driftClassification,
            regression,
            settings);

        return new ValidationBaselineComparison(
            activeBaseline?.BaselineId ?? string.Empty,
            activeBaseline?.SourceRunId ?? string.Empty,
            ActiveBaselinePathForRepo(repoRoot),
            activeBaseline?.CommitHash ?? string.Empty,
            latestResult?.RunId ?? string.Empty,
            latestResultPath,
            latestStabilityArtifactPath,
            driftClassification,
            driftReasons,
            changedFailingStages,
            stageChanges,
            readinessClassification,
            readinessReasons,
            latestResult?.CompletedUtc ?? activeBaseline?.CapturedUtc ?? DateTimeOffset.MinValue);
    }

    private static ValidationBaselineStageChange[] BuildBaselineStageChanges(
        IReadOnlyList<ValidationHistoryStageOutcome> baselineStages,
        IReadOnlyList<ValidationStageResult> latestStages)
    {
        var stageIds = baselineStages.Select(stage => stage.StageId)
            .Concat(latestStages.Select(stage => stage.StageId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stageId => stageId, StringComparer.Ordinal)
            .ToArray();

        var changes = new List<ValidationBaselineStageChange>();
        foreach (var stageId in stageIds)
        {
            var baselineStage = baselineStages.FirstOrDefault(stage => string.Equals(stage.StageId, stageId, StringComparison.Ordinal));
            var latestStage = latestStages.FirstOrDefault(stage => string.Equals(stage.StageId, stageId, StringComparison.Ordinal));
            var baselineStatus = baselineStage?.Status ?? "missing";
            var latestStatus = latestStage?.Status ?? "missing";
            var baselineClassification = baselineStage?.StabilityClassification ?? "missing";
            var latestClassification = latestStage is null ? "missing" : NormalizeStageClassification(latestStage);

            if (string.Equals(baselineStatus, latestStatus, StringComparison.Ordinal) &&
                string.Equals(baselineClassification, latestClassification, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new ValidationBaselineStageChange(
                stageId,
                latestStage?.StageLabel ?? baselineStage?.StageLabel ?? stageId,
                baselineStatus,
                latestStatus,
                baselineClassification,
                latestClassification));
        }

        return changes.ToArray();
    }

    private static (string Classification, IReadOnlyList<string> Reasons) BuildReadinessClassification(
        ValidationReleaseBaseline? activeBaseline,
        ValidationRunResult? latestResult,
        string latestClassification,
        string driftClassification,
        ValidationRegressionSummary regression,
        ValidationSettings settings)
    {
        var reasons = new List<string>();
        if (latestResult is null)
        {
            reasons.Add("No latest validation result is available.");
            return ("not_ready", reasons);
        }

        if (!latestResult.Success)
        {
            reasons.Add("Latest validation failed.");
            return ("not_ready", reasons);
        }

        if (string.Equals(driftClassification, "stage_regression_drift", StringComparison.Ordinal) ||
            string.Equals(driftClassification, "failure_drift", StringComparison.Ordinal))
        {
            reasons.Add("Latest validation drifted from the active release baseline.");
            return ("not_ready", reasons);
        }

        if (string.Equals(regression.Classification, "regression_detected", StringComparison.Ordinal))
        {
            reasons.Add("Recent validation history already indicates a regression.");
            return ("not_ready", reasons);
        }

        if (string.Equals(latestClassification, "flaky_suspected", StringComparison.Ordinal) ||
            string.Equals(driftClassification, "flaky_drift", StringComparison.Ordinal))
        {
            reasons.Add("Latest validation required a flaky-suspected retry.");
            return (settings.FlakySuspectedBlocksReleaseReadiness ? "not_ready" : "caution", reasons);
        }

        if (activeBaseline is null)
        {
            reasons.Add("No active release baseline has been set.");
            return ("caution", reasons);
        }

        if (string.Equals(regression.Classification, "flaky_trend_increasing", StringComparison.Ordinal))
        {
            reasons.Add("Flaky trend is increasing across recent validation history.");
            return ("caution", reasons);
        }

        if (string.Equals(latestClassification, "passed_on_retry", StringComparison.Ordinal) ||
            string.Equals(driftClassification, "retry_drift", StringComparison.Ordinal) ||
            string.Equals(regression.Classification, "passed_after_retry", StringComparison.Ordinal))
        {
            reasons.Add("Latest validation passed after retry.");
            return (settings.CountPassedOnRetryAsReleaseReady ? "ready" : "caution", reasons);
        }

        reasons.Add("Latest validation is clean and matches the active release baseline.");
        return ("ready", reasons);
    }

    private static string TryGetCommitHash(string repoRoot)
    {
        try
        {
            var gitDirectory = ResolveGitDirectory(ResolveRepoRoot(repoRoot));
            if (string.IsNullOrWhiteSpace(gitDirectory))
                return string.Empty;

            var headPath = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(headPath))
                return string.Empty;

            var head = File.ReadAllText(headPath).Trim();
            if (string.IsNullOrWhiteSpace(head))
                return string.Empty;

            if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                return head;

            var reference = head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            var refPath = Path.Combine(gitDirectory, reference);
            if (File.Exists(refPath))
                return File.ReadAllText(refPath).Trim();

            var packedRefsPath = Path.Combine(gitDirectory, "packed-refs");
            if (!File.Exists(packedRefsPath))
                return string.Empty;

            foreach (var line in File.ReadLines(packedRefsPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("^", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && string.Equals(parts[1].Replace('/', Path.DirectorySeparatorChar), reference, StringComparison.Ordinal))
                    return parts[0].Trim();
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveGitDirectory(string repoRoot)
    {
        var gitPath = Path.Combine(repoRoot, ".git");
        if (Directory.Exists(gitPath))
            return gitPath;

        if (!File.Exists(gitPath))
            return string.Empty;

        var contents = File.ReadAllText(gitPath).Trim();
        if (!contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var gitDir = contents["gitdir:".Length..].Trim();
        return Path.IsPathRooted(gitDir)
            ? gitDir
            : Path.GetFullPath(Path.Combine(repoRoot, gitDir));
    }

    private static IReadOnlyList<ValidationHistoryEntry> NormalizeHistoryEntries(IEnumerable<ValidationHistoryEntry> entries)
        => entries
            .GroupBy(entry => entry.RunId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(entry => entry.CompletedUtc)
            .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
            .ToArray();

    private static ValidationTrendSummary BuildTrendSummary(
        IReadOnlyList<ValidationHistoryEntry> entries,
        bool countRetryPassesAsStableInSummaries)
    {
        if (entries.Count == 0)
        {
            return new ValidationTrendSummary(0, 0, 0, 0, 0, countRetryPassesAsStableInSummaries, 0, 0, string.Empty, null, DateTimeOffset.MinValue);
        }

        var passCount = entries.Count(entry => string.Equals(entry.OverallResult, "passed", StringComparison.Ordinal));
        var stablePassCount = entries.Count(entry => IsStableForSummary(entry, countRetryPassesAsStableInSummaries));
        var passedOnRetryCount = entries.Count(entry => string.Equals(entry.StabilityClassification, "passed_on_retry", StringComparison.Ordinal));
        var flakySuspectedCount = entries.Count(entry => string.Equals(entry.StabilityClassification, "flaky_suspected", StringComparison.Ordinal));
        var mostCommonFailingStage = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FirstFailureStage))
            .GroupBy(entry => entry.FirstFailureStage, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;
        var lastCleanPassUtc = entries
            .Where(IsCleanPass)
            .Select(entry => (DateTimeOffset?)entry.CompletedUtc)
            .LastOrDefault();

        return new ValidationTrendSummary(
            entries.Count,
            passCount,
            CalculatePercent(passCount, entries.Count),
            stablePassCount,
            CalculatePercent(stablePassCount, entries.Count),
            countRetryPassesAsStableInSummaries,
            passedOnRetryCount,
            flakySuspectedCount,
            mostCommonFailingStage,
            lastCleanPassUtc,
            entries[^1].CompletedUtc);
    }

    private static ValidationRegressionSummary BuildRegressionSummary(
        IReadOnlyList<ValidationHistoryEntry> entries,
        ValidationSettings settings,
        string historyLedgerPath,
        string trendSummaryPath)
    {
        if (entries.Count == 0)
        {
            return new ValidationRegressionSummary(
                "no_history",
                new[] { "No validation history recorded." },
                0,
                "none",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                historyLedgerPath,
                trendSummaryPath,
                DateTimeOffset.MinValue);
        }

        var latest = entries[^1];
        var recentWindow = entries.TakeLast(Math.Min(settings.RegressionComparisonWindow, entries.Count)).ToArray();
        var previousEntries = entries.Count > 1
            ? entries.Take(entries.Count - 1).TakeLast(Math.Min(settings.RegressionComparisonWindow, entries.Count - 1)).ToArray()
            : Array.Empty<ValidationHistoryEntry>();
        var priorWindow = entries.Count > recentWindow.Length
            ? entries.Take(entries.Count - recentWindow.Length).TakeLast(Math.Min(settings.RegressionComparisonWindow, entries.Count - recentWindow.Length)).ToArray()
            : Array.Empty<ValidationHistoryEntry>();

        var reasons = new List<string>();
        var classification = "stable";
        var failureNovelty = "none";
        var currentFailingStage = latest.FirstFailureStage;
        var currentFailingTestName = latest.FailingTestName;
        var latestResultPath = latest.ResultArtifactPath;
        var latestStabilityArtifactPath = latest.StabilityArtifactPath;
        var retryLikeRecentCount = recentWindow.Count(IsRetryClassifiedPass);
        var retryLikePriorCount = priorWindow.Count(IsRetryClassifiedPass);
        var sameStagePriorFailures = previousEntries.Count(entry =>
            !string.Equals(entry.OverallResult, "passed", StringComparison.Ordinal) &&
            string.Equals(entry.FirstFailureStage, currentFailingStage, StringComparison.Ordinal));
        var cleanHistoryBeforeLatest = previousEntries.Length > 0 && previousEntries.All(IsCleanPass);
        var flakyRecurrence = previousEntries.Any(entry =>
            IsRetryClassifiedPass(entry) &&
            MatchesFailureSignature(entry, latest));

        if (!string.Equals(latest.OverallResult, "passed", StringComparison.Ordinal))
        {
            classification = "failed";
            reasons.Add(string.IsNullOrWhiteSpace(currentFailingStage)
                ? "Latest validation run failed."
                : $"Latest validation run failed at stage '{currentFailingStage}'.");

            if (cleanHistoryBeforeLatest)
            {
                classification = "regression_detected";
                failureNovelty = "new_failure_after_clean_history";
                reasons.Add($"Failure followed {previousEntries.Length} clean pass(es).");
            }
            else if (sameStagePriorFailures > 0)
            {
                classification = "regression_detected";
                failureNovelty = "known_recurring_failure";
                reasons.Add($"Stage '{currentFailingStage}' failed in {sameStagePriorFailures + 1} recent run(s).");
            }
        }
        else if (IsRetryClassifiedPass(latest))
        {
            failureNovelty = flakyRecurrence ? "flaky_recurrence" : "none";
            if (retryLikeRecentCount > retryLikePriorCount)
            {
                classification = "flaky_trend_increasing";
                reasons.Add($"Retry-classified passes increased from {retryLikePriorCount} to {retryLikeRecentCount} across recent history.");
            }
            else
            {
                classification = "passed_after_retry";
                reasons.Add(string.Equals(latest.StabilityClassification, "flaky_suspected", StringComparison.Ordinal)
                    ? "Latest validation passed after retry and flaky behavior was suspected."
                    : "Latest validation passed after retry.");
            }

            if (flakyRecurrence)
            {
                reasons.Add("A matching retry-classified failure signature appeared in recent history.");
            }
        }
        else
        {
            classification = "stable";
            reasons.Add("Latest validation passed cleanly.");
        }

        return new ValidationRegressionSummary(
            classification,
            reasons,
            recentWindow.Length,
            failureNovelty,
            currentFailingStage,
            currentFailingTestName,
            latest.RunId,
            latestResultPath,
            latestStabilityArtifactPath,
            historyLedgerPath,
            trendSummaryPath,
            latest.CompletedUtc);
    }

    private static bool IsStableForSummary(ValidationHistoryEntry entry, bool countRetryPassesAsStableInSummaries)
        => string.Equals(entry.OverallResult, "passed", StringComparison.Ordinal) &&
           (string.Equals(entry.StabilityClassification, "passed", StringComparison.Ordinal) ||
            (countRetryPassesAsStableInSummaries &&
             (string.Equals(entry.StabilityClassification, "passed_on_retry", StringComparison.Ordinal) ||
              string.Equals(entry.StabilityClassification, "flaky_suspected", StringComparison.Ordinal))));

    private static bool IsCleanPass(ValidationHistoryEntry entry)
        => string.Equals(entry.OverallResult, "passed", StringComparison.Ordinal) &&
           string.Equals(entry.StabilityClassification, "passed", StringComparison.Ordinal);

    private static bool IsRetryClassifiedPass(ValidationHistoryEntry entry)
        => string.Equals(entry.StabilityClassification, "passed_on_retry", StringComparison.Ordinal) ||
           string.Equals(entry.StabilityClassification, "flaky_suspected", StringComparison.Ordinal);

    private static bool MatchesFailureSignature(ValidationHistoryEntry left, ValidationHistoryEntry right)
    {
        if (!string.IsNullOrWhiteSpace(left.FailingTestName) &&
            !string.IsNullOrWhiteSpace(right.FailingTestName) &&
            string.Equals(left.FailingTestName, right.FailingTestName, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.FirstFailureStage) &&
               !string.IsNullOrWhiteSpace(right.FirstFailureStage) &&
               string.Equals(left.FirstFailureStage, right.FirstFailureStage, StringComparison.Ordinal);
    }

    private static int CalculatePercent(int numerator, int denominator)
        => denominator <= 0
            ? 0
            : (int)Math.Round((double)numerator * 100d / denominator, MidpointRounding.AwayFromZero);

    private static T TryLoadArtifact<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions());
            return loaded is null ? fallback : loaded;
        }
        catch
        {
            return fallback;
        }
    }

    private static string DetermineRunMode(ValidationAction action, ValidationSettings settings)
    {
        if (action == ValidationAction.RunFullValidationLoop)
            return "sequential_standard_mode";

        return ShouldUseIsolatedWorkspace(action, settings)
            ? "isolated_workspace_mode"
            : "single_stage_manual_mode";
    }

    private static bool ShouldUseIsolatedWorkspace(ValidationAction action, ValidationSettings settings)
        => settings.EnableIsolatedValidationWorkspaceMode &&
           (action == ValidationAction.BuildUiProject || action == ValidationAction.RunUiTests);

    private static string BuildIsolationSummary(
        ValidationAction action,
        string runMode,
        IReadOnlyList<ValidationCommandSpec> stages)
    {
        if (string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal))
        {
            return "Manual build and UI test validation runs inside a copied workspace under the validation run folder so repo-root build outputs stay isolated.";
        }

        if (stages.All(stage => stage.SupportsIsolatedWorkspace))
        {
            return "This action can run in isolated workspace mode when the operator enables it.";
        }

        return action switch
        {
            ValidationAction.RunSmokeValidation =>
                "Smoke validation stays on the repo root because it verifies real workspace/run artifact behavior and writes operator-visible evidence.",
            ValidationAction.RunIntegrityValidation =>
                "Integrity validation stays on the repo root because it intentionally clears caches and transient restore artifacts before rebuilding.",
            ValidationAction.RunFullValidationLoop =>
                "Full validation stays sequential on the repo root because smoke and integrity depend on the real workspace state and must not overlap.",
            _ =>
                "Isolated workspace mode is deferred for at least one stage in this action."
        };
    }

    private static string BuildOrchestrationPolicyNote(ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        var fullLoop = DescribeAction(ValidationAction.RunFullValidationLoop, normalized);
        var manualBuild = DescribeAction(ValidationAction.BuildUiProject, normalized);
        var manualTests = DescribeAction(ValidationAction.RunUiTests, normalized);
        var builder = new StringBuilder();
        builder.AppendLine("# Validation orchestration policy");
        builder.AppendLine();
        builder.AppendLine("Shoots serializes validation stages when they share repo-root side effects. The runner never assumes smoke, integrity, or broader validation scripts are safe to overlap.");
        builder.AppendLine();
        builder.AppendLine("## Why smoke and integrity never run in parallel");
        builder.AppendLine("- Smoke validation uses the live repo workspace and writes run artifacts that operators inspect afterward.");
        builder.AppendLine("- Integrity validation runs `git clean -xfd -e .codex/`, clears NuGet caches when possible, and rebuilds the solution from the repo root.");
        builder.AppendLine("- Running them together can invalidate smoke artifacts mid-run, so Shoots always serializes smoke before integrity.");
        builder.AppendLine();
        builder.AppendLine("## Stage classes");
        foreach (var stage in fullLoop.Stages)
        {
            builder.Append("- ");
            builder.Append(stage.StageLabel);
            builder.Append(": ");
            builder.Append(FormatConcurrencyClasses(stage.ConcurrencyClassifications ?? Array.Empty<string>()));
            builder.Append(". Workspace impact: ");
            builder.Append(FormatWorkspaceImpact(new ValidationWorkspaceImpactMetadata(
                stage.TouchesBuildOutputs,
                stage.ClearsCaches,
                stage.RewritesArtifacts,
                stage.ReadsOnly)));
            builder.Append(". ");
            builder.AppendLine(string.IsNullOrWhiteSpace(stage.IsolationSupportReason)
                ? stage.IsolationSupportStatus
                : stage.IsolationSupportReason);
        }

        builder.AppendLine();
        builder.AppendLine("## Isolated workspace mode");
        builder.Append("- ");
        builder.Append(manualBuild.ActionLabel);
        builder.Append(": ");
        builder.AppendLine(manualBuild.IsolationSupportReason);
        builder.Append("- ");
        builder.Append(manualTests.ActionLabel);
        builder.Append(": ");
        builder.AppendLine(manualTests.IsolationSupportReason);
        builder.AppendLine("- Smoke, integrity, and the full validation loop stay on the repo root. Isolated mode does not try to virtualize cache cleaning or artifact-verification behavior.");
        builder.AppendLine();
        builder.AppendLine("## Full validation sequence");
        builder.AppendLine($"- {string.Join(" -> ", fullLoop.Stages.Select(stage => stage.StageLabel))}");
        builder.AppendLine("- Dependencies are declared in code so the same order is enforced in UI actions, logs, and persisted orchestration artifacts.");
        return builder.ToString().TrimEnd() + System.Environment.NewLine;
    }

    private static ValidationOrchestrationReport BuildOrchestrationReport(
        string runId,
        ValidationAction action,
        string repoRoot,
        string outputFolder,
        string runMode,
        string isolatedWorkspacePath,
        string policyNotePath,
        IReadOnlyList<ValidationCommandSpec> commands)
    {
        repoRoot = ResolveRepoRoot(repoRoot);
        var stages = commands
            .Select(command =>
            {
                var workingDirectory = ResolveWorkingDirectory(repoRoot, isolatedWorkspacePath, runMode, command);
                return new ValidationStageOrchestrationEntry(
                    command.StageId,
                    command.StageLabel,
                    (command.DependsOnStageIds ?? Array.Empty<string>()).ToArray(),
                    (command.ConcurrencyClassifications ?? Array.Empty<string>()).ToArray(),
                    command.CanRunIndependently,
                    new ValidationWorkspaceImpactMetadata(
                        command.TouchesBuildOutputs,
                        command.ClearsCaches,
                        command.RewritesArtifacts,
                        command.ReadsOnly),
                    workingDirectory,
                    command.SupportsIsolatedWorkspace,
                    command.IsolationSupportStatus,
                    command.IsolationSupportReason);
            })
            .ToArray();

        var decisions = new List<ValidationOrchestrationDecision>
        {
            new(
                "run_mode",
                string.Empty,
                DisplayLabel(action),
                string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal)
                    ? $"Validation is running in isolated workspace mode at '{isolatedWorkspacePath}'."
                    : $"Validation is running in {FormatRunMode(runMode)}.")
        };

        if (stages.Length > 1)
        {
            decisions.Add(new ValidationOrchestrationDecision(
                "stage_order",
                string.Empty,
                DisplayLabel(action),
                $"Stage order is fixed: {string.Join(" -> ", stages.Select(stage => stage.StageLabel))}."));
        }

        foreach (var stage in stages)
        {
            if (stage.DependsOnStageIds.Count == 0)
                continue;

            var dependencies = stage.DependsOnStageIds
                .Select(dependency => stages.FirstOrDefault(candidate => string.Equals(candidate.StageId, dependency, StringComparison.Ordinal))?.StageLabel ?? dependency)
                .ToArray();
            decisions.Add(new ValidationOrchestrationDecision(
                "dependency",
                stage.StageId,
                stage.StageLabel,
                $"{stage.StageLabel} waits for {string.Join(", ", dependencies)}."));
        }

        if (stages.Any(stage => stage.ConcurrencyClassifications.Contains("workspace_cleaning", StringComparer.Ordinal)))
        {
            decisions.Add(new ValidationOrchestrationDecision(
                "serialization",
                "integrity_validation",
                "Running integrity validation",
                "Integrity validation is serialized because it clears caches and transient restore artifacts that would invalidate overlapping smoke or build activity."));
        }

        if (stages.Any(stage => string.Equals(stage.StageId, "smoke_validation", StringComparison.Ordinal)) &&
            stages.Any(stage => string.Equals(stage.StageId, "integrity_validation", StringComparison.Ordinal)))
        {
            decisions.Add(new ValidationOrchestrationDecision(
                "workspace_conflict",
                "smoke_validation",
                "Running smoke validation",
                "Smoke validation must finish before integrity validation can clean restore artifacts."));
        }

        return new ValidationOrchestrationReport(
            runId,
            DisplayLabel(action),
            runMode,
            repoRoot,
            outputFolder,
            policyNotePath,
            isolatedWorkspacePath,
            stages,
            decisions,
            DateTimeOffset.UtcNow);
    }

    private static string BuildRunStartedMessage(
        string outputFolder,
        string runMode,
        string isolatedWorkspacePath,
        IReadOnlyList<ValidationOrchestrationDecision> decisions)
    {
        var parts = new List<string>
        {
            $"Validation output folder: {outputFolder}.",
            $"Run mode: {FormatRunMode(runMode)}."
        };

        if (!string.IsNullOrWhiteSpace(isolatedWorkspacePath))
        {
            parts.Add($"Isolated workspace: {isolatedWorkspacePath}.");
        }

        var orderingDecision = decisions.FirstOrDefault(decision => string.Equals(decision.DecisionType, "stage_order", StringComparison.Ordinal));
        if (orderingDecision is not null)
        {
            parts.Add(orderingDecision.Summary);
        }

        return string.Join(" ", parts);
    }

    private static string BuildStageStartedMessage(ValidationCommandSpec command, string runMode, string workingDirectory)
    {
        var classifications = FormatConcurrencyClasses(command.ConcurrencyClassifications ?? Array.Empty<string>());
        var workspaceImpact = FormatWorkspaceImpact(new ValidationWorkspaceImpactMetadata(
            command.TouchesBuildOutputs,
            command.ClearsCaches,
            command.RewritesArtifacts,
            command.ReadsOnly));
        var isolationText = string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal)
            ? " Running in isolated workspace mode."
            : string.Empty;
        return $"{command.StageLabel} started in '{workingDirectory}'. Classification: {classifications}. Workspace impact: {workspaceImpact}.{isolationText}";
    }

    private static string ResolveWorkingDirectory(
        string repoRoot,
        string isolatedWorkspacePath,
        string runMode,
        ValidationCommandSpec command)
        => string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal) &&
           command.SupportsIsolatedWorkspace &&
           !string.IsNullOrWhiteSpace(isolatedWorkspacePath)
            ? isolatedWorkspacePath
            : repoRoot;

    private static string CreateIsolatedWorkspace(string repoRoot, string targetRoot)
    {
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, recursive: true);
        }

        Directory.CreateDirectory(targetRoot);
        CopyDirectoryContents(repoRoot, targetRoot);
        return targetRoot;
    }

    private static void CopyDirectoryContents(string sourceRoot, string targetRoot)
    {
        foreach (var directory in Directory.GetDirectories(sourceRoot))
        {
            var name = Path.GetFileName(directory);
            if (ShouldSkipIsolationDirectory(name))
                continue;

            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var childTarget = Path.Combine(targetRoot, name);
            Directory.CreateDirectory(childTarget);
            CopyDirectoryContents(directory, childTarget);
        }

        foreach (var file in Directory.GetFiles(sourceRoot))
        {
            var name = Path.GetFileName(file);
            if (ShouldSkipIsolationFile(name))
                continue;

            File.Copy(file, Path.Combine(targetRoot, name), overwrite: true);
        }
    }

    private static bool ShouldSkipIsolationDirectory(string name)
        => string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, ".codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "TestResults", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipIsolationFile(string name)
        => string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase);

    private static string FormatRunMode(string runMode)
        => runMode switch
        {
            "isolated_workspace_mode" => "isolated workspace mode",
            "single_stage_manual_mode" => "single-stage manual mode",
            _ => "sequential standard mode"
        };

    private static string FormatConcurrencyClasses(IReadOnlyList<string> classes)
    {
        if (classes.Count == 0)
            return "uncategorized";

        return string.Join(", ", classes.Select(FormatConcurrencyClass));
    }

    private static string FormatConcurrencyClass(string value)
        => value switch
        {
            "parallel_safe" => "parallel-safe",
            "repo_mutating" => "repo-mutating",
            "workspace_cleaning" => "workspace-cleaning",
            "exclusive" => "exclusive",
            _ => value.Replace('_', ' ')
        };

    private static string FormatWorkspaceImpact(ValidationWorkspaceImpactMetadata impact)
    {
        var labels = new List<string>();
        if (impact.TouchesBuildOutputs)
            labels.Add("touches build outputs");
        if (impact.ClearsCaches)
            labels.Add("clears caches");
        if (impact.RewritesArtifacts)
            labels.Add("rewrites artifacts");
        if (impact.ReadsOnly || labels.Count == 0)
            labels.Add(impact.ReadsOnly ? "reads only" : "no additional workspace mutation");

        return string.Join(", ", labels);
    }

    private static IReadOnlyList<ValidationCommandSpec> BuildCommands(ValidationAction action, bool includeValidateBuild, string runMode)
    {
        var commands = new List<ValidationCommandSpec>();
        var useIsolatedWorkspace = string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal);
        if (action is ValidationAction.BuildUiProject or ValidationAction.RunFullValidationLoop)
        {
            commands.Add(new ValidationCommandSpec(
                "build_ui",
                "Building UI",
                "dotnet",
                new[] { "build", @".\ui\Shoots.Ui\Shoots.Ui.csproj", "-c", "Debug", "-v", "minimal" },
                "01-build-ui.log",
                Array.Empty<string>(),
                useIsolatedWorkspace ? new[] { "parallel_safe" } : new[] { "repo_mutating" },
                true,
                true,
                false,
                false,
                false,
                true,
                useIsolatedWorkspace ? "active" : "supported",
                "Manual build validation can run in an isolated copied workspace."));
        }

        if (action is ValidationAction.RunUiTests or ValidationAction.RunFullValidationLoop)
        {
            commands.Add(new ValidationCommandSpec(
                "ui_tests",
                "Running UI tests",
                "dotnet",
                new[] { "test", @".\ui\Shoots.Ui.Tests\Shoots.Ui.Tests.csproj", "-c", "Debug", "-v", "minimal" },
                commands.Count == 0 ? "01-ui-tests.log" : $"{commands.Count + 1:00}-ui-tests.log",
                action == ValidationAction.RunFullValidationLoop ? new[] { "build_ui" } : Array.Empty<string>(),
                useIsolatedWorkspace ? new[] { "parallel_safe" } : new[] { "repo_mutating" },
                true,
                true,
                false,
                false,
                false,
                true,
                useIsolatedWorkspace ? "active" : "supported",
                "Manual UI test validation can run in an isolated copied workspace."));
        }

        if (action is ValidationAction.RunSmokeValidation or ValidationAction.RunFullValidationLoop)
        {
            commands.Add(new ValidationCommandSpec(
                "smoke_validation",
                "Running smoke validation",
                "powershell",
                new[] { "-File", @".\tools\smoke\windows\ui_smoke.ps1" },
                $"{commands.Count + 1:00}-smoke-validation.log",
                action == ValidationAction.RunFullValidationLoop ? new[] { "ui_tests" } : Array.Empty<string>(),
                new[] { "exclusive", "repo_mutating" },
                true,
                true,
                false,
                true,
                false,
                false,
                "deferred",
                "Smoke validation stays on the repo root because it verifies real workspace/run artifact behavior."));
        }

        if (action is ValidationAction.RunIntegrityValidation or ValidationAction.RunFullValidationLoop)
        {
            commands.Add(new ValidationCommandSpec(
                "integrity_validation",
                "Running integrity validation",
                "powershell",
                new[] { "-File", @".\tools\verify\windows_compile_runtime_integrity.ps1" },
                $"{commands.Count + 1:00}-integrity-validation.log",
                action == ValidationAction.RunFullValidationLoop ? new[] { "smoke_validation" } : Array.Empty<string>(),
                new[] { "exclusive", "workspace_cleaning", "repo_mutating" },
                true,
                true,
                true,
                true,
                false,
                false,
                "deferred",
                "Integrity validation stays on the repo root because it intentionally cleans caches and restore artifacts."));
        }

        if (action is ValidationAction.RunFullValidationLoop && includeValidateBuild)
        {
            commands.Add(new ValidationCommandSpec(
                "validate_build",
                "Running repository validation",
                "powershell",
                new[] { "-ExecutionPolicy", "Bypass", "-File", @".\scripts\validate_build.ps1" },
                $"{commands.Count + 1:00}-validate-build.log",
                new[] { "integrity_validation" },
                new[] { "exclusive", "repo_mutating" },
                false,
                true,
                false,
                true,
                false,
                false,
                "deferred",
                "Repository validation stays on the repo root because it writes repo-scoped artifacts and depends on the standard validation sequence."));
        }

        return commands;
    }

    private static string DisplayLabel(ValidationAction action)
        => action switch
        {
            ValidationAction.BuildUiProject => "Build UI project",
            ValidationAction.RunUiTests => "Run UI tests",
            ValidationAction.RunSmokeValidation => "Run smoke validation",
            ValidationAction.RunIntegrityValidation => "Run integrity validation",
            ValidationAction.RunFullValidationLoop => "Run full validation loop",
            _ => action.ToString()
        };

    private static string ActionToken(ValidationAction action)
        => action switch
        {
            ValidationAction.BuildUiProject => "build-ui",
            ValidationAction.RunUiTests => "ui-tests",
            ValidationAction.RunSmokeValidation => "smoke",
            ValidationAction.RunIntegrityValidation => "integrity",
            ValidationAction.RunFullValidationLoop => "full",
            _ => "validation"
        };

    private static ValidationFailureAnalysis AnalyzeFailure(ValidationCommandSpec command, ValidationCommandExecutionResult execution)
    {
        var excerpt = FindFailureExcerpt(execution.OutputLines)
            ?? $"{command.StageLabel} failed with exit code {execution.ExitCode}.";
        return new ValidationFailureAnalysis(
            FindProjectOrFile(command, execution.OutputLines),
            FindFailingTestName(execution.OutputLines),
            excerpt,
            excerpt);
    }

    private static string SummarizeSuccess(string stageLabel, ValidationCommandExecutionResult execution)
    {
        var line = execution.OutputLines
            .LastOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) &&
                                        !candidate.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase) &&
                                        !candidate.Contains("error ", StringComparison.OrdinalIgnoreCase))?
            .Trim();
        return !string.IsNullOrWhiteSpace(line)
            ? line
            : $"{stageLabel} passed.";
    }

    private static string SummarizeFailure(string stageLabel, ValidationCommandExecutionResult execution)
    {
        var excerpt = FindFailureExcerpt(execution.OutputLines);
        return !string.IsNullOrWhiteSpace(excerpt)
            ? excerpt
            : $"{stageLabel} failed with exit code {execution.ExitCode}.";
    }

    private static string BuildRetrySuccessSummary(string stageLabel, string firstFailureExcerpt, string classification)
    {
        var label = classification switch
        {
            "flaky_suspected" => "passed on retry; flaky behavior suspected",
            _ => "passed on retry"
        };

        return $"{stageLabel} {label}. First failure: {firstFailureExcerpt}";
    }

    private static string DetermineRunClassification(bool success, IReadOnlyList<ValidationStageResult> stages)
    {
        if (!success)
            return "failed";

        if (stages.Any(stage => string.Equals(stage.StabilityClassification, "flaky_suspected", StringComparison.Ordinal)))
            return "flaky_suspected";

        if (stages.Any(stage => string.Equals(stage.StabilityClassification, "passed_on_retry", StringComparison.Ordinal)))
            return "passed_on_retry";

        return "passed";
    }

    private static string ToStabilityStatus(string classification)
        => classification switch
        {
            "passed_on_retry" => "Passed after retry",
            "flaky_suspected" => "Flaky suspected",
            "failed" => "Failed",
            _ => "Passed cleanly"
        };

    private static string BuildRunSummary(string classification, int stageCount, string? firstFailureText)
        => classification switch
        {
            "passed_on_retry" => $"Validation passed after retry ({stageCount} stage{(stageCount == 1 ? string.Empty : "s")}).",
            "flaky_suspected" => $"Validation passed after retry; flaky behavior suspected ({stageCount} stage{(stageCount == 1 ? string.Empty : "s")}).",
            "failed" => $"Validation failed: {firstFailureText ?? "Unknown failure."}",
            _ => $"Validation passed ({stageCount} stage{(stageCount == 1 ? string.Empty : "s")})."
        };

    private static string ClassifyRetrySuccess(ValidationCommandSpec command)
        => IsFlakySuspectedStage(command)
            ? "flaky_suspected"
            : "passed_on_retry";

    private static bool IsFlakySuspectedStage(ValidationCommandSpec command)
        => string.Equals(command.StageId, "ui_tests", StringComparison.Ordinal)
            || string.Equals(command.StageId, "validate_build", StringComparison.Ordinal)
            || (string.Equals(command.FileName, "dotnet", StringComparison.OrdinalIgnoreCase)
                && command.Arguments.Count > 0
                && string.Equals(command.Arguments[0], "test", StringComparison.OrdinalIgnoreCase));

    private static string BuildCommandLine(ValidationCommandSpec command)
        => string.Join(" ", new[] { command.FileName }.Concat(command.Arguments).Select(QuoteCommandToken));

    private static string QuoteCommandToken(string token)
        => string.IsNullOrWhiteSpace(token) || token.Contains(' ') || token.Contains('\t')
            ? $"\"{token.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : token;

    private static string ResolveCommandTarget(ValidationCommandSpec command)
        => command.Arguments.FirstOrDefault(argument =>
               argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               argument.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               argument.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
           ?? command.StageLabel;

    private static string FindProjectOrFile(ValidationCommandSpec command, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = TestRunPattern.Match(line);
            if (match.Success)
                return match.Groups["path"].Value.Trim();
        }

        return ResolveCommandTarget(command);
    }

    private static string FindFailingTestName(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var xunit = XunitFailurePattern.Match(line);
            if (xunit.Success)
                return xunit.Groups["name"].Value.Trim();

            var vstest = VstestFailurePattern.Match(line);
            if (vstest.Success)
                return vstest.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }

    private static string? FindFailureExcerpt(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();
            if (ErrorPattern.IsMatch(trimmed))
                return trimmed;
        }

        return lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
    }

    private static string ResolveRepoRoot(string? repoRoot)
    {
        var current = string.IsNullOrWhiteSpace(repoRoot)
            ? AppContext.BaseDirectory
            : repoRoot;

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "Shoots.sln")))
                return Path.GetFullPath(current);

            current = Path.GetDirectoryName(current);
        }

        return Path.GetFullPath(repoRoot ?? Directory.GetCurrentDirectory());
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}

internal sealed class ValidationCommandExecutor : IValidationCommandExecutor
{
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const int ShowHidden = 0;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    public async Task<ValidationCommandExecutionResult> ExecuteAsync(
        ValidationCommandSpec command,
        string workingDirectory,
        string logPath,
        Action<string> onOutput,
        CancellationToken ct)
    {
        var outputLines = new List<string>();
        var directory = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(directory);

        var exitCodePath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(logPath)}.exitcode");
        var scriptPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(logPath)}.cmd");

        File.WriteAllText(scriptPath, BuildBatchScript(command, workingDirectory, logPath, exitCodePath));

        var processHandle = LaunchBatch(scriptPath, workingDirectory);
        try
        {
            using var registration = ct.Register(() => TryTerminate(processHandle));
            long position = 0;

            while (true)
            {
                DrainLog(logPath, ref position, outputLines, onOutput);
                var waitResult = WaitForSingleObject(processHandle, 0);
                if (waitResult == WaitObject0)
                    break;
                if (waitResult != WaitTimeout)
                    throw new IOException($"Validation command wait failed with code {waitResult}.");

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            DrainLog(logPath, ref position, outputLines, onOutput);
            var exitCode = ReadExitCode(exitCodePath, processHandle);
            return new ValidationCommandExecutionResult(exitCode, outputLines);
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static string BuildBatchScript(ValidationCommandSpec command, string workingDirectory, string logPath, string exitCodePath)
    {
        var commandLine = string.Join(" ", new[] { QuoteBatchToken(command.FileName) }.Concat(command.Arguments.Select(QuoteBatchToken)));
        return string.Join(
            System.Environment.NewLine,
            new[]
            {
                "@echo off",
                $"cd /d {QuoteBatchToken(workingDirectory)}",
                $"{commandLine} > {QuoteBatchToken(logPath)} 2>&1",
                "set SHOOTS_EXITCODE=%errorlevel%",
                $"> {QuoteBatchToken(exitCodePath)} echo %SHOOTS_EXITCODE%",
                "exit /b %SHOOTS_EXITCODE%"
            });
    }

    private static nint LaunchBatch(string scriptPath, string workingDirectory)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskNoCloseProcess,
            lpFile = "cmd.exe",
            lpParameters = $"/d /c {QuoteBatchToken(scriptPath)}",
            lpDirectory = workingDirectory,
            nShow = ShowHidden
        };

        if (!ShellExecuteEx(ref info) || info.hProcess == nint.Zero)
            throw new IOException("Could not start validation command.");

        return info.hProcess;
    }

    private static void DrainLog(string logPath, ref long position, List<string> outputLines, Action<string> onOutput)
    {
        if (!File.Exists(logPath))
            return;

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (position > stream.Length)
            position = 0;

        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            outputLines.Add(line);
            onOutput(line);
        }

        position = stream.Position;
    }

    private static int ReadExitCode(string exitCodePath, nint processHandle)
    {
        if (File.Exists(exitCodePath) &&
            int.TryParse(File.ReadAllText(exitCodePath).Trim(), out var parsed))
        {
            return parsed;
        }

        return GetExitCodeProcess(processHandle, out var exitCode)
            ? unchecked((int)exitCode)
            : -1;
    }

    private static void TryTerminate(nint processHandle)
    {
        if (processHandle != nint.Zero)
        {
            TerminateProcess(processHandle, 1);
        }
    }

    private static string QuoteBatchToken(string token)
        => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo execInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public string? lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }
}
