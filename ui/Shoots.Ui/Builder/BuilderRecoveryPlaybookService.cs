using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderRecoveryPatternRecord(
    string PatternId,
    string FailureClass,
    string RepoId,
    string RouteId,
    string RunId,
    string EvidenceBasis,
    IReadOnlyList<string> TriggerArtifacts,
    IReadOnlyList<string> RelatedWarningIds,
    int HistoricalOccurrenceCount,
    string CurrentBlockingState,
    string Severity,
    DateTimeOffset ObservedUtc)
{
    public string Summary => $"{FormatState(Severity)} {FormatState(FailureClass)} for {RepoId} on route {RouteId}. Evidence: {EvidenceBasis}";

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderRecoveryPlaybookStepRecord(
    int StepNumber,
    string ActionType,
    string OperatorAction,
    string Rationale,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> BlockedBy,
    IReadOnlyList<string> LinkedArtifacts)
{
    public string Summary => $"Step {StepNumber}: {OperatorAction} Reason: {Rationale}";
}

public sealed record BuilderRecoveryPlaybookRecord(
    string PlaybookId,
    string Title,
    string Severity,
    string FailureClass,
    string RepoScope,
    bool CrossRepoScope,
    IReadOnlyList<string> AppliesToRunIds,
    IReadOnlyList<string> AppliesToRoutes,
    IReadOnlyList<string> TriggerPatternIds,
    string EvidenceBasis,
    IReadOnlyList<string> EvidenceSources,
    IReadOnlyList<BuilderRecoveryPlaybookStepRecord> RecommendedSteps,
    string ExpectedOperatorGoal,
    string CurrentBlockingState,
    bool RequiresReviewGate,
    bool RequiresApprovalGate,
    bool RequiresFinalizeGate,
    bool AdvisoryOnly,
    IReadOnlyList<string> ArtifactLinks,
    IReadOnlyList<string> SimulationIds,
    string ReasoningSummary)
{
    public string Summary => $"{Title}. Failure class: {FailureClass}. Goal: {ExpectedOperatorGoal}. Advisory only: {AdvisoryOnly}.";

    public string GateSummary
        => $"Review gate: {RequiresReviewGate}. Approval gate: {RequiresApprovalGate}. Finalize gate: {RequiresFinalizeGate}.";
}

public sealed record BuilderRecoveryCoordinationRecord(
    string CoordinationId,
    IReadOnlyList<string> BlockingRepoIds,
    IReadOnlyList<string> AffectedRepoIds,
    IReadOnlyList<string> UpstreamDownstreamRelations,
    IReadOnlyList<string> RecommendedRecoveryOrder,
    IReadOnlyList<string> StagingSuggestions,
    IReadOnlyList<string> RepoIndependenceNotes,
    bool AdvisoryOnly,
    string Summary)
{
    public string RecoveryOrderSummary => RecommendedRecoveryOrder.Count == 0
        ? "No coordinated recovery order recorded."
        : string.Join(" -> ", RecommendedRecoveryOrder);
}

public sealed record BuilderRecoveryPlaybooksRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<string> SourceRunIds,
    IReadOnlyList<BuilderRecoveryPatternRecord> FailurePatterns,
    IReadOnlyList<BuilderRecoveryPlaybookRecord> Playbooks,
    BuilderRecoveryCoordinationRecord CrossRepoCoordination,
    IReadOnlyList<string> ArtifactLinks,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderRecoveryPlaybookService
{
    public const string RecoveryPlaybooksFileName = "builder_recovery_playbooks.json";

    private const string SchemaVersion = "builder_recovery_playbooks.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string RecoveryPlaybooksPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), RecoveryPlaybooksFileName);

    public static IReadOnlyList<string> ResolveSimulationScenarios(string failureClass)
        => failureClass switch
        {
            "patch_rejected" => new[] { "reduce_scope", "isolate_high_risk_files", "switch_route_manual" },
            "route_failed" => new[] { "retry_same_route", "switch_route_manual", "reduce_scope" },
            "review_blocked" => new[] { "reduce_scope" },
            "finalize_blocked" => new[] { "reduce_scope" },
            "orchestration_blocked" => new[] { "staged_orchestration", "reduce_scope" },
            "repeated_failure_pattern" => new[] { "switch_route_manual", "reduce_scope", "staged_orchestration" },
            "cross_repo_dependency_block" => new[] { "staged_orchestration", "reduce_scope" },
            "high_risk_change_stalled" => new[] { "isolate_high_risk_files", "reduce_scope" },
            _ => new[] { "reduce_scope" }
        };

    public static IReadOnlyList<string> ResolveSimulationIds(string playbookId, string failureClass)
        => ResolveSimulationScenarios(failureClass)
            .Select(scenario => ComputeDeterministicId("simulation", playbookId, scenario))
            .ToArray();

    public static BuilderRecoveryPlaybooksRecord? LoadRecoveryPlaybooks(string repoRoot)
        => Load<BuilderRecoveryPlaybooksRecord>(RecoveryPlaybooksPathForRepo(repoRoot));

    public static BuilderRecoveryPlaybooksRecord? RefreshRecoveryPlaybooks(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        BuilderCrossRepoOrchestrationContext orchestration,
        string activeWorkspaceId,
        string requestId,
        DateTimeOffset? observedUtc = null,
        int maxPlaybooks = 12)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(orchestration);

        var descriptors = workspaces
            .Where(descriptor => descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.RepoRootPath))
            .Select(descriptor => BuilderWorkspaceService.CreateDescriptor(descriptor.RepoRootPath, descriptor.RepoName))
            .Where(descriptor => Directory.Exists(descriptor.RepoRootPath))
            .GroupBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (descriptors.Length == 0)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var activeDescriptor = descriptors.FirstOrDefault(descriptor =>
                                   string.Equals(descriptor.WorkspaceId, activeWorkspaceId, StringComparison.OrdinalIgnoreCase))
                               ?? descriptors[0];
        var statusesByWorkspaceId = orchestration.ExecutionState.WorkspaceStatusList
            .ToDictionary(status => status.WorkspaceId, StringComparer.OrdinalIgnoreCase);
        var workspaceOrder = orchestration.Plan.ParticipatingWorkspaceIds
            .Where(workspaceId => !string.IsNullOrWhiteSpace(workspaceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blockingWorkspaceIds = orchestration.ExecutionState.WorkspaceStatusList
            .Where(IsBlockingWorkspace)
            .Select(status => status.WorkspaceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(workspaceId => IndexOfWorkspace(workspaceOrder, workspaceId))
            .ThenBy(workspaceId => workspaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        BuilderRecoveryPlaybooksRecord? activeArtifact = null;
        foreach (var descriptor in descriptors)
        {
            statusesByWorkspaceId.TryGetValue(descriptor.WorkspaceId, out var status);
            var snapshot = BuildWorkspaceSnapshot(descriptor, status, orchestration, requestId, effectiveObservedUtc);
            var coordination = BuildCoordination(snapshot, orchestration, workspaceOrder, blockingWorkspaceIds);
            var patterns = BuildPatterns(snapshot, orchestration, workspaceOrder, blockingWorkspaceIds, effectiveObservedUtc);
            var playbooks = BuildPlaybooks(snapshot, patterns, coordination, effectiveObservedUtc, maxPlaybooks);
            var sourceRunIds = snapshot.SourceRunIds
                .Concat(playbooks.SelectMany(playbook => playbook.AppliesToRunIds))
                .Where(runId => !string.IsNullOrWhiteSpace(runId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(runId => runId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var artifactLinks = BuildArtifactLinks(snapshot, patterns, playbooks);
            var artifact = new BuilderRecoveryPlaybooksRecord(
                descriptor.WorkspaceId,
                SchemaVersion,
                sourceRunIds,
                patterns,
                playbooks,
                coordination,
                artifactLinks,
                true,
                BuildSummary(descriptor.WorkspaceId, playbooks, patterns, coordination),
                RecoveryPlaybooksPathForRepo(descriptor.RepoRootPath),
                effectiveObservedUtc);

            Save(artifact.ArtifactPath, artifact);
            if (string.Equals(descriptor.WorkspaceId, activeDescriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                activeArtifact = artifact;
            }
        }

        return activeArtifact ?? LoadRecoveryPlaybooks(activeDescriptor.RepoRootPath);
    }

    private static WorkspaceRecoverySnapshot BuildWorkspaceSnapshot(
        BuilderWorkspaceDescriptor descriptor,
        BuilderCrossRepoWorkspaceStatusRecord? status,
        BuilderCrossRepoOrchestrationContext orchestration,
        string requestId,
        DateTimeOffset observedUtc)
    {
        var repoRoot = descriptor.RepoRootPath;
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        var routeRecommendations = BuilderRouteIntelligenceService.LoadRouteRecommendations(repoRoot);
        var routeWarnings = BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(repoRoot);
        var highRiskFlags = BuilderReviewWorkspaceService.LoadHighRiskFileFlags(repoRoot);
        var reviewArtifacts = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot);
        var failurePatterns = BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
        var latestRun = BuilderExecutionService.LoadLatestBuilderProofRun(repoRoot);
        var fileStates = ResolveFileStates(reviewArtifacts);

        var currentRoute = FirstNonEmpty(
            status?.RouteDecision,
            routeResolution?.RouteDecision,
            routeRecommendations?.RecommendedRoutes.FirstOrDefault()?.Route,
            "not_recorded");
        var currentBlockingState = FirstNonEmpty(
            reviewWorkspace?.ReviewCounts.FinalizeEligibilityState,
            reviewArtifacts.PatchApplyDecision?.ApplyEligibilityState,
            status?.FinalizeReadiness,
            "not_recorded");
        var sourceRunIds = new[]
            {
                requestId,
                orchestration.Plan.OrchestrationId,
                reviewWorkspace?.ExecutionSessionId,
                reviewArtifacts.ExecutionSession?.SessionId,
                latestRun?.ProofRunId
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var artifactLinks = new[]
            {
                BuilderWorkspaceService.RouteResolutionPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.RouteRecommendationsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.OrchestrationRecommendationsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.KnowledgeGraphPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.ExecutionPatternsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoRoot),
                BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot),
                BuilderReviewWorkspaceService.ReviewQueuePathForRepo(repoRoot),
                BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot),
                BuilderReviewWorkspaceService.PatchReviewOutcomePathForRepo(repoRoot),
                BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoPlanPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.WorkspaceTaskSegmentsPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoRoot),
                latestRun is null ? string.Empty : BuilderExecutionService.BuilderRouteStabilitySummaryPath(latestRun.RunFolder),
                latestRun is null ? string.Empty : BuilderExecutionService.BuilderRouteReconfirmationPath(latestRun.RunFolder)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkspaceRecoverySnapshot(
            descriptor,
            status,
            currentRoute,
            currentBlockingState,
            routeWarnings?.Entries.Select(BuildWarningId).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>(),
            failurePatterns?.Entries?.Where(entry => string.Equals(entry.Workspace, descriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<BuilderFailurePatternRecord>(),
            fileStates.Where(file => IsRejectedState(file.ApprovalState)).Select(file => file.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            fileStates.Where(file => IsNeedsRevisionState(file.ApprovalState)).Select(file => file.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            fileStates.Where(file => IsPendingState(file.ApprovalState)).Select(file => file.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            highRiskFlags.Entries
                .Where(entry => !fileStates.Any(file =>
                    string.Equals(file.RelativePath, entry.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(file.ApprovalState, "approved", StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.FilePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            sourceRunIds,
            artifactLinks,
            observedUtc);
    }

    private static IReadOnlyList<BuilderRecoveryPatternRecord> BuildPatterns(
        WorkspaceRecoverySnapshot snapshot,
        BuilderCrossRepoOrchestrationContext orchestration,
        IReadOnlyList<string> workspaceOrder,
        IReadOnlyList<string> blockingWorkspaceIds,
        DateTimeOffset observedUtc)
    {
        var patterns = new List<BuilderRecoveryPatternRecord>();
        var routeFailures = snapshot.Failures
            .Where(entry => string.Equals(entry.RouteAttempted, snapshot.CurrentRoute, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.FailureId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (snapshot.RejectedFiles.Count > 0)
        {
            patterns.Add(BuildPattern(snapshot, "patch_rejected", "high", $"Rejected files keep the workspace blocked: {string.Join(", ", snapshot.RejectedFiles)}.", snapshot.RejectedFiles.Count, observedUtc));
        }

        if (snapshot.PendingFiles.Count > 0 && snapshot.RejectedFiles.Count == 0 && snapshot.RevisionFiles.Count == 0)
        {
            patterns.Add(BuildPattern(snapshot, "review_blocked", "medium", $"Pending review files still need explicit operator decisions: {string.Join(", ", snapshot.PendingFiles)}.", snapshot.PendingFiles.Count, observedUtc));
        }

        if (IsFinalizeBlocked(snapshot.CurrentBlockingState))
        {
            var blockReasons = snapshot.Status?.Summary ?? $"Finalize remains blocked in state {snapshot.CurrentBlockingState}.";
            patterns.Add(BuildPattern(snapshot, "finalize_blocked", snapshot.RevisionFiles.Count > 0 ? "high" : "medium", blockReasons, 1, observedUtc));
        }

        if (routeFailures.Length > 0 || snapshot.WarningIds.Count > 0)
        {
            patterns.Add(BuildPattern(
                snapshot,
                "route_failed",
                routeFailures.Length >= 2 ? "critical" : "high",
                routeFailures.Length > 0
                    ? $"Route {snapshot.CurrentRoute} has {routeFailures.Length} recorded blocked outcome(s). Latest evidence: {routeFailures[0].FailureReason}"
                    : $"Route {snapshot.CurrentRoute} is currently warned by route intelligence.",
                Math.Max(routeFailures.Length, snapshot.WarningIds.Count),
                observedUtc));
        }

        if (routeFailures.Length >= 2 || snapshot.Failures.GroupBy(entry => entry.RouteAttempted, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() >= 2))
        {
            var repeatedRoute = routeFailures.Length >= 2
                ? snapshot.CurrentRoute
                : snapshot.Failures
                    .GroupBy(entry => entry.RouteAttempted, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Key)
                    .First();
            var repeatedCount = snapshot.Failures.Count(entry => string.Equals(entry.RouteAttempted, repeatedRoute, StringComparison.OrdinalIgnoreCase));
            patterns.Add(BuildPattern(snapshot with { CurrentRoute = repeatedRoute }, "repeated_failure_pattern", "critical", $"Route {repeatedRoute} has repeated failure evidence in this workspace ({repeatedCount} recorded occurrence(s)).", repeatedCount, observedUtc));
        }

        if (snapshot.HighRiskStalledFiles.Count > 0)
        {
            patterns.Add(BuildPattern(snapshot, "high_risk_change_stalled", "high", $"High-risk files still require focused review: {string.Join(", ", snapshot.HighRiskStalledFiles)}.", snapshot.HighRiskStalledFiles.Count, observedUtc));
        }

        if (workspaceOrder.Count > 1 && IsOrchestrationBlocked(orchestration))
        {
            patterns.Add(BuildPattern(snapshot, "orchestration_blocked", blockingWorkspaceIds.Contains(snapshot.Descriptor.WorkspaceId, StringComparer.OrdinalIgnoreCase) ? "critical" : "high", $"Cross-repo orchestration is blocked by {DescribeWorkspaceList(blockingWorkspaceIds)}. Current workspace state: {snapshot.CurrentBlockingState}.", Math.Max(blockingWorkspaceIds.Count, 1), observedUtc));
        }

        if (workspaceOrder.Count > 1 && HasCrossRepoDependencyBlock(snapshot.Descriptor.WorkspaceId, workspaceOrder, blockingWorkspaceIds))
        {
            patterns.Add(BuildPattern(snapshot, "cross_repo_dependency_block", "high", BuildDependencyEvidence(snapshot.Descriptor.WorkspaceId, workspaceOrder, blockingWorkspaceIds), Math.Max(blockingWorkspaceIds.Count, 1), observedUtc));
        }

        return patterns
            .OrderBy(pattern => SeverityRank(pattern.Severity))
            .ThenBy(pattern => pattern.FailureClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pattern => pattern.RepoId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pattern => pattern.RouteId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pattern => pattern.PatternId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BuilderRecoveryPlaybookRecord> BuildPlaybooks(
        WorkspaceRecoverySnapshot snapshot,
        IReadOnlyList<BuilderRecoveryPatternRecord> patterns,
        BuilderRecoveryCoordinationRecord coordination,
        DateTimeOffset observedUtc,
        int maxPlaybooks)
        => patterns
            .Select(pattern => BuildPlaybook(snapshot, pattern, coordination, observedUtc))
            .OrderBy(playbook => SeverityRank(playbook.Severity))
            .ThenBy(playbook => playbook.FailureClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(playbook => playbook.RepoScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(playbook => playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(playbook => playbook.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxPlaybooks, 0))
            .ToArray();

    private static BuilderRecoveryPatternRecord BuildPattern(
        WorkspaceRecoverySnapshot snapshot,
        string failureClass,
        string severity,
        string evidenceBasis,
        int historicalOccurrenceCount,
        DateTimeOffset observedUtc)
        => new(
            ComputeDeterministicId("pattern", snapshot.Descriptor.WorkspaceId, failureClass, snapshot.CurrentRoute, snapshot.CurrentBlockingState, evidenceBasis),
            failureClass,
            snapshot.Descriptor.WorkspaceId,
            snapshot.CurrentRoute,
            snapshot.SourceRunIds.FirstOrDefault() ?? snapshot.Descriptor.WorkspaceId,
            evidenceBasis,
            snapshot.ArtifactLinks,
            snapshot.WarningIds,
            historicalOccurrenceCount,
            snapshot.CurrentBlockingState,
            severity,
            observedUtc);

    private static BuilderRecoveryPlaybookRecord BuildPlaybook(
        WorkspaceRecoverySnapshot snapshot,
        BuilderRecoveryPatternRecord pattern,
        BuilderRecoveryCoordinationRecord coordination,
        DateTimeOffset observedUtc)
    {
        var crossRepoScope = string.Equals(pattern.FailureClass, "orchestration_blocked", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(pattern.FailureClass, "cross_repo_dependency_block", StringComparison.OrdinalIgnoreCase);
        var title = pattern.FailureClass switch
        {
            "patch_rejected" => "Recover rejected patch decisions",
            "route_failed" => "Review route failure evidence",
            "review_blocked" => "Clear pending review blockers",
            "finalize_blocked" => "Resolve finalize gate blockers",
            "orchestration_blocked" => "Stage multi-repo recovery",
            "repeated_failure_pattern" => "Break repeated failure cycle",
            "cross_repo_dependency_block" => "Recover upstream dependency block",
            "high_risk_change_stalled" => "Isolate stalled high-risk files",
            _ => "Review advisory recovery guidance"
        };
        var expectedGoal = pattern.FailureClass switch
        {
            "patch_rejected" => "Clear explicit rejected-file blockers before considering another supervised run.",
            "route_failed" => "Understand why the current route failed before any manual reroute or retry.",
            "review_blocked" => "Advance pending files to explicit operator decisions without bypassing review.",
            "finalize_blocked" => "Restore the workspace to ready_to_finalize only after upstream review issues are resolved.",
            "orchestration_blocked" => "Unblock the orchestration by recovering blocking repos in sequence.",
            "repeated_failure_pattern" => "Stop repeating a failing route without new evidence.",
            "cross_repo_dependency_block" => "Resolve upstream repo blockers before downstream retry or finalize.",
            "high_risk_change_stalled" => "Separate high-risk review from low-risk work and clear explicit approvals.",
            _ => "Use deterministic evidence before choosing the next operator action."
        };
        var playbookId = ComputeDeterministicId("playbook", pattern.PatternId, pattern.FailureClass, pattern.RouteId, pattern.CurrentBlockingState);
        var simulationIds = ResolveSimulationIds(playbookId, pattern.FailureClass);

        return new BuilderRecoveryPlaybookRecord(
            playbookId,
            title,
            pattern.Severity,
            pattern.FailureClass,
            snapshot.Descriptor.WorkspaceId,
            crossRepoScope,
            snapshot.SourceRunIds,
            new[] { pattern.RouteId }.Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "not_recorded", StringComparison.OrdinalIgnoreCase)).ToArray(),
            new[] { pattern.PatternId },
            pattern.EvidenceBasis,
            ResolveEvidenceSources(pattern.FailureClass),
            BuildSteps(pattern, coordination, snapshot.ArtifactLinks),
            expectedGoal,
            pattern.CurrentBlockingState,
            true,
            true,
            true,
            true,
            snapshot.ArtifactLinks,
            simulationIds,
            $"Generated from deterministic evidence at {observedUtc:O}. Routing, review, approval, and finalize remain operator-controlled.");
    }

    private static IReadOnlyList<BuilderRecoveryPlaybookStepRecord> BuildSteps(
        BuilderRecoveryPatternRecord pattern,
        BuilderRecoveryCoordinationRecord coordination,
        IReadOnlyList<string> artifactLinks)
    {
        var commonBlockedBy = new[] { "review_gate", "approval_gate", "finalize_gate" };
        return pattern.FailureClass switch
        {
            "patch_rejected" => new[]
            {
                Step(1, "inspect_rejected_files", "Inspect rejected files in the review workspace and queue views.", "Rejected files are explicit blockers that must be understood before another supervised run.", new[] { "Review workspace artifact exists." }, commonBlockedBy, artifactLinks),
                Step(2, "review_high_risk_first", "Review any high-risk files in the same cluster before changing scope.", "High-risk files often explain why the patch was rejected or stalled.", new[] { "High-risk flags or build/runtime files are present." }, new[] { "approval_gate" }, artifactLinks),
                Step(3, "compare_recent_acceptance", "Compare the rejected change cluster against the most recent accepted patterns.", "Historical accepted patterns provide deterministic contrast without authorizing reuse.", new[] { "Execution pattern history exists." }, new[] { "routing_policy" }, artifactLinks),
                Step(4, "manual_reroute_only", "Choose any alternate route manually only after reviewing route warnings and current evidence.", "Route changes remain operator decisions and do not bypass review.", new[] { "Route intelligence has been reviewed." }, new[] { "routing_policy", "review_gate", "approval_gate", "finalize_gate" }, artifactLinks)
            },
            "route_failed" => new[]
            {
                Step(1, "inspect_route_diagnostics", "Inspect the route resolution, route warnings, and route stability artifacts.", "The operator needs the deterministic failure evidence before considering a retry.", new[] { "Current route artifact exists." }, new[] { "routing_policy" }, artifactLinks),
                Step(2, "compare_success_failure_rates", "Compare historical success and failure rates for the same route and model tier.", "Repeated risk without finalized evidence is a signal to avoid blind retries.", new[] { "Knowledge graph and route intelligence artifacts exist." }, new[] { "routing_policy" }, artifactLinks),
                Step(3, "manual_alternate_route", "Consider an alternate recommended route manually when warnings keep repeating.", "Recommendations are advisory only and cannot change routing automatically.", new[] { "Current route warnings were reviewed." }, new[] { "routing_policy", "review_gate" }, artifactLinks),
                Step(4, "retry_after_cause_review", "Retry only after documenting the deterministic cause in the artifact chain.", "Manual cause review prevents repeating the same failure path without new evidence.", new[] { "Failure cause is understood." }, new[] { "review_gate", "approval_gate", "finalize_gate" }, artifactLinks)
            },
            "review_blocked" => new[]
            {
                Step(1, "inspect_pending_queue", "Inspect pending files in the review workspace and review queue.", "Pending review is a state problem, not a permission to finalize.", new[] { "Review queue artifact exists." }, commonBlockedBy, artifactLinks),
                Step(2, "focus_explicit_decisions", "Move each pending file to an explicit approve, reject, or needs-revision decision.", "Finalize cannot progress until each file has a deterministic operator decision.", new[] { "Pending files are identified." }, new[] { "approval_gate", "finalize_gate" }, artifactLinks)
            },
            "finalize_blocked" => new[]
            {
                Step(1, "inspect_finalize_blockers", "Inspect finalize blockers in the patch outcome and patch apply artifacts.", "The builder is blocked by explicit gate state, not by missing automation.", new[] { "Patch apply artifact exists." }, new[] { "finalize_gate" }, artifactLinks),
                Step(2, "clear_upstream_review_state", "Resolve review or revision blockers before attempting finalize again.", "Finalize readiness is downstream from review state.", new[] { "Blocking review reason is identified." }, commonBlockedBy, artifactLinks)
            },
            "orchestration_blocked" => new[]
            {
                Step(1, "identify_blocking_repos", $"Identify the blocking repo set: {DescribeWorkspaceList(coordination.BlockingRepoIds)}.", "Cross-repo recovery begins with the explicit blockers, not a whole-graph reset.", new[] { "Cross-repo execution artifact exists." }, commonBlockedBy, artifactLinks),
                Step(2, "stage_repo_recovery", "Recover blocked repos one at a time and preserve unaffected repo artifacts.", "Successful repos should not be reset because another repo is blocked.", new[] { "Blocking repo order is known." }, commonBlockedBy, artifactLinks),
                Step(3, "reenter_after_valid_state", "Re-enter orchestration only after the blocking repo reaches a valid review or finalize state.", "Cross-repo guidance remains advisory and cannot finalize for another repo.", new[] { "Blocking repo state has changed." }, commonBlockedBy, artifactLinks)
            },
            "repeated_failure_pattern" => new[]
            {
                Step(1, "stop_blind_retries", "Stop repeating the same route until the repeated warning signature is reviewed.", "Repeated failures indicate the current route choice lacks new evidence.", new[] { "Repeated failure evidence exists." }, new[] { "routing_policy" }, artifactLinks),
                Step(2, "compare_last_success", "Compare the current run against the most recent analogous finalized pattern.", "A deterministic delta often explains what changed between success and failure.", new[] { "Execution pattern history exists." }, new[] { "review_gate" }, artifactLinks),
                Step(3, "escalate_manual_review", "Escalate to manual operator review of the linked artifact chain before choosing the next route.", "Manual review is the intended escape hatch when repeat failure history accumulates.", new[] { "Artifact chain is available." }, new[] { "routing_policy", "review_gate", "approval_gate", "finalize_gate" }, artifactLinks)
            },
            "cross_repo_dependency_block" => new[]
            {
                Step(1, "inspect_dependency_relations", "Inspect upstream and downstream repo relations in the orchestration and knowledge artifacts.", "Dependency direction determines where recovery should start.", new[] { "Cross-repo plan exists." }, commonBlockedBy, artifactLinks),
                Step(2, "recover_upstream_first", "Recover the upstream blocking repo before retrying downstream repos.", "A downstream retry is low-value when the upstream dependency remains blocked.", new[] { "Blocking upstream repo is identified." }, commonBlockedBy, artifactLinks),
                Step(3, "hold_downstream_finalize", "Hold downstream finalize until the upstream repo clears review and finalize blockers.", "One repo clearing review never authorizes finalize in another repo.", new[] { "Upstream blocker exists." }, new[] { "finalize_gate" }, artifactLinks)
            },
            "high_risk_change_stalled" => new[]
            {
                Step(1, "isolate_high_risk_files", "Isolate the high-risk file group in the review queue before broad orchestration changes.", "High-risk files deserve focused review rather than being hidden in a wide patch.", new[] { "High-risk flags artifact exists." }, new[] { "approval_gate", "finalize_gate" }, artifactLinks),
                Step(2, "focused_review_before_retry", "Require focused review of the high-risk files before any broad retry or orchestration rerun.", "High-risk review is an explicit gate, not a background heuristic.", new[] { "High-risk files remain non-approved." }, new[] { "approval_gate", "finalize_gate" }, artifactLinks),
                Step(3, "manual_route_choice_after_review", "Revisit route guidance only after the high-risk cluster is understood.", "Route changes without resolving the risk hotspot usually repeat the same stall.", new[] { "High-risk evidence has been reviewed." }, new[] { "routing_policy", "review_gate", "approval_gate", "finalize_gate" }, artifactLinks)
            },
            _ => new[]
            {
                Step(1, "inspect_artifacts", "Inspect the linked recovery artifacts before choosing a next step.", "Recovery guidance remains advisory only.", new[] { "Linked artifacts exist." }, new[] { "review_gate", "approval_gate", "finalize_gate", "routing_policy" }, artifactLinks)
            }
        };
    }

    private static BuilderRecoveryCoordinationRecord BuildCoordination(
        WorkspaceRecoverySnapshot snapshot,
        BuilderCrossRepoOrchestrationContext orchestration,
        IReadOnlyList<string> workspaceOrder,
        IReadOnlyList<string> blockingWorkspaceIds)
    {
        var affectedRepoIds = workspaceOrder
            .Where(workspaceId => HasCrossRepoDependencyBlock(workspaceId, workspaceOrder, blockingWorkspaceIds) ||
                                  blockingWorkspaceIds.Contains(workspaceId, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(workspaceId => IndexOfWorkspace(workspaceOrder, workspaceId))
            .ThenBy(workspaceId => workspaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relations = workspaceOrder.Zip(workspaceOrder.Skip(1), (source, target) => $"{source} -> {target}").OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var recommendedOrder = blockingWorkspaceIds
            .Concat(workspaceOrder.Where(workspaceId => !blockingWorkspaceIds.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(workspaceId => IndexOfWorkspace(workspaceOrder, workspaceId))
            .ThenBy(workspaceId => workspaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stagingSuggestions = BuildStagingSuggestions(workspaceOrder, blockingWorkspaceIds);
        var independenceNotes = new[]
        {
            "Per-repo review and finalize remain independent.",
            "A successful review in one repo never authorizes finalize in another repo.",
            "Staged recovery should preserve unaffected workspace artifacts."
        };

        return new BuilderRecoveryCoordinationRecord(
            ComputeDeterministicId("coordination", orchestration.Plan.OrchestrationId, snapshot.Descriptor.WorkspaceId, string.Join("|", blockingWorkspaceIds)),
            blockingWorkspaceIds,
            affectedRepoIds,
            relations,
            recommendedOrder,
            stagingSuggestions,
            independenceNotes,
            true,
            blockingWorkspaceIds.Count == 0
                ? $"No coordinated recovery is currently required for {snapshot.Descriptor.WorkspaceId}."
                : $"Coordinated recovery for {snapshot.Descriptor.WorkspaceId} should begin with {DescribeWorkspaceList(blockingWorkspaceIds)}. Guidance is advisory only.");
    }

    private static IReadOnlyList<string> BuildArtifactLinks(
        WorkspaceRecoverySnapshot snapshot,
        IReadOnlyList<BuilderRecoveryPatternRecord> patterns,
        IReadOnlyList<BuilderRecoveryPlaybookRecord> playbooks)
        => snapshot.ArtifactLinks
            .Concat(patterns.SelectMany(pattern => pattern.TriggerArtifacts))
            .Concat(playbooks.SelectMany(playbook => playbook.ArtifactLinks))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildSummary(
        string workspaceId,
        IReadOnlyList<BuilderRecoveryPlaybookRecord> playbooks,
        IReadOnlyList<BuilderRecoveryPatternRecord> patterns,
        BuilderRecoveryCoordinationRecord coordination)
        => playbooks.Count == 0
            ? $"No deterministic recovery playbooks are currently recorded for {workspaceId}. Guidance remains advisory only."
            : $"Generated {playbooks.Count} advisory recovery playbook(s) from {patterns.Count} pattern(s) for {workspaceId}. Coordination summary: {coordination.Summary}";

    private static IReadOnlyList<string> ResolveEvidenceSources(string failureClass)
        => failureClass switch
        {
            "patch_rejected" => new[] { "review_rejection_evidence", "high_risk_review_evidence" },
            "route_failed" => new[] { "current_route_warning_evidence", "knowledge_graph_failure_evidence" },
            "review_blocked" => new[] { "review_queue_evidence" },
            "finalize_blocked" => new[] { "review_outcome_evidence", "finalize_gate_evidence" },
            "orchestration_blocked" => new[] { "cross_repo_coordination_evidence", "orchestration_execution_evidence" },
            "repeated_failure_pattern" => new[] { "knowledge_graph_repeat_failure_evidence", "current_route_warning_evidence" },
            "cross_repo_dependency_block" => new[] { "cross_repo_coordination_evidence", "workspace_dependency_evidence" },
            "high_risk_change_stalled" => new[] { "high_risk_review_evidence", "review_queue_evidence" },
            _ => new[] { "artifact_chain_evidence" }
        };

    private static IReadOnlyList<string> BuildStagingSuggestions(IReadOnlyList<string> workspaceOrder, IReadOnlyList<string> blockingWorkspaceIds)
    {
        var suggestions = new List<string>();
        if (blockingWorkspaceIds.Count == 0)
        {
            suggestions.Add("No blocking repo is currently recorded. Preserve the current staged state.");
        }
        else
        {
            foreach (var workspaceId in blockingWorkspaceIds)
            {
                suggestions.Add($"Recover {workspaceId} before retrying downstream repos.");
            }

            for (var index = 0; index < workspaceOrder.Count - 1; index++)
            {
                var source = workspaceOrder[index];
                var target = workspaceOrder[index + 1];
                if (blockingWorkspaceIds.Contains(source, StringComparer.OrdinalIgnoreCase))
                {
                    suggestions.Add($"Hold {target} finalize until {source} clears review and finalize blockers.");
                }
            }

            suggestions.Add("Re-run only the blocked repos where valid instead of resetting the whole orchestration.");
        }

        return suggestions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FileStateRecord> ResolveFileStates(BuilderReviewArtifactSet reviewArtifacts)
    {
        if (reviewArtifacts.FileReviewDecision?.Entries is { Count: > 0 } decisions)
        {
            return decisions
                .Select(entry => new FileStateRecord(entry.RelativePath, entry.ApprovalState))
                .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (reviewArtifacts.PatchDiffReview?.FileEntries is { Count: > 0 } entries)
        {
            return entries
                .Select(entry => new FileStateRecord(entry.RelativePath, entry.ApprovalState))
                .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return Array.Empty<FileStateRecord>();
    }

    private static BuilderRecoveryPlaybookStepRecord Step(
        int stepNumber,
        string actionType,
        string operatorAction,
        string rationale,
        IReadOnlyList<string> preconditions,
        IReadOnlyList<string> blockedBy,
        IReadOnlyList<string> linkedArtifacts)
        => new(
            stepNumber,
            actionType,
            operatorAction,
            rationale,
            preconditions.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            blockedBy.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            linkedArtifacts.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());

    private static bool IsFinalizeBlocked(string state)
        => !string.Equals(state, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(state, "no_changed_files", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(state, "finalized", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(state, "applied", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(state, "not_recorded", StringComparison.OrdinalIgnoreCase);

    private static bool IsOrchestrationBlocked(BuilderCrossRepoOrchestrationContext orchestration)
        => string.Equals(orchestration.ExecutionState.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(orchestration.ExecutionState.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase) ||
           orchestration.ExecutionState.WorkspaceStatusList.Any(IsBlockingWorkspace);

    private static bool IsBlockingWorkspace(BuilderCrossRepoWorkspaceStatusRecord status)
        => status.RejectedSegment ||
           string.Equals(status.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase);

    private static bool HasCrossRepoDependencyBlock(string workspaceId, IReadOnlyList<string> workspaceOrder, IReadOnlyList<string> blockingWorkspaceIds)
    {
        if (workspaceOrder.Count <= 1 || blockingWorkspaceIds.Count == 0)
        {
            return false;
        }

        var workspaceIndex = IndexOfWorkspace(workspaceOrder, workspaceId);
        return blockingWorkspaceIds.Any(blockingWorkspaceId =>
        {
            var blockingIndex = IndexOfWorkspace(workspaceOrder, blockingWorkspaceId);
            return blockingIndex >= 0 && workspaceIndex >= 0 && (blockingIndex < workspaceIndex || string.Equals(blockingWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string BuildDependencyEvidence(string workspaceId, IReadOnlyList<string> workspaceOrder, IReadOnlyList<string> blockingWorkspaceIds)
    {
        var workspaceIndex = IndexOfWorkspace(workspaceOrder, workspaceId);
        var upstreamBlockers = blockingWorkspaceIds
            .Where(blockingWorkspaceId => IndexOfWorkspace(workspaceOrder, blockingWorkspaceId) <= workspaceIndex)
            .OrderBy(blockingWorkspaceId => IndexOfWorkspace(workspaceOrder, blockingWorkspaceId))
            .ThenBy(blockingWorkspaceId => blockingWorkspaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return upstreamBlockers.Length == 0
            ? $"{workspaceId} participates in a blocked orchestration but no upstream blocker was resolved."
            : $"{workspaceId} depends on upstream recovery in {DescribeWorkspaceList(upstreamBlockers)}.";
    }

    private static int SeverityRank(string severity)
        => severity switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            _ => 3
        };

    private static int IndexOfWorkspace(IReadOnlyList<string> workspaceOrder, string workspaceId)
    {
        for (var index = 0; index < workspaceOrder.Count; index++)
        {
            if (string.Equals(workspaceOrder[index], workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static bool IsRejectedState(string approvalState)
        => string.Equals(approvalState, "rejected", StringComparison.OrdinalIgnoreCase);

    private static bool IsNeedsRevisionState(string approvalState)
        => string.Equals(approvalState, "needs_revision", StringComparison.OrdinalIgnoreCase);

    private static bool IsPendingState(string approvalState)
        => string.Equals(approvalState, "pending_review", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string DescribeWorkspaceList(IReadOnlyList<string> workspaceIds)
        => workspaceIds.Count == 0 ? "no blocking workspaces" : string.Join(", ", workspaceIds);

    private static string BuildWarningId(BuilderRouteRiskWarningEntryRecord warning)
        => ComputeDeterministicId("warning", warning.Workspace, warning.RouteAttempted, warning.WarningReason, warning.RelatedKnowledgeGraphNode);

    private static string ComputeDeterministicId(string prefix, params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"{prefix}-{hash[..10]}";
    }

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

    private sealed record FileStateRecord(string RelativePath, string ApprovalState);

    private sealed record WorkspaceRecoverySnapshot(
        BuilderWorkspaceDescriptor Descriptor,
        BuilderCrossRepoWorkspaceStatusRecord? Status,
        string CurrentRoute,
        string CurrentBlockingState,
        IReadOnlyList<string> WarningIds,
        IReadOnlyList<BuilderFailurePatternRecord> Failures,
        IReadOnlyList<string> RejectedFiles,
        IReadOnlyList<string> RevisionFiles,
        IReadOnlyList<string> PendingFiles,
        IReadOnlyList<string> HighRiskStalledFiles,
        IReadOnlyList<string> SourceRunIds,
        IReadOnlyList<string> ArtifactLinks,
        DateTimeOffset ObservedUtc);
}
