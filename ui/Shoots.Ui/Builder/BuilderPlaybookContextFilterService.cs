using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderPlaybookContextSnapshotRecord(
    string WorkspaceId,
    string OperatorIntent,
    string ActiveConstraintProfileId,
    int ActiveConstraintCount,
    string CurrentFailureClass,
    string RepoFocus,
    string ActiveRoute,
    string CurrentBlockingState,
    string OrchestrationState,
    IReadOnlyList<string> BlockingGates,
    IReadOnlyList<string> RecentOperatorDecisionIds,
    IReadOnlyList<string> RecentOperatorActions,
    bool IsCrossRepoBlock,
    bool IsHighRiskContext,
    string Summary);

public sealed record BuilderPlaybookContextFilterModeRecord(
    string ModeId,
    string Label,
    int VisiblePlaybookCount,
    int VisibleSimulationCount,
    string Summary);

public sealed record BuilderPlaybookContextFilterEntryRecord(
    string PlaybookId,
    double BaseRelevanceScore,
    double RelevanceScore,
    double IntentAlignmentScore,
    string IntentReason,
    bool ViolatesConstraints,
    IReadOnlyList<string> ViolatedConstraints,
    string ConstraintReason,
    string PriorityBand,
    IReadOnlyList<string> ActiveContextFlags,
    string FilterReason,
    string VisibilityState,
    IReadOnlyList<string> VisibleSimulationIds,
    IReadOnlyList<string> EvidenceLinks);

