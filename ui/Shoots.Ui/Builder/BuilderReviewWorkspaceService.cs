using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderConversationChangedFileRecord(
    string Path,
    string FileCategory,
    string ChangeKind,
    string ChangeSummary,
    bool ReviewReady);

public sealed record BuilderConversationStageRecord(
    string StageId,
    string StageLabel,
    string StageState,
    string Detail,
    IReadOnlyList<string> LinkedArtifactPaths);

public sealed record BuilderConversationExecutionSessionRecord(
    string SessionId,
    string SourceConversationIntakeId,
    string SourceConversationHandoffId,
    string RawRequestText,
    string NormalizedTaskClass,
    string SelectedRoute,
    string StackId,
    string StackLabel,
    string ToolchainContextSummary,
    string SessionState,
    string CurrentStageId,
    string CurrentStageLabel,
    string ReviewState,
    string ValidationSummary,
    string SourceExecutionPrepPath,
    string LaunchArtifactPath,
    string ResultArtifactPath,
    string PatchReviewPath,
    string PatchReviewOutcomePath,
    IReadOnlyList<BuilderConversationChangedFileRecord> ChangedFiles,
    IReadOnlyList<BuilderConversationStageRecord> Stages,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchReviewChangedFileRecord(
    string Path,
    string FileCategory,
    string ChangeKind,
    string ChangeSummary,
    bool ReviewReady);

public sealed record BuilderPatchReviewRecord(
    string SessionId,
    string SourceConversationIntakeId,
    string SourceConversationHandoffId,
    string RouteUsed,
    string StackId,
    string StackLabel,
    string ValidationSummary,
    string ReviewReadinessState,
    IReadOnlyList<BuilderPatchReviewChangedFileRecord> ChangedFiles,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchDiffReviewFileEntryRecord(
    string RelativePath,
    string FileCategory,
    string ChangeKind,
    string DiffSummary,
    string PatchPreviewText,
    string ApprovalState,
    string RejectionReason,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchDiffReviewRecord(
    string SessionId,
    string SourcePatchReviewId,
    string SourcePatchReviewPath,
    string OverallFileReviewState,
    string ReviewReadinessState,
    IReadOnlyList<BuilderPatchDiffReviewFileEntryRecord> FileEntries,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFileReviewDecisionEntryRecord(
    string RelativePath,
    string ApprovalState,
    string OperatorDecisionSource,
    string RejectionReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFileReviewDecisionRecord(
    string SessionId,
    string SourcePatchDiffReviewId,
    string OverallFileReviewState,
    IReadOnlyList<BuilderFileReviewDecisionEntryRecord> Entries,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchReviewOutcomeRecord(
    string SessionId,
    string ReviewDecisionState,
    string SessionState,
    string ReviewState,
    string ReviewNote,
    string RerouteRoute,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchApplyDecisionRecord(
    string SessionId,
    string OverallFileApprovalState,
    string ApplyEligibilityState,
    IReadOnlyList<string> BlockReasons,
    string FinalizationState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewWorkspaceCounts(
    int TotalChangedFiles,
    int PendingFiles,
    int ApprovedFiles,
    int RejectedFiles,
    int NeedsRevisionFiles,
    string FinalizeEligibilityState);

public sealed record BuilderReviewWorkspaceFileRecord(
    string RelativePath,
    string DirectoryPath,
    string FileHeader,
    string FileCategory,
    string FilePurpose,
    string ProjectArea,
    string FeatureArea,
    string ChangeKind,
    string ChangeSummary,
    string DiffSummary,
    string BoundedDiffExcerpt,
    string ApprovalState,
    string RejectionReason,
    string RelatedTestFilePath,
    string ContextSummary,
    string HighRiskCategory,
    bool RequiresExplicitApproval,
    int PriorityRank,
    string PriorityLabel,
    IReadOnlyList<string> LinkedArtifactPaths,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewWorkspaceGroupRecord(
    string GroupKind,
    string GroupKey,
    string GroupLabel,
    int TotalFiles,
    int PendingFiles,
    int ApprovedFiles,
    int RejectedFiles,
    int NeedsRevisionFiles,
    int HighRiskFiles,
    IReadOnlyList<BuilderReviewWorkspaceFileRecord> Files);

public sealed record BuilderReviewWorkspaceRecord(
    string ExecutionSessionId,
    string PatchReviewId,
    string PatchDiffReviewId,
    string GroupingUsed,
    IReadOnlyList<string> ActiveFilters,
    BuilderReviewWorkspaceCounts ReviewCounts,
    IReadOnlyList<BuilderReviewWorkspaceGroupRecord> FileGroups,
    IReadOnlyList<string> NavigationOrder,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewNavigationStateRecord(
    string CurrentFilePath,
    string CurrentFilter,
    string CurrentGroup,
    string NextPendingFile,
    string NextRejectedFile,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewEfficiencySummaryRecord(
    int TotalFiles,
    int FilesReviewedThisSession,
    string FirstPendingFile,
    string FirstRejectedFile,
    double ApprovalCompletionPercentage,
    string FinalizeReadiness,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewWorkspaceHistoryEntryRecord(
    string SessionId,
    string GroupingUsed,
    IReadOnlyList<string> FiltersUsed,
    BuilderReviewWorkspaceCounts ReviewCounts,
    string FinalReviewState,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewWorkspaceHistoryRecord(
    int RetentionCount,
    IReadOnlyList<BuilderReviewWorkspaceHistoryEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderHighRiskFileFlagEntryRecord(
    string FilePath,
    string RiskCategory,
    bool RequiresExplicitApproval,
    DateTimeOffset ObservedUtc);

public sealed record BuilderHighRiskFileFlagsRecord(
    string ExecutionSessionId,
    IReadOnlyList<BuilderHighRiskFileFlagEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewQueuePriorityEntryRecord(
    string RelativePath,
    int PriorityRank,
    string PriorityLabel,
    string ApprovalState,
    string HighRiskCategory,
    bool RequiresExplicitApproval);

public sealed record BuilderReviewQueueRecord(
    string ExecutionSessionId,
    string PatchReviewId,
    IReadOnlyList<string> QueueOrder,
    IReadOnlyList<string> PendingFiles,
    IReadOnlyList<string> RejectedFiles,
    IReadOnlyList<string> RevisionFiles,
    IReadOnlyList<string> ApprovedFiles,
    IReadOnlyList<BuilderHighRiskFileFlagEntryRecord> HighRiskFiles,
    IReadOnlyList<BuilderReviewQueuePriorityEntryRecord> PriorityOrdering,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewQueueNavigationRecord(
    string CurrentFilePath,
    string NextPriorityFile,
    string NextPendingFile,
    string NextRejectedFile,
    string NextRevisionFile,
    string NextHighRiskFile,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderBatchReviewActionEntryRecord(
    string SessionId,
    string ActionType,
    string ScopeType,
    string ScopeValue,
    IReadOnlyList<string> AffectedFiles,
    string OperatorConfirmationState,
    DateTimeOffset ObservedUtc);

public sealed record BuilderBatchReviewActionsRecord(
    string SessionId,
    IReadOnlyList<BuilderBatchReviewActionEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReviewWorkspacePreferences(
    string CurrentFilter = "all",
    string GroupBy = "directory",
    string CurrentFilePath = "");

public sealed record BuilderBatchReviewActionRequest(
    string ActionType,
    string ScopeType,
    string ScopeValue,
    string CurrentFilter = "all",
    string CurrentGrouping = "directory",
    string CurrentFilePath = "",
    string OperatorConfirmationState = "confirmed");

public sealed record BuilderReviewArtifactSet(
    BuilderConversationExecutionSessionRecord? ExecutionSession,
    BuilderPatchReviewRecord? PatchReview,
    BuilderPatchDiffReviewRecord? PatchDiffReview,
    BuilderFileReviewDecisionRecord? FileReviewDecision,
    BuilderPatchReviewOutcomeRecord? PatchReviewOutcome,
    BuilderPatchApplyDecisionRecord? PatchApplyDecision,
    string PatchBundlePath);

public sealed record BuilderReviewWorkspaceContext(
    BuilderReviewArtifactSet Sources,
    BuilderReviewWorkspaceRecord Workspace,
    BuilderReviewNavigationStateRecord NavigationState,
    BuilderReviewEfficiencySummaryRecord EfficiencySummary,
    BuilderReviewWorkspaceHistoryRecord WorkspaceHistory,
    BuilderReviewQueueRecord Queue,
    BuilderReviewQueueNavigationRecord QueueNavigation,
    BuilderHighRiskFileFlagsRecord HighRiskFlags,
    BuilderBatchReviewActionsRecord BatchReviewActions);

public static class BuilderReviewWorkspaceService
{
    public const string ConversationExecutionSessionFileName = "builder_conversation_execution_session.json";
    public const string PatchReviewFileName = "builder_patch_review.json";
    public const string PatchDiffReviewFileName = "builder_patch_diff_review.json";
    public const string FileReviewDecisionFileName = "builder_file_review_decision.json";
    public const string PatchReviewOutcomeFileName = "builder_patch_review_outcome.json";
    public const string PatchApplyDecisionFileName = "builder_patch_apply_decision.json";
    public const string PatchBundleFileName = "builder_patch_bundle.patch";
    public const string ReviewWorkspaceFileName = "builder_review_workspace.json";
    public const string ReviewNavigationStateFileName = "builder_review_navigation_state.json";
    public const string ReviewEfficiencySummaryFileName = "builder_review_efficiency_summary.json";
    public const string ReviewWorkspaceHistoryFileName = "builder_review_workspace_history.json";
    public const string ReviewQueueFileName = "builder_review_queue.json";
    public const string ReviewQueueNavigationFileName = "builder_review_queue_navigation.json";
    public const string HighRiskFileFlagsFileName = "builder_high_risk_file_flags.json";
    public const string BatchReviewActionsFileName = "builder_batch_review_actions.json";

    private const int DefaultHistoryRetentionCount = 12;
    private const int DefaultBatchActionRetentionCount = 32;
    private const int MaxExcerptLines = 12;
    private const int MaxExcerptCharacters = 900;
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string ConversationExecutionSessionPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ConversationExecutionSessionFileName);

    public static string PatchReviewPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), PatchReviewFileName);

    public static string PatchDiffReviewPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), PatchDiffReviewFileName);

    public static string FileReviewDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), FileReviewDecisionFileName);

    public static string PatchReviewOutcomePathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), PatchReviewOutcomeFileName);

    public static string PatchApplyDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), PatchApplyDecisionFileName);

    public static string PatchBundlePathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), PatchBundleFileName);

    public static string ReviewWorkspacePathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewWorkspaceFileName);

    public static string ReviewNavigationStatePathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewNavigationStateFileName);

    public static string ReviewEfficiencySummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewEfficiencySummaryFileName);

    public static string ReviewWorkspaceHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewWorkspaceHistoryFileName);

    public static string ReviewQueuePathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewQueueFileName);

    public static string ReviewQueueNavigationPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), ReviewQueueNavigationFileName);

    public static string HighRiskFileFlagsPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), HighRiskFileFlagsFileName);

    public static string BatchReviewActionsPathForRepo(string repoRoot)
        => Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), BatchReviewActionsFileName);

    public static BuilderReviewWorkspaceContext? RefreshWorkspaceArtifacts(
        string repoRoot,
        BuilderReviewWorkspacePreferences? preferences = null,
        int historyRetentionCount = DefaultHistoryRetentionCount,
        DateTimeOffset? observedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentNullException(nameof(repoRoot));

        var sources = LoadArtifacts(repoRoot);
        if (sources.ExecutionSession is null &&
            sources.PatchReview is null &&
            sources.PatchDiffReview is null &&
            sources.FileReviewDecision is null)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var filter = NormalizeFilter(preferences?.CurrentFilter);
        var grouping = NormalizeGrouping(preferences?.GroupBy);
        var preferredCurrentFile = NormalizeRelativePath(preferences?.CurrentFilePath);
        var existingHistory = LoadWorkspaceHistory(repoRoot);
        var existingBatchActions = NormalizeBatchReviewActions(
            repoRoot,
            ResolveExecutionSessionId(sources),
            LoadBatchReviewActions(repoRoot),
            effectiveObservedUtc);
        var files = BuildFileRecords(repoRoot, sources, effectiveObservedUtc);
        var counts = BuildCounts(files);
        var visibleFiles = files
            .Where(file => MatchesFilter(file, filter))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var groups = BuildGroups(visibleFiles, grouping);
        var currentFilePath = ResolveCurrentFilePath(preferredCurrentFile, visibleFiles);
        var navigation = new BuilderReviewNavigationStateRecord(
            currentFilePath,
            filter,
            ResolveCurrentGroupKey(groups, currentFilePath),
            FindFirstFile(visibleFiles, file => IsPendingState(file.ApprovalState)),
            FindFirstFile(visibleFiles, file => IsRejectedState(file.ApprovalState)),
            ReviewNavigationStatePathForRepo(repoRoot),
            effectiveObservedUtc);
        var workspace = new BuilderReviewWorkspaceRecord(
            ResolveExecutionSessionId(sources),
            ResolvePatchReviewId(sources),
            ResolvePatchDiffReviewId(sources),
            grouping,
            new[] { filter },
            counts,
            groups,
            visibleFiles.Select(file => file.RelativePath).ToArray(),
            BuildLinkedArtifactPaths(repoRoot, sources),
            BuildWorkspaceSummary(counts, filter, grouping),
            ReviewWorkspacePathForRepo(repoRoot),
            effectiveObservedUtc);
        var efficiency = BuildEfficiencySummary(repoRoot, files, counts, effectiveObservedUtc);
        var history = BuildHistory(existingHistory, workspace, navigation, historyRetentionCount, effectiveObservedUtc, repoRoot);
        var highRiskFlags = BuildHighRiskFlags(repoRoot, workspace.ExecutionSessionId, files, effectiveObservedUtc);
        var queue = BuildReviewQueue(repoRoot, sources, files, highRiskFlags, effectiveObservedUtc);
        var queueNavigation = BuildQueueNavigation(repoRoot, queue, currentFilePath, effectiveObservedUtc);

        Directory.CreateDirectory(BuilderExecutionService.BuilderProofRootForRepo(repoRoot));
        Save(workspace.ArtifactPath, workspace);
        Save(navigation.ArtifactPath, navigation);
        Save(efficiency.ArtifactPath, efficiency);
        Save(history.ArtifactPath, history);
        Save(queue.ArtifactPath, queue);
        Save(queueNavigation.ArtifactPath, queueNavigation);
        Save(highRiskFlags.ArtifactPath, highRiskFlags);
        Save(existingBatchActions.ArtifactPath, existingBatchActions);

        return new BuilderReviewWorkspaceContext(
            sources,
            workspace,
            navigation,
            efficiency,
            history,
            queue,
            queueNavigation,
            highRiskFlags,
            existingBatchActions);
    }

    public static BuilderReviewWorkspaceRecord? LoadWorkspace(string repoRoot)
        => Load<BuilderReviewWorkspaceRecord>(ReviewWorkspacePathForRepo(repoRoot));

    public static BuilderReviewNavigationStateRecord? LoadNavigationState(string repoRoot)
        => Load<BuilderReviewNavigationStateRecord>(ReviewNavigationStatePathForRepo(repoRoot));

    public static BuilderReviewEfficiencySummaryRecord? LoadEfficiencySummary(string repoRoot)
        => Load<BuilderReviewEfficiencySummaryRecord>(ReviewEfficiencySummaryPathForRepo(repoRoot));

    public static BuilderReviewWorkspaceHistoryRecord LoadWorkspaceHistory(string repoRoot)
        => Load(ReviewWorkspaceHistoryPathForRepo(repoRoot), new BuilderReviewWorkspaceHistoryRecord(
            DefaultHistoryRetentionCount,
            Array.Empty<BuilderReviewWorkspaceHistoryEntryRecord>(),
            "No builder review workspace history recorded.",
            ReviewWorkspaceHistoryPathForRepo(repoRoot),
            DateTimeOffset.MinValue));

    public static BuilderReviewQueueRecord? LoadReviewQueue(string repoRoot)
        => Load<BuilderReviewQueueRecord>(ReviewQueuePathForRepo(repoRoot));

    public static BuilderReviewQueueNavigationRecord? LoadReviewQueueNavigation(string repoRoot)
        => Load<BuilderReviewQueueNavigationRecord>(ReviewQueueNavigationPathForRepo(repoRoot));

    public static BuilderHighRiskFileFlagsRecord LoadHighRiskFileFlags(string repoRoot)
        => Load(HighRiskFileFlagsPathForRepo(repoRoot), new BuilderHighRiskFileFlagsRecord(
            "review-session",
            Array.Empty<BuilderHighRiskFileFlagEntryRecord>(),
            "No high-risk review flags recorded.",
            HighRiskFileFlagsPathForRepo(repoRoot),
            DateTimeOffset.MinValue));

    public static BuilderBatchReviewActionsRecord LoadBatchReviewActions(string repoRoot)
        => Load(BatchReviewActionsPathForRepo(repoRoot), new BuilderBatchReviewActionsRecord(
            "review-session",
            Array.Empty<BuilderBatchReviewActionEntryRecord>(),
            "No builder batch review actions recorded.",
            BatchReviewActionsPathForRepo(repoRoot),
            DateTimeOffset.MinValue));

    public static BuilderReviewArtifactSet LoadArtifacts(string repoRoot)
        => new(
            Load<BuilderConversationExecutionSessionRecord>(ConversationExecutionSessionPathForRepo(repoRoot)),
            Load<BuilderPatchReviewRecord>(PatchReviewPathForRepo(repoRoot)),
            Load<BuilderPatchDiffReviewRecord>(PatchDiffReviewPathForRepo(repoRoot)),
            Load<BuilderFileReviewDecisionRecord>(FileReviewDecisionPathForRepo(repoRoot)),
            Load<BuilderPatchReviewOutcomeRecord>(PatchReviewOutcomePathForRepo(repoRoot)),
            Load<BuilderPatchApplyDecisionRecord>(PatchApplyDecisionPathForRepo(repoRoot)),
            PatchBundlePathForRepo(repoRoot));

    public static BuilderReviewWorkspaceContext? ApplyBatchReviewAction(
        string repoRoot,
        BuilderBatchReviewActionRequest request,
        DateTimeOffset? observedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentNullException(nameof(repoRoot));
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var sources = LoadArtifacts(repoRoot);
        if (sources.ExecutionSession is null &&
            sources.PatchReview is null &&
            sources.PatchDiffReview is null &&
            sources.FileReviewDecision is null)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var filter = NormalizeFilter(request.CurrentFilter);
        var grouping = NormalizeGrouping(request.CurrentGrouping);
        var requestedCurrentFile = NormalizeRelativePath(request.CurrentFilePath);
        var files = BuildFileRecords(repoRoot, sources, effectiveObservedUtc);
        var visibleFiles = files
            .Where(file => MatchesFilter(file, filter))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var currentFilePath = ResolveCurrentFilePath(requestedCurrentFile, visibleFiles);
        var allGroups = BuildGroups(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(), grouping);
        var currentGroupKey = ResolveCurrentGroupKey(allGroups, currentFilePath);
        var currentFile = files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase));

        var normalizedActionType = NormalizeBatchActionType(request.ActionType);
        var normalizedScopeType = NormalizeBatchScopeType(request.ScopeType, normalizedActionType);
        var scopeValue = ResolveBatchScopeValue(normalizedScopeType, request.ScopeValue, currentGroupKey, currentFile, filter);
        var scopedFiles = ResolveBatchScopedFiles(files, visibleFiles, allGroups, normalizedScopeType, scopeValue);
        var affectedFiles = ResolveBatchAffectedFiles(scopedFiles, normalizedActionType)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (affectedFiles.Length == 0)
        {
            return RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences(filter, grouping, currentFilePath),
                observedUtc: effectiveObservedUtc);
        }

        var batchActionsPath = BatchReviewActionsPathForRepo(repoRoot);
        var batchActions = AppendBatchReviewAction(
            NormalizeBatchReviewActions(repoRoot, ResolveExecutionSessionId(sources), LoadBatchReviewActions(repoRoot), effectiveObservedUtc),
            new BuilderBatchReviewActionEntryRecord(
                ResolveExecutionSessionId(sources),
                normalizedActionType,
                normalizedScopeType,
                scopeValue,
                affectedFiles,
                FirstNonEmpty(request.OperatorConfirmationState, "confirmed"),
                effectiveObservedUtc),
            effectiveObservedUtc,
            batchActionsPath);

        var updatedDecision = BuildUpdatedFileReviewDecision(
            repoRoot,
            sources,
            files,
            affectedFiles,
            normalizedActionType,
            normalizedScopeType,
            scopeValue,
            batchActionsPath,
            effectiveObservedUtc);
        Save(updatedDecision.ArtifactPath, updatedDecision);
        Save(batchActions.ArtifactPath, batchActions);

        var updatedSources = LoadArtifacts(repoRoot);
        var refreshedFiles = BuildFileRecords(repoRoot, updatedSources, effectiveObservedUtc);
        var refreshedCounts = BuildCounts(refreshedFiles);

        Save(
            PatchReviewOutcomePathForRepo(repoRoot),
            BuildUpdatedPatchReviewOutcome(repoRoot, updatedSources, refreshedCounts, effectiveObservedUtc));
        Save(
            PatchApplyDecisionPathForRepo(repoRoot),
            BuildUpdatedPatchApplyDecision(repoRoot, updatedSources, refreshedFiles, refreshedCounts, effectiveObservedUtc));

        return RefreshWorkspaceArtifacts(
            repoRoot,
            new BuilderReviewWorkspacePreferences(filter, grouping, currentFilePath),
            observedUtc: effectiveObservedUtc);
    }

    public static string NormalizeFilter(string? filter)
        => (filter ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => "all",
            "all files" => "all",
            "all" => "all",
            "pending" => "pending_only",
            "pending only" => "pending_only",
            "pending_only" => "pending_only",
            "approved" => "approved_only",
            "approved only" => "approved_only",
            "approved_only" => "approved_only",
            "rejected" => "rejected_only",
            "rejected only" => "rejected_only",
            "rejected_only" => "rejected_only",
            "needs revision" => "needs_revision_only",
            "needs_revision" => "needs_revision_only",
            "needs_revision_only" => "needs_revision_only",
            "created" => "created_only",
            "created only" => "created_only",
            "created_only" => "created_only",
            "modified" => "modified_only",
            "modified only" => "modified_only",
            "modified_only" => "modified_only",
            "deleted" => "deleted_only",
            "deleted only" => "deleted_only",
            "deleted_only" => "deleted_only",
            _ => "all"
        };

    public static string NormalizeGrouping(string? groupBy)
        => (groupBy ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => "directory",
            "directory" => "directory",
            "change type" => "change_type",
            "change_type" => "change_type",
            "category" => "file_category",
            "file category" => "file_category",
            "file_category" => "file_category",
            "state" => "review_state",
            "review state" => "review_state",
            "review_state" => "review_state",
            _ => "directory"
        };

    private static BuilderReviewWorkspaceFileRecord[] BuildFileRecords(
        string repoRoot,
        BuilderReviewArtifactSet sources,
        DateTimeOffset observedUtc)
    {
        var reviewFiles = sources.PatchReview?.ChangedFiles ?? Array.Empty<BuilderPatchReviewChangedFileRecord>();
        var reviewByPath = reviewFiles.ToDictionary(file => NormalizeRelativePath(file.Path), StringComparer.OrdinalIgnoreCase);
        var diffByPath = (sources.PatchDiffReview?.FileEntries ?? Array.Empty<BuilderPatchDiffReviewFileEntryRecord>())
            .ToDictionary(file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var decisionByPath = (sources.FileReviewDecision?.Entries ?? Array.Empty<BuilderFileReviewDecisionEntryRecord>())
            .ToDictionary(file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var sessionByPath = (sources.ExecutionSession?.ChangedFiles ?? Array.Empty<BuilderConversationChangedFileRecord>())
            .ToDictionary(file => NormalizeRelativePath(file.Path), StringComparer.OrdinalIgnoreCase);
        var bundleText = TryReadAllText(sources.PatchBundlePath);

        var allPaths = reviewByPath.Keys
            .Concat(diffByPath.Keys)
            .Concat(decisionByPath.Keys)
            .Concat(sessionByPath.Keys)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return allPaths.Select(path =>
        {
            reviewByPath.TryGetValue(path, out var review);
            diffByPath.TryGetValue(path, out var diff);
            decisionByPath.TryGetValue(path, out var decision);
            sessionByPath.TryGetValue(path, out var sessionFile);

            var changeKind = NormalizeChangeKind(FirstNonEmpty(review?.ChangeKind, diff?.ChangeKind, sessionFile?.ChangeKind));
            var fileCategory = NormalizeFileCategory(FirstNonEmpty(review?.FileCategory, diff?.FileCategory, sessionFile?.FileCategory, InferFileCategory(path)));
            var changeSummary = FirstNonEmpty(review?.ChangeSummary, sessionFile?.ChangeSummary, diff?.DiffSummary, $"{Path.GetFileName(path)} was {changeKind}.");
            var diffSummary = FirstNonEmpty(diff?.DiffSummary, changeSummary);
            var approvalState = NormalizeApprovalState(FirstNonEmpty(decision?.ApprovalState, diff?.ApprovalState));
            var rejectionReason = FirstNonEmpty(decision?.RejectionReason, diff?.RejectionReason);
            var filePurpose = DetermineFilePurpose(fileCategory, path);
            var projectArea = DetermineProjectArea(path);
            var featureArea = DetermineFeatureArea(path, projectArea);
            var relatedTestFilePath = FindRelatedTestFile(repoRoot, path);
            var highRiskCategory = DetermineHighRiskCategory(path, fileCategory);
            var requiresExplicitApproval = !string.IsNullOrWhiteSpace(highRiskCategory);
            var priorityRank = DeterminePriorityRank(approvalState, requiresExplicitApproval);
            var priorityLabel = DeterminePriorityLabel(priorityRank, approvalState, highRiskCategory);
            var linkedArtifactPaths = new[]
            {
                sources.ExecutionSession?.ArtifactPath ?? string.Empty,
                sources.PatchReview?.ArtifactPath ?? string.Empty,
                sources.PatchDiffReview?.ArtifactPath ?? string.Empty,
                sources.FileReviewDecision?.ArtifactPath ?? string.Empty,
                sources.PatchReviewOutcome?.ArtifactPath ?? string.Empty,
                sources.PatchApplyDecision?.ArtifactPath ?? string.Empty,
                sources.PatchBundlePath
            }
            .Where(pathValue => !string.IsNullOrWhiteSpace(pathValue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pathValue => pathValue, StringComparer.Ordinal)
            .ToArray();

            return new BuilderReviewWorkspaceFileRecord(
                path,
                NormalizeDirectoryPath(Path.GetDirectoryName(path)),
                $"{path} ({changeKind})",
                fileCategory,
                filePurpose,
                projectArea,
                featureArea,
                changeKind,
                changeSummary,
                diffSummary,
                BuildBoundedExcerpt(FirstNonEmpty(diff?.PatchPreviewText, ExtractPatchExcerpt(bundleText, path), diffSummary)),
                approvalState,
                rejectionReason,
                relatedTestFilePath,
                BuildContextSummary(filePurpose, projectArea, featureArea, changeKind, relatedTestFilePath),
                highRiskCategory,
                requiresExplicitApproval,
                priorityRank,
                priorityLabel,
                linkedArtifactPaths,
                MaxDate(
                    sources.ExecutionSession?.ObservedUtc ?? DateTimeOffset.MinValue,
                    sources.PatchReview?.ObservedUtc ?? DateTimeOffset.MinValue,
                    diff?.ObservedUtc ?? DateTimeOffset.MinValue,
                    decision?.ObservedUtc ?? DateTimeOffset.MinValue,
                    observedUtc));
        }).ToArray();
    }

    private static BuilderReviewWorkspaceCounts BuildCounts(IReadOnlyList<BuilderReviewWorkspaceFileRecord> files)
    {
        var total = files.Count;
        var approved = files.Count(file => IsApprovedState(file.ApprovalState));
        var rejected = files.Count(file => IsRejectedState(file.ApprovalState));
        var needsRevision = files.Count(file => IsNeedsRevisionState(file.ApprovalState));
        var pending = total - approved - rejected - needsRevision;
        var finalizeEligibilityState = rejected > 0
            ? "blocked_by_rejection"
            : needsRevision > 0
                ? "blocked_by_revision_request"
                : pending == total && total > 0
                    ? "pending_review"
                    : pending > 0
                        ? "partially_approved"
                        : total > 0
                            ? "ready_to_finalize"
                            : "no_changed_files";

        return new BuilderReviewWorkspaceCounts(total, pending, approved, rejected, needsRevision, finalizeEligibilityState);
    }

    private static BuilderReviewWorkspaceGroupRecord[] BuildGroups(
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        string grouping)
        => files
            .GroupBy(file => ResolveGroupKey(grouping, file), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => ResolveGroupSortKey(grouping, group.Key), StringComparer.Ordinal)
            .Select(group =>
            {
                var groupFiles = group.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
                return new BuilderReviewWorkspaceGroupRecord(
                    grouping,
                    group.Key,
                    ResolveGroupLabel(grouping, group.Key),
                    groupFiles.Length,
                    groupFiles.Count(file => IsPendingState(file.ApprovalState)),
                    groupFiles.Count(file => IsApprovedState(file.ApprovalState)),
                    groupFiles.Count(file => IsRejectedState(file.ApprovalState)),
                    groupFiles.Count(file => IsNeedsRevisionState(file.ApprovalState)),
                    groupFiles.Count(file => file.RequiresExplicitApproval),
                    groupFiles);
            })
            .ToArray();

    private static BuilderReviewEfficiencySummaryRecord BuildEfficiencySummary(
        string repoRoot,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        BuilderReviewWorkspaceCounts counts,
        DateTimeOffset observedUtc)
    {
        var reviewed = counts.ApprovedFiles + counts.RejectedFiles + counts.NeedsRevisionFiles;
        var completion = counts.TotalChangedFiles == 0
            ? 0d
            : Math.Round(reviewed * 100d / counts.TotalChangedFiles, 2, MidpointRounding.AwayFromZero);
        return new BuilderReviewEfficiencySummaryRecord(
            counts.TotalChangedFiles,
            reviewed,
            FindFirstFile(files, file => IsPendingState(file.ApprovalState)),
            FindFirstFile(files, file => IsRejectedState(file.ApprovalState)),
            completion,
            counts.FinalizeEligibilityState,
            $"Reviewed {reviewed} of {counts.TotalChangedFiles} changed file(s). Finalize state={counts.FinalizeEligibilityState}.",
            ReviewEfficiencySummaryPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderReviewWorkspaceHistoryRecord BuildHistory(
        BuilderReviewWorkspaceHistoryRecord existing,
        BuilderReviewWorkspaceRecord workspace,
        BuilderReviewNavigationStateRecord navigation,
        int historyRetentionCount,
        DateTimeOffset observedUtc,
        string repoRoot)
    {
        var priorFilters = existing.Entries
            .Where(entry => string.Equals(entry.SessionId, workspace.ExecutionSessionId, StringComparison.Ordinal))
            .SelectMany(entry => entry.FiltersUsed)
            .Append(navigation.CurrentFilter)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(filter => filter, StringComparer.Ordinal)
            .ToArray();
        var current = new BuilderReviewWorkspaceHistoryEntryRecord(
            workspace.ExecutionSessionId,
            workspace.GroupingUsed,
            priorFilters,
            workspace.ReviewCounts,
            workspace.ReviewCounts.FinalizeEligibilityState,
            observedUtc);
        var entries = existing.Entries
            .Append(current)
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .Take(Math.Max(1, historyRetentionCount))
            .ToArray();
        var summary = entries.Length == 0
            ? "No builder review workspace history recorded."
            : $"Latest review workspace session {entries[0].SessionId} is {entries[0].FinalReviewState} with grouping {entries[0].GroupingUsed}.";

        return new BuilderReviewWorkspaceHistoryRecord(
            Math.Max(existing.RetentionCount, Math.Max(1, historyRetentionCount)),
            entries,
            summary,
            ReviewWorkspaceHistoryPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderHighRiskFileFlagsRecord BuildHighRiskFlags(
        string repoRoot,
        string sessionId,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        DateTimeOffset observedUtc)
    {
        var entries = files
            .Where(file => file.RequiresExplicitApproval)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => new BuilderHighRiskFileFlagEntryRecord(
                file.RelativePath,
                file.HighRiskCategory,
                file.RequiresExplicitApproval,
                observedUtc))
            .ToArray();
        var summary = entries.Length == 0
            ? "No high-risk review flags recorded."
            : $"High-risk review flags cover {entries.Length} file(s).";

        return new BuilderHighRiskFileFlagsRecord(
            sessionId,
            entries,
            summary,
            HighRiskFileFlagsPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderReviewQueueRecord BuildReviewQueue(
        string repoRoot,
        BuilderReviewArtifactSet sources,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        BuilderHighRiskFileFlagsRecord highRiskFlags,
        DateTimeOffset observedUtc)
    {
        var orderedFiles = files
            .OrderBy(file => file.PriorityRank)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var pendingFiles = orderedFiles
            .Where(file => IsPendingState(file.ApprovalState))
            .Select(file => file.RelativePath)
            .ToArray();
        var rejectedFiles = orderedFiles
            .Where(file => IsRejectedState(file.ApprovalState))
            .Select(file => file.RelativePath)
            .ToArray();
        var revisionFiles = orderedFiles
            .Where(file => IsNeedsRevisionState(file.ApprovalState))
            .Select(file => file.RelativePath)
            .ToArray();
        var approvedFiles = orderedFiles
            .Where(file => IsApprovedState(file.ApprovalState))
            .Select(file => file.RelativePath)
            .ToArray();
        var priorityOrdering = orderedFiles
            .Select(file => new BuilderReviewQueuePriorityEntryRecord(
                file.RelativePath,
                file.PriorityRank,
                file.PriorityLabel,
                file.ApprovalState,
                file.HighRiskCategory,
                file.RequiresExplicitApproval))
            .ToArray();
        var summary = orderedFiles.Length == 0
            ? "No builder review queue recorded."
            : $"Queue tracks {orderedFiles.Length} file(s). Remaining={pendingFiles.Length + rejectedFiles.Length + revisionFiles.Length}. High-risk={highRiskFlags.Entries.Count}.";

        return new BuilderReviewQueueRecord(
            ResolveExecutionSessionId(sources),
            ResolvePatchReviewId(sources),
            orderedFiles.Select(file => file.RelativePath).ToArray(),
            pendingFiles,
            rejectedFiles,
            revisionFiles,
            approvedFiles,
            highRiskFlags.Entries,
            priorityOrdering,
            summary,
            ReviewQueuePathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderReviewQueueNavigationRecord BuildQueueNavigation(
        string repoRoot,
        BuilderReviewQueueRecord queue,
        string currentFilePath,
        DateTimeOffset observedUtc)
    {
        var current = queue.QueueOrder.Contains(currentFilePath, StringComparer.OrdinalIgnoreCase)
            ? currentFilePath
            : queue.QueueOrder.FirstOrDefault() ?? string.Empty;
        var priorityEntries = queue.PriorityOrdering.ToArray();
        var nextPriority = priorityEntries.FirstOrDefault()?.RelativePath ?? string.Empty;

        return new BuilderReviewQueueNavigationRecord(
            current,
            nextPriority,
            FindNextQueueFile(queue.QueueOrder, current, path => queue.PendingFiles.Contains(path, StringComparer.OrdinalIgnoreCase)),
            FindNextQueueFile(queue.QueueOrder, current, path => queue.RejectedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)),
            FindNextQueueFile(queue.QueueOrder, current, path => queue.RevisionFiles.Contains(path, StringComparer.OrdinalIgnoreCase)),
            FindNextQueueFile(queue.QueueOrder, current, path => queue.HighRiskFiles.Any(flag => string.Equals(flag.FilePath, path, StringComparison.OrdinalIgnoreCase))),
            ReviewQueueNavigationPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderBatchReviewActionsRecord NormalizeBatchReviewActions(
        string repoRoot,
        string sessionId,
        BuilderBatchReviewActionsRecord existing,
        DateTimeOffset observedUtc)
    {
        if (!string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new BuilderBatchReviewActionsRecord(
                sessionId,
                Array.Empty<BuilderBatchReviewActionEntryRecord>(),
                "No builder batch review actions recorded.",
                BatchReviewActionsPathForRepo(repoRoot),
                observedUtc);
        }

        var entries = existing.Entries
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.ActionType, StringComparer.Ordinal)
            .Take(DefaultBatchActionRetentionCount)
            .ToArray();
        var summary = entries.Length == 0
            ? "No builder batch review actions recorded."
            : $"Latest batch action {entries[0].ActionType} touched {entries[0].AffectedFiles.Count} file(s) in {entries[0].ScopeType}:{entries[0].ScopeValue}.";

        return new BuilderBatchReviewActionsRecord(
            sessionId,
            entries,
            summary,
            BatchReviewActionsPathForRepo(repoRoot),
            entries.FirstOrDefault()?.ObservedUtc ?? observedUtc);
    }

    private static BuilderBatchReviewActionsRecord AppendBatchReviewAction(
        BuilderBatchReviewActionsRecord existing,
        BuilderBatchReviewActionEntryRecord entry,
        DateTimeOffset observedUtc,
        string artifactPath)
    {
        var entries = existing.Entries
            .Append(entry)
            .OrderByDescending(action => action.ObservedUtc)
            .ThenBy(action => action.ActionType, StringComparer.Ordinal)
            .Take(DefaultBatchActionRetentionCount)
            .ToArray();
        return new BuilderBatchReviewActionsRecord(
            existing.SessionId,
            entries,
            $"Latest batch action {entry.ActionType} touched {entry.AffectedFiles.Count} file(s) in {entry.ScopeType}:{entry.ScopeValue}.",
            artifactPath,
            observedUtc);
    }

    private static BuilderFileReviewDecisionRecord BuildUpdatedFileReviewDecision(
        string repoRoot,
        BuilderReviewArtifactSet sources,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        IReadOnlyList<string> affectedFiles,
        string actionType,
        string scopeType,
        string scopeValue,
        string batchActionsPath,
        DateTimeOffset observedUtc)
    {
        var state = ResolveBatchActionState(actionType);
        var rejectionReason = ResolveBatchActionRejectionReason(actionType, scopeType, scopeValue);
        var affected = new HashSet<string>(affectedFiles.Select(NormalizeRelativePath), StringComparer.OrdinalIgnoreCase);
        var existingByPath = (sources.FileReviewDecision?.Entries ?? Array.Empty<BuilderFileReviewDecisionEntryRecord>())
            .ToDictionary(entry => NormalizeRelativePath(entry.RelativePath), StringComparer.OrdinalIgnoreCase);

        var entries = files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file =>
            {
                existingByPath.TryGetValue(file.RelativePath, out var existing);
                var linkedArtifacts = (existing?.LinkedArtifactPaths ?? Array.Empty<string>())
                    .Append(batchActionsPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                if (!affected.Contains(file.RelativePath))
                {
                    return new BuilderFileReviewDecisionEntryRecord(
                        file.RelativePath,
                        NormalizeApprovalState(existing?.ApprovalState ?? file.ApprovalState),
                        existing?.OperatorDecisionSource ?? "operator",
                        existing?.RejectionReason ?? file.RejectionReason,
                        linkedArtifacts,
                        existing?.ObservedUtc ?? file.ObservedUtc);
                }

                return new BuilderFileReviewDecisionEntryRecord(
                    file.RelativePath,
                    state,
                    "operator_batch",
                    rejectionReason,
                    linkedArtifacts,
                    observedUtc);
            })
            .ToArray();
        var rebuiltFiles = files
            .Select(file => file with
            {
                ApprovalState = entries.First(entry => string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)).ApprovalState,
                RejectionReason = entries.First(entry => string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)).RejectionReason
            })
            .ToArray();
        var counts = BuildCounts(rebuiltFiles);

        return new BuilderFileReviewDecisionRecord(
            ResolveExecutionSessionId(sources),
            sources.FileReviewDecision?.SourcePatchDiffReviewId ?? ResolvePatchDiffReviewId(sources),
            BuildOverallFileReviewState(counts),
            entries,
            BuildLinkedArtifactPaths(repoRoot, sources)
                .Append(batchActionsPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            BuildFileDecisionSummary(counts, actionType, affectedFiles.Count),
            FileReviewDecisionPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderPatchReviewOutcomeRecord BuildUpdatedPatchReviewOutcome(
        string repoRoot,
        BuilderReviewArtifactSet sources,
        BuilderReviewWorkspaceCounts counts,
        DateTimeOffset observedUtc)
    {
        var sessionState = counts.FinalizeEligibilityState switch
        {
            "blocked_by_rejection" => "awaiting_revision",
            "blocked_by_revision_request" => "awaiting_revision",
            "ready_to_finalize" => "ready_to_finalize",
            "no_changed_files" => "ready_to_finalize",
            _ => "awaiting_review"
        };
        var reviewState = counts.FinalizeEligibilityState switch
        {
            "blocked_by_rejection" => "blocked",
            "blocked_by_revision_request" => "blocked",
            "ready_to_finalize" => "approved",
            "no_changed_files" => "approved",
            _ => "pending_review"
        };

        return new BuilderPatchReviewOutcomeRecord(
            ResolveExecutionSessionId(sources),
            BuildOverallFileReviewState(counts),
            sessionState,
            reviewState,
            BuildPatchReviewOutcomeNote(counts),
            sources.PatchReviewOutcome?.RerouteRoute ?? string.Empty,
            BuildLinkedArtifactPaths(repoRoot, sources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            $"Patch review outcome is {reviewState} with finalize state {counts.FinalizeEligibilityState}.",
            PatchReviewOutcomePathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderPatchApplyDecisionRecord BuildUpdatedPatchApplyDecision(
        string repoRoot,
        BuilderReviewArtifactSet sources,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        BuilderReviewWorkspaceCounts counts,
        DateTimeOffset observedUtc)
    {
        return new BuilderPatchApplyDecisionRecord(
            ResolveExecutionSessionId(sources),
            BuildOverallFileReviewState(counts),
            counts.FinalizeEligibilityState,
            BuildPatchApplyBlockReasons(files, counts),
            counts.FinalizeEligibilityState,
            BuildLinkedArtifactPaths(repoRoot, sources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            BuildPatchApplySummary(counts),
            PatchApplyDecisionPathForRepo(repoRoot),
            observedUtc);
    }

    private static string BuildWorkspaceSummary(BuilderReviewWorkspaceCounts counts, string filter, string grouping)
        => $"Review workspace tracks {counts.TotalChangedFiles} changed file(s). Filter={filter}. Grouping={grouping}. Finalize state={counts.FinalizeEligibilityState}.";

    private static string ResolveExecutionSessionId(BuilderReviewArtifactSet sources)
        => FirstNonEmpty(
            sources.ExecutionSession?.SessionId,
            sources.PatchReview?.SessionId,
            sources.PatchDiffReview?.SessionId,
            sources.FileReviewDecision?.SessionId,
            "review-session");

    private static string ResolvePatchReviewId(BuilderReviewArtifactSet sources)
    {
        var seed = FirstNonEmpty(
            sources.PatchReview?.SessionId,
            sources.PatchReview?.ArtifactPath,
            sources.ExecutionSession?.PatchReviewPath,
            sources.ExecutionSession?.SessionId);
        return string.IsNullOrWhiteSpace(seed)
            ? "patch-review"
            : $"patch-review-{SanitizeIdToken(seed)}";
    }

    private static string ResolvePatchDiffReviewId(BuilderReviewArtifactSet sources)
    {
        var seed = FirstNonEmpty(
            sources.PatchDiffReview?.SourcePatchReviewId,
            sources.PatchDiffReview?.SessionId,
            sources.PatchDiffReview?.ArtifactPath,
            sources.ExecutionSession?.SessionId);
        return string.IsNullOrWhiteSpace(seed)
            ? "patch-diff-review"
            : $"patch-diff-review-{SanitizeIdToken(seed)}";
    }

    private static string[] BuildLinkedArtifactPaths(string repoRoot, BuilderReviewArtifactSet sources)
        => new[]
        {
            ConversationExecutionSessionPathForRepo(repoRoot),
            PatchReviewPathForRepo(repoRoot),
            PatchDiffReviewPathForRepo(repoRoot),
            FileReviewDecisionPathForRepo(repoRoot),
            PatchReviewOutcomePathForRepo(repoRoot),
            PatchApplyDecisionPathForRepo(repoRoot),
            PatchBundlePathForRepo(repoRoot)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        .Concat(new[]
        {
            ReviewWorkspacePathForRepo(repoRoot),
            ReviewNavigationStatePathForRepo(repoRoot),
            ReviewEfficiencySummaryPathForRepo(repoRoot),
            ReviewWorkspaceHistoryPathForRepo(repoRoot),
            ReviewQueuePathForRepo(repoRoot),
            ReviewQueueNavigationPathForRepo(repoRoot),
            HighRiskFileFlagsPathForRepo(repoRoot),
            BatchReviewActionsPathForRepo(repoRoot)
        }.Where(path => !string.IsNullOrWhiteSpace(path)))
        .Concat((sources.ExecutionSession?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(sources.PatchReview?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(sources.PatchDiffReview?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(sources.FileReviewDecision?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(sources.PatchReviewOutcome?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(sources.PatchApplyDecision?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static string ResolveCurrentFilePath(string preferredPath, IReadOnlyList<BuilderReviewWorkspaceFileRecord> visibleFiles)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var matched = visibleFiles.FirstOrDefault(file => string.Equals(file.RelativePath, preferredPath, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched.RelativePath;
        }

        var pending = FindFirstFile(visibleFiles, file => IsPendingState(file.ApprovalState));
        if (!string.IsNullOrWhiteSpace(pending))
            return pending;

        var rejected = FindFirstFile(visibleFiles, file => IsRejectedState(file.ApprovalState));
        if (!string.IsNullOrWhiteSpace(rejected))
            return rejected;

        return visibleFiles.FirstOrDefault()?.RelativePath ?? string.Empty;
    }

    private static string ResolveCurrentGroupKey(
        IReadOnlyList<BuilderReviewWorkspaceGroupRecord> groups,
        string currentFilePath)
    {
        var matchedGroup = groups.FirstOrDefault(group =>
            group.Files.Any(file => string.Equals(file.RelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase)));
        return matchedGroup?.GroupKey ?? groups.FirstOrDefault()?.GroupKey ?? string.Empty;
    }

    private static bool MatchesFilter(BuilderReviewWorkspaceFileRecord file, string filter)
        => filter switch
        {
            "pending_only" => IsPendingState(file.ApprovalState),
            "approved_only" => IsApprovedState(file.ApprovalState),
            "rejected_only" => IsRejectedState(file.ApprovalState),
            "needs_revision_only" => IsNeedsRevisionState(file.ApprovalState),
            "created_only" => string.Equals(file.ChangeKind, "created", StringComparison.Ordinal),
            "modified_only" => string.Equals(file.ChangeKind, "modified", StringComparison.Ordinal),
            "deleted_only" => string.Equals(file.ChangeKind, "deleted", StringComparison.Ordinal),
            _ => true
        };

    private static string ResolveGroupKey(string grouping, BuilderReviewWorkspaceFileRecord file)
        => grouping switch
        {
            "change_type" => file.ChangeKind,
            "file_category" => file.FileCategory,
            "review_state" => file.ApprovalState,
            _ => string.IsNullOrWhiteSpace(file.DirectoryPath) ? "." : file.DirectoryPath
        };

    private static string ResolveGroupLabel(string grouping, string groupKey)
        => grouping switch
        {
            "change_type" => groupKey switch
            {
                "created" => "Created files",
                "modified" => "Modified files",
                "deleted" => "Deleted files",
                _ => $"Change type: {groupKey}"
            },
            "file_category" => $"{FormatLabel(groupKey)} files",
            "review_state" => $"{FormatApprovalState(groupKey)} files",
            _ => string.IsNullOrWhiteSpace(groupKey) || string.Equals(groupKey, ".", StringComparison.Ordinal) ? "Repo root" : groupKey
        };

    private static string ResolveGroupSortKey(string grouping, string groupKey)
        => grouping switch
        {
            "change_type" => groupKey switch
            {
                "created" => "01",
                "modified" => "02",
                "deleted" => "03",
                _ => $"99-{groupKey}"
            },
            "review_state" => groupKey switch
            {
                "rejected" => "01",
                "needs_revision" => "02",
                "pending_review" => "03",
                "approved" => "04",
                _ => $"99-{groupKey}"
            },
            _ => groupKey
        };

    private static string FindNextQueueFile(
        IReadOnlyList<string> queueOrder,
        string currentFilePath,
        Func<string, bool> predicate)
    {
        if (queueOrder.Count == 0)
            return string.Empty;

        var matching = queueOrder.Where(predicate).ToArray();
        if (matching.Length == 0)
            return string.Empty;

        var currentIndex = Array.FindIndex(queueOrder.ToArray(), path =>
            string.Equals(path, currentFilePath, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
            return matching[0];

        for (var offset = 1; offset <= queueOrder.Count; offset++)
        {
            var candidate = queueOrder[(currentIndex + offset) % queueOrder.Count];
            if (predicate(candidate))
                return candidate;
        }

        return matching[0];
    }

    private static string NormalizeBatchActionType(string? actionType)
        => (actionType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "approve_all_pending_in_group" => "approve_pending_in_group",
            "approve_pending_in_group" => "approve_pending_in_group",
            "approve group" => "approve_pending_in_group",
            "approve_all_pending_in_directory" => "approve_pending_in_directory",
            "approve_pending_in_directory" => "approve_pending_in_directory",
            "approve directory" => "approve_pending_in_directory",
            "approve_all_pending_in_filter" => "approve_pending_in_filter",
            "approve_pending_in_filter" => "approve_pending_in_filter",
            "approve filter" => "approve_pending_in_filter",
            "reject_all_in_group" => "reject_all_in_group",
            "reject_group" => "reject_all_in_group",
            "reject all in group" => "reject_all_in_group",
            "mark_group_needs_revision" => "mark_group_needs_revision",
            "needs_revision_group" => "mark_group_needs_revision",
            "mark group as needs revision" => "mark_group_needs_revision",
            _ => "approve_pending_in_filter"
        };

    private static string NormalizeBatchScopeType(string? scopeType, string actionType)
    {
        var normalized = (scopeType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "group" => "group",
            "directory" => "directory",
            "filter" => "filter",
            _ => actionType switch
            {
                "approve_pending_in_group" => "group",
                "approve_pending_in_directory" => "directory",
                "approve_pending_in_filter" => "filter",
                "reject_all_in_group" => "group",
                "mark_group_needs_revision" => "group",
                _ => "filter"
            }
        };
    }

    private static string ResolveBatchScopeValue(
        string scopeType,
        string requestedScopeValue,
        string currentGroupKey,
        BuilderReviewWorkspaceFileRecord? currentFile,
        string currentFilter)
    {
        var normalizedRequested = NormalizeRelativePath(requestedScopeValue);
        return scopeType switch
        {
            "group" => FirstNonEmpty(normalizedRequested, currentGroupKey),
            "directory" => FirstNonEmpty(normalizedRequested, currentFile?.DirectoryPath, "."),
            "filter" => FirstNonEmpty(requestedScopeValue, currentFilter),
            _ => FirstNonEmpty(normalizedRequested, currentFilter)
        };
    }

    private static BuilderReviewWorkspaceFileRecord[] ResolveBatchScopedFiles(
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> allFiles,
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> visibleFiles,
        IReadOnlyList<BuilderReviewWorkspaceGroupRecord> allGroups,
        string scopeType,
        string scopeValue)
        => scopeType switch
        {
            "group" => allGroups
                .FirstOrDefault(group => string.Equals(group.GroupKey, scopeValue, StringComparison.OrdinalIgnoreCase))
                ?.Files
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<BuilderReviewWorkspaceFileRecord>(),
            "directory" => allFiles
                .Where(file => string.Equals(file.DirectoryPath, scopeValue, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            "filter" => visibleFiles
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            _ => Array.Empty<BuilderReviewWorkspaceFileRecord>()
        };

    private static string[] ResolveBatchAffectedFiles(
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> scopedFiles,
        string actionType)
        => actionType switch
        {
            "approve_pending_in_group" => scopedFiles
                .Where(file => IsPendingState(file.ApprovalState) && !file.RequiresExplicitApproval)
                .Select(file => file.RelativePath)
                .ToArray(),
            "approve_pending_in_directory" => scopedFiles
                .Where(file => IsPendingState(file.ApprovalState) && !file.RequiresExplicitApproval)
                .Select(file => file.RelativePath)
                .ToArray(),
            "approve_pending_in_filter" => scopedFiles
                .Where(file => IsPendingState(file.ApprovalState) && !file.RequiresExplicitApproval)
                .Select(file => file.RelativePath)
                .ToArray(),
            "reject_all_in_group" => scopedFiles.Select(file => file.RelativePath).ToArray(),
            "mark_group_needs_revision" => scopedFiles.Select(file => file.RelativePath).ToArray(),
            _ => Array.Empty<string>()
        };

    private static string ResolveBatchActionState(string actionType)
        => actionType switch
        {
            "reject_all_in_group" => "rejected",
            "mark_group_needs_revision" => "needs_revision",
            _ => "approved"
        };

    private static string ResolveBatchActionRejectionReason(string actionType, string scopeType, string scopeValue)
        => actionType switch
        {
            "reject_all_in_group" => $"Batch rejected via {scopeType} scope {scopeValue}.",
            "mark_group_needs_revision" => $"Batch marked for revision via {scopeType} scope {scopeValue}.",
            _ => string.Empty
        };

    private static string BuildOverallFileReviewState(BuilderReviewWorkspaceCounts counts)
        => counts.FinalizeEligibilityState switch
        {
            "blocked_by_rejection" => "rejected_file_present",
            "blocked_by_revision_request" => "revision_requested",
            "ready_to_finalize" => "approved",
            "no_changed_files" => "no_changed_files",
            "partially_approved" => "partially_approved",
            _ => "pending_review"
        };

    private static string BuildFileDecisionSummary(
        BuilderReviewWorkspaceCounts counts,
        string actionType,
        int affectedCount)
        => $"File review decisions reflect {actionType} for {affectedCount} file(s). Finalize state={counts.FinalizeEligibilityState}.";

    private static string BuildPatchReviewOutcomeNote(BuilderReviewWorkspaceCounts counts)
        => counts.FinalizeEligibilityState switch
        {
            "blocked_by_rejection" => "Rejected files still block finalization.",
            "blocked_by_revision_request" => "Files marked for revision still block finalization.",
            "ready_to_finalize" => "All reviewed files are approved and ready for finalization.",
            "partially_approved" => "Review is partially complete and still needs operator approval.",
            "pending_review" => "Review is still pending.",
            _ => "No changed files remain in the current review workspace."
        };

    private static string[] BuildPatchApplyBlockReasons(
        IReadOnlyList<BuilderReviewWorkspaceFileRecord> files,
        BuilderReviewWorkspaceCounts counts)
    {
        if (counts.RejectedFiles > 0)
        {
            return files
                .Where(file => IsRejectedState(file.ApprovalState))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => $"Rejected file {file.RelativePath} must be resolved before finalize.")
                .ToArray();
        }

        if (counts.NeedsRevisionFiles > 0)
        {
            return files
                .Where(file => IsNeedsRevisionState(file.ApprovalState))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => $"Needs revision file {file.RelativePath} must be updated before finalize.")
                .ToArray();
        }

        if (counts.PendingFiles > 0)
        {
            return files
                .Where(file => IsPendingState(file.ApprovalState))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => file.RequiresExplicitApproval
                    ? $"High-risk file {file.RelativePath} still requires explicit approval before finalize."
                    : $"Pending review file {file.RelativePath} must be approved before finalize.")
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static string BuildPatchApplySummary(BuilderReviewWorkspaceCounts counts)
        => counts.FinalizeEligibilityState switch
        {
            "blocked_by_rejection" => $"Finalize is blocked while {counts.RejectedFiles} rejected file(s) remain.",
            "blocked_by_revision_request" => $"Finalize is blocked while {counts.NeedsRevisionFiles} file(s) need revision.",
            "partially_approved" => $"Finalize is blocked while {counts.PendingFiles} file(s) still need approval.",
            "pending_review" => "Finalize is blocked until review begins.",
            "ready_to_finalize" => "Finalize is ready because every changed file is approved.",
            _ => "Finalize is not applicable because no changed files were recorded."
        };

    private static string DetermineHighRiskCategory(string relativePath, string fileCategory)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalized);
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "nuget.config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase))
        {
            return "build_system";
        }

        if (string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "package-lock.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "yarn.lock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "paket.dependencies", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "paket.lock", StringComparison.OrdinalIgnoreCase))
        {
            return "dependency_manifest";
        }

        if (string.Equals(fileName, "xunit.runner.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase))
        {
            return "test_harness_configuration";
        }

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "launchSettings.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileCategory, "config_json", StringComparison.OrdinalIgnoreCase) &&
            normalized.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "project_configuration";
        }

        if (string.Equals(fileName, "Program.cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "App.xaml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "App.xaml.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.IndexOf("\\Runtime\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("\\Hosting\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("\\Host\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "runtime_core";
        }

        return string.Empty;
    }

    private static int DeterminePriorityRank(string approvalState, bool requiresExplicitApproval)
    {
        if (IsRejectedState(approvalState))
            return 1;
        if (IsNeedsRevisionState(approvalState))
            return 2;
        if (requiresExplicitApproval)
            return 3;
        if (IsPendingState(approvalState))
            return 4;

        return 5;
    }

    private static string DeterminePriorityLabel(int priorityRank, string approvalState, string highRiskCategory)
        => priorityRank switch
        {
            1 => "Rejected blocker",
            2 => "Revision blocker",
            3 => string.IsNullOrWhiteSpace(highRiskCategory)
                ? "High priority"
                : $"High risk: {FormatLabel(highRiskCategory)}",
            4 => "Pending review",
            _ => IsApprovedState(approvalState) ? "Approved" : "Queued"
        };

    private static string DetermineProjectArea(string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath)
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "Unknown";

        var projectSegment = segments.FirstOrDefault(segment => segment.StartsWith("Shoots.", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(projectSegment))
            return projectSegment;

        if (segments.Length >= 2 &&
            (string.Equals(segments[0], "ui", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "tools", StringComparison.OrdinalIgnoreCase)))
        {
            return segments[1];
        }

        return segments[0];
    }

    private static string DetermineFeatureArea(string relativePath, string projectArea)
    {
        var directory = NormalizeDirectoryPath(Path.GetDirectoryName(NormalizeRelativePath(relativePath)));
        if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, ".", StringComparison.Ordinal))
            return projectArea;

        var projectToken = projectArea.Replace('/', '\\');
        var index = directory.IndexOf(projectToken, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var feature = directory[(index + projectToken.Length)..].Trim('\\');
            return string.IsNullOrWhiteSpace(feature) ? projectArea : feature;
        }

        return directory;
    }

    private static string DetermineFilePurpose(string fileCategory, string relativePath)
        => fileCategory switch
        {
            "ui_markup" => "UI markup and bindings",
            "view_model" => "UI state and command orchestration",
            "test_code" => "Automated test coverage",
            "build_config" => "Build, SDK, or project orchestration",
            "dependency_manifest" => "Dependency manifest and package resolution",
            "test_harness_config" => "Test harness and validation configuration",
            "config_json" => "Structured configuration or artifact data",
            "docs" => "Documentation or operator guidance",
            _ when relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => "Build, SDK, or project orchestration",
            _ when relativePath.EndsWith(".props", StringComparison.OrdinalIgnoreCase) => "Build, SDK, or project orchestration",
            _ when relativePath.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) => "Build, SDK, or project orchestration",
            _ when string.Equals(Path.GetFileName(relativePath), "package.json", StringComparison.OrdinalIgnoreCase) => "Dependency manifest and package resolution",
            _ when relativePath.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase) => "Test harness and validation configuration",
            _ when relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) => "UI markup and bindings",
            _ when relativePath.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase) => "UI state and command orchestration",
            _ when relativePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) => "Automated test coverage",
            _ => "Source implementation"
        };

    private static string BuildContextSummary(
        string filePurpose,
        string projectArea,
        string featureArea,
        string changeKind,
        string relatedTestPath)
    {
        var builder = new StringBuilder();
        builder.Append(filePurpose);
        builder.Append(". Project area: ");
        builder.Append(projectArea);
        builder.Append(". Feature area: ");
        builder.Append(string.IsNullOrWhiteSpace(featureArea) ? projectArea : featureArea);
        builder.Append(". Change type: ");
        builder.Append(changeKind);
        if (!string.IsNullOrWhiteSpace(relatedTestPath))
        {
            builder.Append(". Related test: ");
            builder.Append(relatedTestPath);
        }

        return builder.ToString();
    }

    private static string FindRelatedTestFile(string repoRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return string.Empty;

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0)
            return normalized;

        var fileName = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var searchRoots = Directory.EnumerateDirectories(repoRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var candidatePatterns = new[]
        {
            $"{fileName}Tests.cs",
            $"{fileName}*Tests.cs",
            $"*{fileName}*Tests.cs"
        };

        foreach (var root in searchRoots)
        {
            foreach (var pattern in candidatePatterns)
            {
                var match = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return NormalizeRelativePath(Path.GetRelativePath(repoRoot, match));
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractPatchExcerpt(string? bundleText, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(bundleText) || string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var target = NormalizeRelativePath(relativePath).Replace('\\', '/');
        var lines = bundleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if ((lines[i].StartsWith("--- a/", StringComparison.Ordinal) || lines[i].StartsWith("+++ b/", StringComparison.Ordinal)) &&
                lines[i].IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                start = lines[i].StartsWith("--- a/", StringComparison.Ordinal) ? i : Math.Max(0, i - 1);
                break;
            }
        }

        if (start < 0)
            return string.Empty;

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("--- a/", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        return BuildBoundedExcerpt(string.Join(System.Environment.NewLine, lines.Skip(start).Take(end - start)));
    }

    private static string BuildBoundedExcerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "No bounded diff excerpt recorded.";

        var normalizedLines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxExcerptLines)
            .ToArray();
        var excerpt = string.Join(System.Environment.NewLine, normalizedLines);
        if (excerpt.Length > MaxExcerptCharacters)
            excerpt = $"{excerpt[..MaxExcerptCharacters].TrimEnd()}{System.Environment.NewLine}...";

        return excerpt;
    }

    private static string NormalizeApprovalState(string? approvalState)
    {
        var normalized = (approvalState ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "approved" => "approved",
            "rejected" => "rejected",
            "needs_revision" => "needs_revision",
            "revision_requested" => "needs_revision",
            "pending" => "pending_review",
            "pending_review" => "pending_review",
            "pending_operator_review" => "pending_review",
            "" => "pending_review",
            _ when normalized.Contains("approve", StringComparison.Ordinal) => "approved",
            _ when normalized.Contains("reject", StringComparison.Ordinal) => "rejected",
            _ when normalized.Contains("revision", StringComparison.Ordinal) => "needs_revision",
            _ => "pending_review"
        };
    }

    private static string NormalizeChangeKind(string? changeKind)
    {
        var normalized = (changeKind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "created" => "created",
            "added" => "created",
            "modified" => "modified",
            "updated" => "modified",
            "deleted" => "deleted",
            "removed" => "deleted",
            _ => "modified"
        };
    }

    private static string NormalizeFileCategory(string? fileCategory)
    {
        var normalized = (fileCategory ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "source_code" : normalized;
    }

    private static string InferFileCategory(string path)
        => path switch
        {
            _ when path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when string.Equals(Path.GetFileName(path), "global.json", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when string.Equals(Path.GetFileName(path), "nuget.config", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when string.Equals(Path.GetFileName(path), "package.json", StringComparison.OrdinalIgnoreCase) => "dependency_manifest",
            _ when string.Equals(Path.GetFileName(path), "package-lock.json", StringComparison.OrdinalIgnoreCase) => "dependency_manifest",
            _ when string.Equals(Path.GetFileName(path), "pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) => "dependency_manifest",
            _ when string.Equals(Path.GetFileName(path), "yarn.lock", StringComparison.OrdinalIgnoreCase) => "dependency_manifest",
            _ when string.Equals(Path.GetFileName(path), "packages.lock.json", StringComparison.OrdinalIgnoreCase) => "dependency_manifest",
            _ when string.Equals(Path.GetFileName(path), "xunit.runner.json", StringComparison.OrdinalIgnoreCase) => "test_harness_config",
            _ when path.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase) => "test_harness_config",
            _ when path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) => "ui_markup",
            _ when path.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase) => "view_model",
            _ when path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) => "test_code",
            _ when path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "config_json",
            _ when path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) => "docs",
            _ => "source_code"
        };

    private static string NormalizeRelativePath(string? path)
        => (path ?? string.Empty)
            .Trim()
            .Replace('/', '\\')
            .TrimStart('\\');

    private static string NormalizeDirectoryPath(string? directoryPath)
    {
        var normalized = NormalizeRelativePath(directoryPath);
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static bool IsApprovedState(string approvalState)
        => string.Equals(approvalState, "approved", StringComparison.Ordinal);

    private static bool IsRejectedState(string approvalState)
        => string.Equals(approvalState, "rejected", StringComparison.Ordinal);

    private static bool IsNeedsRevisionState(string approvalState)
        => string.Equals(approvalState, "needs_revision", StringComparison.Ordinal);

    private static bool IsPendingState(string approvalState)
        => !IsApprovedState(approvalState) && !IsRejectedState(approvalState) && !IsNeedsRevisionState(approvalState);

    private static string FindFirstFile(
        IEnumerable<BuilderReviewWorkspaceFileRecord> files,
        Func<BuilderReviewWorkspaceFileRecord, bool> predicate)
        => files.Where(predicate)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => file.RelativePath)
            .FirstOrDefault() ?? string.Empty;

    private static string FormatLabel(string value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Replace('_', ' ');

    private static string FormatApprovalState(string value)
        => value switch
        {
            "approved" => "Approved",
            "rejected" => "Rejected",
            "needs_revision" => "Needs revision",
            "pending_review" => "Pending review",
            _ => FormatLabel(value)
        };

    private static string SanitizeIdToken(string value)
    {
        var effective = Path.GetFileNameWithoutExtension(value);
        if (string.IsNullOrWhiteSpace(effective))
            effective = value;

        var builder = new StringBuilder(effective.Length);
        foreach (var ch in effective)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static DateTimeOffset MaxDate(params DateTimeOffset[] values)
        => values.OrderByDescending(value => value).FirstOrDefault();

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? TryReadAllText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return ReadAllTextShared(path);
        }
        catch
        {
            return null;
        }
    }

    private static T? Load<T>(string path)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(ReadAllTextShared(path), JsonOptions());
        }
        catch
        {
            return null;
        }
    }

    private static T Load<T>(string path, T fallback)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(ReadAllTextShared(path), JsonOptions()) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var serialized = JsonSerializer.Serialize(value, JsonOptions());
        var gate = SaveLocks.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(serialized);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