public sealed record BuilderPlaybookContextFiltersRecord(
    string WorkspaceId,
    string SchemaVersion,
    BuilderPlaybookContextSnapshotRecord ContextSnapshot,
    IReadOnlyList<BuilderPlaybookContextFilterModeRecord> Filters,
    IReadOnlyList<BuilderPlaybookContextFilterEntryRecord> RelevanceScores,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderPlaybookContextFilterService
{
    public const string PlaybookContextFiltersFileName = "builder_playbook_context_filters.json";
    public const string ShowAllModeId = "show_all";
    public const string ShowRelevantModeId = "show_relevant";
    public const string ShowHighPriorityOnlyModeId = "show_high_priority_only";

    private const string SchemaVersion = "builder_playbook_context_filters.v3";
    private const double FailureMatchWeight = 0.35d;
    private const double RepoAlignmentWeight = 0.10d;
    private const double RouteAlignmentWeight = 0.15d;
    private const double ScopeAlignmentWeight = 0.10d;
    private const double BlockingGateWeight = 0.10d;
    private const double RankingInfluenceWeight = 0.10d;
    private const double RecentDecisionWeight = 0.05d;
    private const double HighRiskWeight = 0.05d;
    private const double RelevantThreshold = 55d;
    private const double HighPriorityThreshold = 75d;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string PlaybookContextFiltersPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), PlaybookContextFiltersFileName);

    public static BuilderPlaybookContextFiltersRecord? LoadContextFilters(string repoRoot)
        => Load<BuilderPlaybookContextFiltersRecord>(PlaybookContextFiltersPathForRepo(repoRoot));

    public static BuilderPlaybookContextFiltersRecord? RefreshContextFilters(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderRouteRiskWarningsRecord? riskWarnings = null,
        BuilderCrossRepoExecutionStateRecord? executionState = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        if (playbooks is null)
        {
            return null;
        }

        rankings ??= BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        riskWarnings ??= BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        executionState ??= BuilderCrossRepoOrchestrationService.LoadExecutionState(repoRoot);

        var snapshot = BuildContextSnapshot(repoRoot, playbooks, decisions, riskWarnings, executionState);
        var rankingIndex = (rankings?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var recentDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        var entries = playbooks.Playbooks
            .Select(playbook => BuildEntry(
                repoRoot,
                playbooks,
                playbook,
                snapshot,
                recentDecisions,
                rankingIndex.TryGetValue(playbook.PlaybookId, out var ranking) ? ranking : null))
            .OrderByDescending(entry => entry.RelevanceScore)
            .ThenBy(entry => PriorityBandRank(entry.PriorityBand))
            .ThenBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var filters = BuildFilters(playbooks, entries);
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var report = new BuilderPlaybookContextFiltersRecord(
            playbooks.WorkspaceId,
            SchemaVersion,
            snapshot,
            filters,
            entries,
            true,
            BuildSummary(playbooks.WorkspaceId, snapshot, entries, filters),
            PlaybookContextFiltersPathForRepo(repoRoot),
            effectiveObservedUtc);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderPlaybookContextSnapshotRecord BuildContextSnapshot(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord playbooks,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderRouteRiskWarningsRecord? riskWarnings,
        BuilderCrossRepoExecutionStateRecord? executionState)
    {
        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var operatorIntent = BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        var constraints = BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        var activeConstraintProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(repoRoot);
        var highRiskFlags = BuilderReviewWorkspaceService.LoadHighRiskFileFlags(repoRoot);
        var currentFailureClass = playbooks.FailurePatterns.FirstOrDefault()?.FailureClass
                                  ?? playbooks.Playbooks.FirstOrDefault()?.FailureClass
                                  ?? "not_recorded";
        var activeRoute = routeResolution?.RouteDecision
                          ?? playbooks.FailurePatterns.FirstOrDefault()?.RouteId
                          ?? playbooks.Playbooks.FirstOrDefault()?.AppliesToRoutes.FirstOrDefault()
                          ?? "not_recorded";
        var currentBlockingState = reviewWorkspace?.ReviewCounts.FinalizeEligibilityState
                                   ?? executionState?.WorkspaceStatusList.FirstOrDefault(entry =>
                                       string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))?.FinalizeReadiness
                                   ?? playbooks.Playbooks.FirstOrDefault()?.CurrentBlockingState
                                   ?? "not_recorded";
        var orchestrationState = executionState?.FinalizeReadiness
                                 ?? playbooks.CrossRepoCoordination.Summary
                                 ?? "not_recorded";
        var recentDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var isCrossRepoBlock = playbooks.CrossRepoCoordination.BlockingRepoIds.Count > 0 ||
                               executionState?.WorkspaceStatusList.Count > 1 &&
                               (string.Equals(executionState.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(executionState.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase));
        var isHighRiskContext = highRiskFlags?.Entries.Any(entry => entry.RequiresExplicitApproval) ?? false;
        var blockingGates = ResolveBlockingGates(currentFailureClass, currentBlockingState, isHighRiskContext, riskWarnings, activeRoute);

        return new BuilderPlaybookContextSnapshotRecord(
            workspaceId,
            operatorIntent?.Intent ?? string.Empty,
            activeConstraintProfile?.ProfileId ?? string.Empty,
            activeConstraintProfile?.Constraints.Count ?? 0,
            currentFailureClass,
            workspaceId,
            activeRoute,
            currentBlockingState,
            orchestrationState,
            blockingGates,
            recentDecisions.Select(entry => entry.DecisionId).ToArray(),
            recentDecisions.Select(entry => $"{entry.ActionTaken}:{entry.ResultState}").ToArray(),
            isCrossRepoBlock,
            isHighRiskContext,
            BuildSnapshotSummary(workspaceId, operatorIntent?.Intent ?? string.Empty, activeConstraintProfile?.ProfileName ?? string.Empty, activeConstraintProfile?.Constraints.Count ?? 0, currentFailureClass, activeRoute, currentBlockingState, blockingGates, recentDecisions.Length, isCrossRepoBlock, isHighRiskContext));
    }

    private static IReadOnlyList<string> ResolveBlockingGates(
        string currentFailureClass,
        string currentBlockingState,
        bool isHighRiskContext,
        BuilderRouteRiskWarningsRecord? riskWarnings,
        string activeRoute)
    {
        var gates = new List<string>();
        if (string.Equals(currentFailureClass, "route_failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentFailureClass, "repeated_failure_pattern", StringComparison.OrdinalIgnoreCase) ||
            (riskWarnings?.Entries.Any(entry => string.Equals(entry.RouteAttempted, activeRoute, StringComparison.OrdinalIgnoreCase)) ?? false))
        {
            gates.Add("routing_policy");
        }

        if (string.Equals(currentBlockingState, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentBlockingState, "pending_review", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentFailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentFailureClass, "review_blocked", StringComparison.OrdinalIgnoreCase))
        {
            gates.Add("review_gate");
        }

        if (isHighRiskContext ||
            string.Equals(currentBlockingState, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentFailureClass, "high_risk_change_stalled", StringComparison.OrdinalIgnoreCase))
        {
            gates.Add("approval_gate");
        }

        if (!string.Equals(currentBlockingState, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentBlockingState, "no_changed_files", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentBlockingState, "finalized", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentBlockingState, "applied", StringComparison.OrdinalIgnoreCase))
        {
            gates.Add("finalize_gate");
        }

        return gates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderPlaybookContextFilterEntryRecord BuildEntry(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord playbooks,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderPlaybookContextSnapshotRecord snapshot,
        IReadOnlyList<BuilderOperatorDecisionRecord> recentDecisions,
        BuilderPlaybookRankingRecord? ranking)
    {
        var failureMatch = string.Equals(playbook.FailureClass, snapshot.CurrentFailureClass, StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
        var repoAlignment = string.Equals(playbook.RepoScope, snapshot.RepoFocus, StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
        var routeAlignment = playbook.AppliesToRoutes.Contains(snapshot.ActiveRoute, StringComparer.OrdinalIgnoreCase) ? 1d : 0d;
        var scopeAlignment = snapshot.IsCrossRepoBlock
            ? playbook.CrossRepoScope ? 1d : 0d
            : playbook.CrossRepoScope ? 0d : 1d;
        var blockingGateMatch = ResolveBlockingGateMatch(playbook, snapshot.BlockingGates, snapshot.CurrentBlockingState);
        var rankingInfluence = ranking is null ? 0.50d : Clamp01(ranking.RankingScore / 100d);
        var recentDecisionMatch = recentDecisions.Any(entry =>
            string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.FailureClass, playbook.FailureClass, StringComparison.OrdinalIgnoreCase) ||
            playbook.AppliesToRoutes.Contains(entry.TargetRoute, StringComparer.OrdinalIgnoreCase))
            ? 1d
            : 0d;
        var highRiskMatch = snapshot.IsHighRiskContext &&
                            string.Equals(playbook.FailureClass, "high_risk_change_stalled", StringComparison.OrdinalIgnoreCase)
            ? 1d
            : 0d;

        var baseRelevanceScore = Math.Round(Clamp01(
                FailureMatchWeight * failureMatch +
                RepoAlignmentWeight * repoAlignment +
                RouteAlignmentWeight * routeAlignment +
                ScopeAlignmentWeight * scopeAlignment +
                BlockingGateWeight * blockingGateMatch +
                RankingInfluenceWeight * rankingInfluence +
                RecentDecisionWeight * recentDecisionMatch +
                HighRiskWeight * highRiskMatch) * 100d,
            2);
        var intentAlignmentScore = ranking?.IntentAlignmentScore ?? 0d;
        var constraintEvaluation = BuilderOperatorConstraintService.EvaluatePlaybookConstraints(
            playbook,
            BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot),
            snapshot.CurrentBlockingState,
            snapshot.IsHighRiskContext);
        var relevanceScore = string.IsNullOrWhiteSpace(snapshot.OperatorIntent)
            ? baseRelevanceScore
            : Math.Round(Clamp01((baseRelevanceScore + ((intentAlignmentScore - 50d) * 0.20d)) / 100d) * 100d, 2);
        var priorityBand = relevanceScore >= HighPriorityThreshold
            ? "high"
            : relevanceScore >= RelevantThreshold
                ? "medium"
                : "low";
        var visibilityState = priorityBand switch
        {
            "high" => "visible_in_high_priority_only",
            "medium" => "visible_in_relevant_only",
            _ => "visible_in_show_all_only"
        };
        var activeFlags = BuildActiveFlags(
            failureMatch > 0d,
            repoAlignment > 0d,
            routeAlignment > 0d,
            snapshot.IsCrossRepoBlock && playbook.CrossRepoScope,
            highRiskMatch > 0d,
            blockingGateMatch > 0d,
            recentDecisionMatch > 0d,
            ranking is not null && ranking.RankingScore >= RelevantThreshold,
            !string.IsNullOrWhiteSpace(snapshot.OperatorIntent) && intentAlignmentScore >= RelevantThreshold,
            constraintEvaluation.ViolatesConstraints);
        var filterReason = BuildFilterReason(
            playbook,
            snapshot,
            priorityBand,
            baseRelevanceScore,
            relevanceScore,
            activeFlags,
            ranking,
            constraintEvaluation);
        var evidenceLinks = new[]
            {
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
                BuilderWorkspaceService.RouteResolutionPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoRoot)
            }
            .Concat(playbook.ArtifactLinks)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderPlaybookContextFilterEntryRecord(
            playbook.PlaybookId,
            baseRelevanceScore,
            relevanceScore,
            intentAlignmentScore,
            ranking?.IntentReason ?? "No explicit operator intent is currently recorded.",
            constraintEvaluation.ViolatesConstraints,
            constraintEvaluation.ViolatedConstraints,
            constraintEvaluation.ConstraintReason,
            priorityBand,
            activeFlags,
            filterReason,
            visibilityState,
            playbook.SimulationIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            evidenceLinks);
    }

    private static double ResolveBlockingGateMatch(
        BuilderRecoveryPlaybookRecord playbook,
        IReadOnlyList<string> blockingGates,
        string currentBlockingState)
    {
        if (string.Equals(playbook.CurrentBlockingState, currentBlockingState, StringComparison.OrdinalIgnoreCase))
        {
            return 1d;
        }

        var playbookGates = new List<string>();
        if (playbook.RequiresReviewGate)
        {
            playbookGates.Add("review_gate");
        }

        if (playbook.RequiresApprovalGate)
        {
            playbookGates.Add("approval_gate");
        }

        if (playbook.RequiresFinalizeGate)
        {
            playbookGates.Add("finalize_gate");
        }

        if (string.Equals(playbook.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(playbook.FailureClass, "repeated_failure_pattern", StringComparison.OrdinalIgnoreCase))
        {
            playbookGates.Add("routing_policy");
        }

        return playbookGates.Any(gate => blockingGates.Contains(gate, StringComparer.OrdinalIgnoreCase))
            ? 0.5d
            : 0d;
    }

    private static IReadOnlyList<string> BuildActiveFlags(
        bool isCurrentFailureMatch,
        bool isRepoRelevant,
        bool isRouteRelevant,
        bool isCrossRepoBlock,
        bool isHighRiskContext,
        bool hasBlockingGateAlignment,
        bool hasRecentOperatorFocus,
        bool hasRankingSupport,
        bool isIntentAligned,
        bool violatesConstraints)
    {
        var flags = new List<string>();
        if (isCurrentFailureMatch)
        {
            flags.Add("is_current_failure_match");
        }

        if (isRepoRelevant)
        {
            flags.Add("is_repo_relevant");
        }

        if (isRouteRelevant)
        {
            flags.Add("is_route_relevant");
        }

        if (isCrossRepoBlock)
        {
            flags.Add("is_cross_repo_block");
        }

        if (isHighRiskContext)
        {
            flags.Add("is_high_risk_context");
        }

        if (hasBlockingGateAlignment)
        {
            flags.Add("has_blocking_gate_alignment");
        }

        if (hasRecentOperatorFocus)
        {
            flags.Add("has_recent_operator_focus");
        }

        if (hasRankingSupport)
        {
            flags.Add("has_ranking_support");
        }

        if (isIntentAligned)
        {
            flags.Add("is_intent_aligned");
        }

        if (violatesConstraints)
        {
            flags.Add("violates_constraints");
        }

        return flags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BuilderPlaybookContextFilterModeRecord> BuildFilters(
        BuilderRecoveryPlaybooksRecord playbooks,
        IReadOnlyList<BuilderPlaybookContextFilterEntryRecord> entries)
    {
        var visibleByPlaybookId = entries.ToDictionary(entry => entry.PlaybookId, entry => entry.VisibleSimulationIds.Count, StringComparer.OrdinalIgnoreCase);
        return new[]
        {
            BuildFilterMode(ShowAllModeId, "Show All", entries, entry => true, visibleByPlaybookId),
            BuildFilterMode(ShowRelevantModeId, "Show Relevant", entries, entry => !string.Equals(entry.VisibilityState, "visible_in_show_all_only", StringComparison.OrdinalIgnoreCase), visibleByPlaybookId),
            BuildFilterMode(ShowHighPriorityOnlyModeId, "Show High Priority Only", entries, entry => string.Equals(entry.PriorityBand, "high", StringComparison.OrdinalIgnoreCase), visibleByPlaybookId)
        }
        .OrderBy(entry => FilterModeRank(entry.ModeId))
        .ThenBy(entry => entry.ModeId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static BuilderPlaybookContextFilterModeRecord BuildFilterMode(
        string modeId,
        string label,
        IReadOnlyList<BuilderPlaybookContextFilterEntryRecord> entries,
        Func<BuilderPlaybookContextFilterEntryRecord, bool> predicate,
        IReadOnlyDictionary<string, int> visibleByPlaybookId)
    {
        var visible = entries.Where(predicate).ToArray();
        var simulationCount = visible.Sum(entry => visibleByPlaybookId.TryGetValue(entry.PlaybookId, out var count) ? count : 0);
        return new BuilderPlaybookContextFilterModeRecord(
            modeId,
            label,
            visible.Length,
            simulationCount,
            $"{label} exposes {visible.Length} playbook(s) and {simulationCount} simulation(s).");
    }

    private static string BuildFilterReason(
        BuilderRecoveryPlaybookRecord playbook,
        BuilderPlaybookContextSnapshotRecord snapshot,
        string priorityBand,
        double baseRelevanceScore,
        double relevanceScore,
        IReadOnlyList<string> activeFlags,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookConstraintEvaluationRecord constraintEvaluation)
    {
        if (activeFlags.Count == 0)
        {
            return $"Available in Show All only with relevance {relevanceScore:0.##}. No strong match was recorded for the current failure, route, or blocking gate.";
        }

        var reasons = new List<string>();
        if (activeFlags.Contains("is_current_failure_match", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"Matches the current failure class {snapshot.CurrentFailureClass.Replace('_', ' ')}.");
        }

        if (activeFlags.Contains("is_route_relevant", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"Matches the active route {snapshot.ActiveRoute}.");
        }

        if (activeFlags.Contains("is_cross_repo_block", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add("Matches the current cross-repo blocking context.");
        }

        if (activeFlags.Contains("is_high_risk_context", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add("Matches the current high-risk review context.");
        }

        if (activeFlags.Contains("has_blocking_gate_alignment", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"Aligns with the active blocking gate set {string.Join(", ", snapshot.BlockingGates.Select(FormatToken))}.");
        }

        if (activeFlags.Contains("has_recent_operator_focus", StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add("Recent operator activity touched this playbook, route, or failure class.");
        }

        if (activeFlags.Contains("has_ranking_support", StringComparer.OrdinalIgnoreCase) && ranking is not null)
        {
            reasons.Add($"Ranking support is {ranking.RankingScore:0.##} with {FormatToken(ranking.ConfidenceIndicator)}.");
        }

        if (activeFlags.Contains("is_intent_aligned", StringComparer.OrdinalIgnoreCase) &&
            ranking is not null &&
            !string.IsNullOrWhiteSpace(snapshot.OperatorIntent))
        {
            reasons.Add($"Intent alignment for {BuilderOperatorIntentService.GetIntentLabel(snapshot.OperatorIntent)} is {ranking.IntentAlignmentScore:0.##}. {ranking.IntentReason}");
        }

        if (constraintEvaluation.ViolatesConstraints)
        {
            reasons.Add(constraintEvaluation.ConstraintReason);
        }

        reasons.Add($"{FormatToken(priorityBand)} priority in the current workspace context with base relevance {baseRelevanceScore:0.##} and current relevance {relevanceScore:0.##}.");
        return string.Join(" ", reasons);
    }

    private static string BuildSnapshotSummary(
        string workspaceId,
        string operatorIntent,
        string constraintProfileName,
        int constraintCount,
        string currentFailureClass,
        string activeRoute,
        string currentBlockingState,
        IReadOnlyList<string> blockingGates,
        int recentDecisionCount,
        bool isCrossRepoBlock,
        bool isHighRiskContext)
        => $"{workspaceId}: operator intent {BuilderOperatorIntentService.GetIntentLabel(operatorIntent)}, active constraint profile {(string.IsNullOrWhiteSpace(constraintProfileName) ? "none" : constraintProfileName)} with {constraintCount} explicit constraint(s), current failure {FormatToken(currentFailureClass)}, active route {activeRoute}, blocking state {FormatToken(currentBlockingState)}, gates {string.Join(", ", blockingGates.Select(FormatToken))}. Recent operator decisions: {recentDecisionCount}. Cross-repo block: {isCrossRepoBlock}. High-risk context: {isHighRiskContext}.";

    private static string BuildSummary(
        string workspaceId,
        BuilderPlaybookContextSnapshotRecord snapshot,
        IReadOnlyList<BuilderPlaybookContextFilterEntryRecord> entries,
        IReadOnlyList<BuilderPlaybookContextFilterModeRecord> filters)
    {
        var relevant = filters.FirstOrDefault(entry => string.Equals(entry.ModeId, ShowRelevantModeId, StringComparison.OrdinalIgnoreCase));
        var highPriority = filters.FirstOrDefault(entry => string.Equals(entry.ModeId, ShowHighPriorityOnlyModeId, StringComparison.OrdinalIgnoreCase));
        var violatingCount = entries.Count(entry => entry.ViolatesConstraints);
        return $"Generated contextual narrowing for {entries.Count} playbook(s) in {workspaceId}. Operator intent: {BuilderOperatorIntentService.GetIntentLabel(snapshot.OperatorIntent)}. Active constraint count: {snapshot.ActiveConstraintCount}. Current failure: {FormatToken(snapshot.CurrentFailureClass)}. Relevant view exposes {relevant?.VisiblePlaybookCount ?? 0} playbook(s); high-priority view exposes {highPriority?.VisiblePlaybookCount ?? 0}; constraint-violating playbooks: {violatingCount}.";
    }

    private static int FilterModeRank(string modeId)
        => modeId switch
        {
            ShowAllModeId => 0,
            ShowRelevantModeId => 1,
            ShowHighPriorityOnlyModeId => 2,
            _ => 3
        };

    private static int PriorityBandRank(string priorityBand)
        => priorityBand switch
        {
            "high" => 0,
            "medium" => 1,
            _ => 2
        };

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

    private static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            lock (GetSaveLock(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream);
            }
        }
        catch
        {
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (GetSaveLock(path))
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, SerializerOptions);
        }
    }

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());
}
