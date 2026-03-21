using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderRecoverySimulationRecord(
    string SimulationId,
    string PlaybookId,
    string Scenario,
    string FailureClass,
    string TargetRoute,
    string PredictedOutcome,
    string PredictedOutcomeClass,
    string SuccessLikelihood,
    string FailureLikelihood,
    string ConfidenceLevel,
    double ConfidenceScore,
    string RiskEscalation,
    IReadOnlyList<string> RiskFlags,
    string ConstraintCompatibility,
    IReadOnlyList<string> BlockedByConstraints,
    string ConstraintReason,
    IReadOnlyList<string> ExpectedStateChanges,
    IReadOnlyList<string> BlockingConditions,
    string ExpectedNextBlockingGate,
    IReadOnlyList<string> ArtifactLinks,
    string ReasoningSummary,
    DateTimeOffset ObservedUtc)
{
    public string Summary
        => $"{FormatToken(Scenario)} predicts {PredictedOutcome} Success: {FormatToken(SuccessLikelihood)}. Failure: {FormatToken(FailureLikelihood)}. Next gate: {FormatToken(ExpectedNextBlockingGate)}.";

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderRecoverySimulationsRecord(
    string WorkspaceId,
    string SchemaVersion,
    string ActiveConstraintProfileId,
    IReadOnlyList<string> SourcePlaybookIds,
    IReadOnlyList<BuilderRecoverySimulationRecord> Simulations,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderRecoverySimulationService
{
    public const string RecoverySimulationsFileName = "builder_recovery_simulations.json";

    private const string SchemaVersion = "builder_recovery_simulations.v2";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string RecoverySimulationsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), RecoverySimulationsFileName);

    public static BuilderRecoverySimulationsRecord? LoadRecoverySimulations(string repoRoot)
        => Load<BuilderRecoverySimulationsRecord>(RecoverySimulationsPathForRepo(repoRoot));

    public static BuilderRecoverySimulationsRecord? RefreshRecoverySimulations(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        BuilderCrossRepoOrchestrationContext orchestration,
        string activeWorkspaceId,
        string requestId,
        DateTimeOffset? observedUtc = null,
        int maxSimulationsPerPlaybook = 3)
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
        var workspaceStatuses = orchestration.ExecutionState.WorkspaceStatusList
            .ToDictionary(status => status.WorkspaceId, StringComparer.OrdinalIgnoreCase);

        BuilderRecoverySimulationsRecord? activeArtifact = null;
        foreach (var descriptor in descriptors)
        {
            workspaceStatuses.TryGetValue(descriptor.WorkspaceId, out var status);
            var snapshot = BuildSnapshot(descriptor, status, orchestration, requestId);
            var artifact = BuildArtifact(snapshot, effectiveObservedUtc, maxSimulationsPerPlaybook);
            Save(artifact.ArtifactPath, artifact);
            if (string.Equals(descriptor.WorkspaceId, activeDescriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                activeArtifact = artifact;
            }
        }

        return activeArtifact ?? LoadRecoverySimulations(activeDescriptor.RepoRootPath);
    }

    public static BuilderRecoverySimulationsRecord? RefreshRecoverySimulations(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        DateTimeOffset? observedUtc = null,
        int maxSimulationsPerPlaybook = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        if (playbooks is null)
        {
            return null;
        }

        var descriptor = BuilderWorkspaceService.CreateDescriptor(repoRoot, Path.GetFileName(repoRoot));
        var executionState = BuilderCrossRepoOrchestrationService.LoadExecutionState(repoRoot);
        var status = executionState?.WorkspaceStatusList.FirstOrDefault(entry =>
            string.Equals(entry.WorkspaceId, descriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase));
        var snapshot = BuildSnapshot(
            descriptor,
            status,
            playbooks.SourceRunIds.FirstOrDefault() ?? descriptor.WorkspaceId,
            playbooks,
            executionState);
        var artifact = BuildArtifact(snapshot, observedUtc ?? DateTimeOffset.UtcNow, maxSimulationsPerPlaybook);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    private static WorkspaceSimulationSnapshot BuildSnapshot(
        BuilderWorkspaceDescriptor descriptor,
        BuilderCrossRepoWorkspaceStatusRecord? status,
        BuilderCrossRepoOrchestrationContext orchestration,
        string requestId)
        => BuildSnapshot(
            descriptor,
            status,
            requestId,
            BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(descriptor.RepoRootPath),
            orchestration.ExecutionState);

    private static WorkspaceSimulationSnapshot BuildSnapshot(
        BuilderWorkspaceDescriptor descriptor,
        BuilderCrossRepoWorkspaceStatusRecord? status,
        string requestId,
        BuilderRecoveryPlaybooksRecord? playbooks,
        BuilderCrossRepoExecutionStateRecord? executionState)
    {
        var repoRoot = descriptor.RepoRootPath;
        var routeRecommendations = BuilderRouteIntelligenceService.LoadRouteRecommendations(repoRoot);
        var routeWarnings = BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        var orchestrationRecommendations = BuilderRouteIntelligenceService.LoadOrchestrationRecommendations(repoRoot);
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(repoRoot);
        var highRiskFlags = BuilderReviewWorkspaceService.LoadHighRiskFileFlags(repoRoot);
        var failurePatterns = BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
        var executionPatterns = BuilderKnowledgeGraphService.LoadExecutionPatterns(repoRoot);
        var constraints = BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        var activeConstraintProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);

        var currentRoute = status?.RouteDecision
                           ?? routeResolution?.RouteDecision
                           ?? playbooks?.Playbooks.FirstOrDefault()?.AppliesToRoutes.FirstOrDefault()
                           ?? "not_recorded";
        var currentModelTier = status?.ModelTier
                               ?? routeRecommendations?.ModelTierSuggestions.FirstOrDefault()
                               ?? "not_recorded";
        var pendingFiles = reviewWorkspace?.ReviewCounts.PendingFiles ?? status?.PendingReviews ?? 0;
        var rejectedFiles = reviewWorkspace?.ReviewCounts.RejectedFiles ?? (status?.RejectedSegment == true ? 1 : 0);
        var revisionFiles = reviewWorkspace?.ReviewCounts.NeedsRevisionFiles ?? 0;
        var changedFiles = reviewWorkspace?.ReviewCounts.TotalChangedFiles ?? status?.ChangedFiles ?? 0;
        var highRiskFiles = highRiskFlags?.Entries.Count(entry => entry.RequiresExplicitApproval) ?? 0;
        var blockingRepoIds = executionState?.WorkspaceStatusList
            .Where(IsBlockingWorkspace)
            .Select(entry => entry.WorkspaceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        var artifactLinks = (playbooks?.ArtifactLinks ?? Array.Empty<string>())
            .Concat(new[]
            {
                BuilderRouteIntelligenceService.RouteRecommendationsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.OrchestrationRecommendationsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.ExecutionPatternsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoRoot),
                BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot),
                BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoPlanPathForRepo(repoRoot),
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                RecoverySimulationsPathForRepo(repoRoot)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkspaceSimulationSnapshot(
            descriptor,
            requestId,
            currentRoute,
            currentModelTier,
            status?.FinalizeReadiness ?? reviewWorkspace?.ReviewCounts.FinalizeEligibilityState ?? "not_recorded",
            pendingFiles,
            rejectedFiles,
            revisionFiles,
            highRiskFiles,
            changedFiles,
            blockingRepoIds,
            activeConstraintProfile?.ProfileId ?? string.Empty,
            constraints,
            playbooks,
            routeRecommendations,
            routeWarnings,
            orchestrationRecommendations,
            failurePatterns,
            executionPatterns,
            artifactLinks);
    }

    private static BuilderRecoverySimulationsRecord BuildArtifact(
        WorkspaceSimulationSnapshot snapshot,
        DateTimeOffset observedUtc,
        int maxSimulationsPerPlaybook)
    {
        var playbooks = snapshot.Playbooks?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>();
        var playbookOrder = playbooks
            .Select((playbook, index) => new { playbook.PlaybookId, Index = index, SimulationOrder = playbook.SimulationIds.Select((id, simulationIndex) => new { id, simulationIndex }).ToDictionary(pair => pair.id, pair => pair.simulationIndex, StringComparer.OrdinalIgnoreCase) })
            .ToDictionary(
                entry => entry.PlaybookId,
                entry => (entry.Index, entry.SimulationOrder),
                StringComparer.OrdinalIgnoreCase);
        var simulations = playbooks
            .SelectMany(playbook => BuildSimulationsForPlaybook(snapshot, playbook, observedUtc, maxSimulationsPerPlaybook))
            .OrderBy(simulation => playbookOrder[simulation.PlaybookId].Index)
            .ThenBy(simulation => playbookOrder[simulation.PlaybookId].SimulationOrder.TryGetValue(simulation.SimulationId, out var index) ? index : int.MaxValue)
            .ThenBy(simulation => simulation.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderRecoverySimulationsRecord(
            snapshot.Descriptor.WorkspaceId,
            SchemaVersion,
            snapshot.ActiveConstraintProfileId,
            playbooks.Select(playbook => playbook.PlaybookId).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            simulations,
            true,
            BuildSummary(snapshot.Descriptor.WorkspaceId, snapshot.ActiveConstraintProfileId, playbooks.Count, simulations.Length, simulations.Count(entry => string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))),
            RecoverySimulationsPathForRepo(snapshot.Descriptor.RepoRootPath),
            observedUtc);
    }

    private static IReadOnlyList<BuilderRecoverySimulationRecord> BuildSimulationsForPlaybook(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook,
        DateTimeOffset observedUtc,
        int maxSimulationsPerPlaybook)
    {
        var scenarios = BuilderRecoveryPlaybookService.ResolveSimulationScenarios(playbook.FailureClass)
            .Take(Math.Max(maxSimulationsPerPlaybook, 0))
            .ToArray();
        var simulationIds = playbook.SimulationIds.Count == scenarios.Length
            ? playbook.SimulationIds
            : BuilderRecoveryPlaybookService.ResolveSimulationIds(playbook.PlaybookId, playbook.FailureClass)
                .Take(scenarios.Length)
                .ToArray();

        var currentRoute = playbook.AppliesToRoutes.FirstOrDefault() ?? snapshot.CurrentRoute;
        var currentRecommendation = FindRecommendation(snapshot.RouteRecommendations, currentRoute);
        var alternateRecommendation = snapshot.RouteRecommendations?.RecommendedRoutes
            .Where(entry => !string.Equals(entry.Route, currentRoute, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.HistoricalSuccessRate)
            .ThenByDescending(entry => entry.SuccessCount)
            .ThenBy(entry => entry.FailureCount)
            .ThenBy(entry => entry.Route, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var sameRouteFailureCount = snapshot.FailurePatterns?.Entries.Count(entry =>
            string.Equals(entry.Workspace, snapshot.Descriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.RouteAttempted, currentRoute, StringComparison.OrdinalIgnoreCase)) ?? 0;
        var sameRouteWarningCount = snapshot.RouteWarnings?.Entries.Count(entry =>
            string.Equals(entry.RouteAttempted, currentRoute, StringComparison.OrdinalIgnoreCase)) ?? 0;

        var simulations = new List<BuilderRecoverySimulationRecord>(scenarios.Length);
        for (var index = 0; index < scenarios.Length; index++)
        {
            var scenario = scenarios[index];
            var simulationId = simulationIds[index];
            simulations.Add(BuildSimulation(
                snapshot,
                playbook,
                scenario,
                simulationId,
                currentRoute,
                currentRecommendation,
                alternateRecommendation,
                sameRouteFailureCount,
                sameRouteWarningCount,
                observedUtc));
        }

        return simulations;
    }

    private static BuilderRecoverySimulationRecord BuildSimulation(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook,
        string scenario,
        string simulationId,
        string currentRoute,
        BuilderRouteRecommendationEntryRecord? currentRecommendation,
        BuilderRouteRecommendationEntryRecord? alternateRecommendation,
        int sameRouteFailureCount,
        int sameRouteWarningCount,
        DateTimeOffset observedUtc)
    {
        var scenarioData = scenario switch
        {
            "retry_same_route" => BuildRetrySameRouteProjection(snapshot, playbook, currentRoute, currentRecommendation, sameRouteFailureCount, sameRouteWarningCount),
            "switch_route_manual" => BuildSwitchRouteProjection(snapshot, playbook, currentRoute, currentRecommendation, alternateRecommendation, sameRouteFailureCount, sameRouteWarningCount),
            "reduce_scope" => BuildReduceScopeProjection(snapshot, playbook),
            "staged_orchestration" => BuildStagedOrchestrationProjection(snapshot, playbook),
            "isolate_high_risk_files" => BuildIsolateHighRiskProjection(snapshot, playbook),
            _ => BuildReduceScopeProjection(snapshot, playbook)
        };
        var baseSimulation = new BuilderRecoverySimulationRecord(
            simulationId,
            playbook.PlaybookId,
            scenario,
            playbook.FailureClass,
            currentRoute,
            scenarioData.PredictedOutcome,
            scenarioData.PredictedOutcomeClass,
            scenarioData.SuccessLikelihood,
            scenarioData.FailureLikelihood,
            scenarioData.ConfidenceLevel,
            ConfidenceScoreFromLevel(scenarioData.ConfidenceLevel),
            scenarioData.RiskEscalation,
            scenarioData.RiskFlags,
            "compatible",
            Array.Empty<string>(),
            "No explicit operator constraint blocks this what-if scenario.",
            scenarioData.ExpectedStateChanges,
            scenarioData.BlockingConditions,
            scenarioData.ExpectedNextBlockingGate,
            playbook.ArtifactLinks
                .Concat(snapshot.ArtifactLinks)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            scenarioData.ReasoningSummary,
            observedUtc);
        var constraintEvaluation = BuilderOperatorConstraintService.EvaluateSimulationConstraints(
            baseSimulation,
            playbook,
            snapshot.Constraints,
            snapshot.FinalizeReadiness,
            snapshot.HighRiskFiles > 0,
            snapshot.BlockingRepoIds.Count > 0);

        return baseSimulation with
        {
            ConstraintCompatibility = constraintEvaluation.ConstraintCompatibility,
            BlockedByConstraints = constraintEvaluation.BlockedByConstraints,
            ConstraintReason = constraintEvaluation.ConstraintReason
        };
    }

    private static ScenarioProjection BuildRetrySameRouteProjection(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook,
        string currentRoute,
        BuilderRouteRecommendationEntryRecord? currentRecommendation,
        int sameRouteFailureCount,
        int sameRouteWarningCount)
    {
        var successRate = currentRecommendation?.HistoricalSuccessRate ?? (sameRouteFailureCount == 0 && sameRouteWarningCount == 0 ? 45d : 15d);
        var failureRate = Math.Max(currentRecommendation?.HistoricalFailureRate ?? 0d, sameRouteFailureCount * 25d + sameRouteWarningCount * 20d);
        var expectedGate = sameRouteWarningCount > 0 || sameRouteFailureCount > 0
            ? "routing_policy"
            : NextReviewGate(snapshot);
        var riskFlags = BuildRiskFlags(
            sameRouteFailureCount >= 2 ? "repeated_route_failure" : string.Empty,
            sameRouteWarningCount > 0 ? "active_route_warning" : string.Empty,
            snapshot.RejectedFiles > 0 ? "rejected_files_present" : string.Empty,
            snapshot.RevisionFiles > 0 ? "revision_gate_present" : string.Empty);
        var predictedOutcome = sameRouteFailureCount > 0 || sameRouteWarningCount > 0
            ? $"Retrying {currentRoute} is likely to repeat the current blocked path until the recorded route warnings and failure evidence are addressed."
            : $"Retrying {currentRoute} is likely to return the workspace to supervised review, where the normal review and finalize gates still apply.";
        var expectedStateChanges = new[]
        {
            $"Route focus would remain on {currentRoute}.",
            $"Projected next blocking gate remains {FormatToken(expectedGate)}.",
            "Approval and finalize state would remain unchanged until a new supervised run is reviewed."
        };
        var blockingConditions = BuildBlockingConditions(
            sameRouteWarningCount > 0 ? "route_warnings_remain_active" : string.Empty,
            sameRouteFailureCount > 0 ? "recorded_failure_cause_not_cleared" : string.Empty,
            snapshot.RejectedFiles > 0 ? "rejected_files_still_block_review" : string.Empty,
            snapshot.RevisionFiles > 0 ? "revision_requests_still_block_finalize" : string.Empty);

        return new ScenarioProjection(
            predictedOutcome,
            sameRouteFailureCount > 0 || sameRouteWarningCount > 0 ? "failed_same_pattern" : "partial_success",
            LikelihoodFromRate(successRate),
            LikelihoodFromRate(failureRate),
            DetermineConfidenceLevel(playbook, currentRecommendation is not null, sameRouteFailureCount, sameRouteWarningCount),
            sameRouteFailureCount >= 2 ? "high" : sameRouteWarningCount > 0 ? "medium" : "low",
            riskFlags,
            expectedStateChanges,
            blockingConditions,
            expectedGate,
            $"Current route {currentRoute} has {sameRouteFailureCount} failure record(s) and {sameRouteWarningCount} active warning(s).");
    }

    private static ScenarioProjection BuildSwitchRouteProjection(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook,
        string currentRoute,
        BuilderRouteRecommendationEntryRecord? currentRecommendation,
        BuilderRouteRecommendationEntryRecord? alternateRecommendation,
        int sameRouteFailureCount,
        int sameRouteWarningCount)
    {
        var alternateRoute = alternateRecommendation?.Route ?? "manual_alternate_route";
        var successRate = alternateRecommendation?.HistoricalSuccessRate ?? (sameRouteFailureCount > 0 || sameRouteWarningCount > 0 ? 55d : 35d);
        var failureRate = alternateRecommendation?.HistoricalFailureRate ?? (alternateRecommendation is null ? 35d : 20d);
        var predictedOutcome = alternateRecommendation is null
            ? "No historically better alternate route is recorded, so a manual route switch would trade known failure evidence for lower-confidence review guidance."
            : $"Manual switch to {alternateRoute} has a stronger historical success profile than {currentRoute}, but it still leads back into supervised review and approval gates.";
        var expectedStateChanges = new[]
        {
            $"Operator attention would move from {currentRoute} to {alternateRoute}.",
            "Route selection would still require explicit operator confirmation.",
            $"Projected next blocking gate remains {FormatToken(NextReviewGate(snapshot))} after any rerun."
        };
        var blockingConditions = BuildBlockingConditions(
            "routing_policy_requires_explicit_operator_choice",
            snapshot.RejectedFiles > 0 ? "rejected_files_still_require_review" : string.Empty,
            alternateRecommendation is null ? "alternate_route_history_is_sparse" : string.Empty);
        var riskFlags = BuildRiskFlags(
            sameRouteFailureCount > 0 ? "current_route_has_failure_history" : string.Empty,
            sameRouteWarningCount > 0 ? "current_route_has_active_warnings" : string.Empty,
            alternateRecommendation is null ? "alternate_route_not_proven" : string.Empty,
            alternateRecommendation is not null && alternateRecommendation.FailureCount > alternateRecommendation.SuccessCount ? "alternate_route_has_material_failures" : string.Empty);

        return new ScenarioProjection(
            predictedOutcome,
            successRate >= failureRate ? "partial_success" : "new_failure_pattern",
            LikelihoodFromRate(successRate),
            LikelihoodFromRate(failureRate),
            DetermineConfidenceLevel(playbook, alternateRecommendation is not null, sameRouteFailureCount, sameRouteWarningCount),
            alternateRecommendation is null ? "medium" : successRate >= (currentRecommendation?.HistoricalSuccessRate ?? 0d) ? "low" : "medium",
            riskFlags,
            expectedStateChanges,
            blockingConditions,
            "routing_policy",
            alternateRecommendation is null
                ? "The knowledge graph does not record a clearly superior alternate route for this workspace."
                : $"Alternate route {alternateRoute} shows success {alternateRecommendation.HistoricalSuccessRate:0.##}% versus current {currentRecommendation?.HistoricalSuccessRate ?? 0d:0.##}%.");
    }

    private static ScenarioProjection BuildReduceScopeProjection(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook)
    {
        var blockerCount = snapshot.RejectedFiles + snapshot.RevisionFiles + snapshot.HighRiskFiles + snapshot.PendingFiles;
        var successRate = blockerCount > 0 ? 72d : 38d;
        var failureRate = blockerCount > 0 ? 24d : 45d;
        var expectedGate = NextReviewGate(snapshot);
        var predictedOutcome = blockerCount > 0
            ? $"Reducing scope is likely to shrink the review surface around the {blockerCount} explicit blocker(s) without changing current approval or finalize authority."
            : "Reducing scope may simplify the next supervised run, but the current artifacts do not identify a strong blocker cluster.";
        var expectedStateChanges = new[]
        {
            $"Projected changed-file set shrinks from {snapshot.ChangedFiles} file(s) to the blocking subset.",
            $"Projected next blocking gate remains {FormatToken(expectedGate)}.",
            "Current workspace artifacts remain unchanged until the operator chooses a new supervised run."
        };
        var blockingConditions = BuildBlockingConditions(
            snapshot.RejectedFiles > 0 ? "rejected_scope_must_be_reviewed_explicitly" : string.Empty,
            snapshot.RevisionFiles > 0 ? "revision_scope_still_blocks_finalize" : string.Empty,
            snapshot.PendingFiles > 0 ? "pending_scope_still_requires_review" : string.Empty);
        var riskFlags = BuildRiskFlags(
            snapshot.HighRiskFiles > 0 ? "high_risk_files_remain_in_scope" : string.Empty,
            snapshot.BlockingRepoIds.Count > 1 ? "multiple_repo_blockers_exist" : string.Empty);

        return new ScenarioProjection(
            predictedOutcome,
            "partial_success",
            LikelihoodFromRate(successRate),
            LikelihoodFromRate(failureRate),
            DetermineConfidenceLevel(playbook, blockerCount > 0, snapshot.RejectedFiles, snapshot.HighRiskFiles),
            snapshot.HighRiskFiles > 0 ? "medium" : "low",
            riskFlags,
            expectedStateChanges,
            blockingConditions,
            expectedGate,
            $"Scope reduction is anchored to {blockerCount} explicit blocker(s) in review and high-risk artifacts.");
    }

    private static ScenarioProjection BuildStagedOrchestrationProjection(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook)
    {
        var blockingCount = snapshot.BlockingRepoIds.Count;
        var recommendedSequence = snapshot.OrchestrationRecommendations?.RecommendedSequenceSummary ?? "no recorded sequence";
        var successRate = blockingCount switch
        {
            0 => 35d,
            1 => 78d,
            _ => 58d
        };
        var failureRate = blockingCount switch
        {
            0 => 45d,
            1 => 22d,
            _ => 34d
        };
        var predictedOutcome = blockingCount == 0
            ? "Staged orchestration recovery has limited evidence because no current blocking repo is recorded."
            : $"Staging recovery repo-by-repo is likely to preserve unaffected workspace state while focusing on {blockingCount} blocking repo(s).";
        var expectedStateChanges = new[]
        {
            $"Recovery order would follow {recommendedSequence}.",
            "Unaffected repos are expected to remain unchanged and independently auditable.",
            $"Projected next blocking gate remains {FormatToken(NextCrossRepoGate(snapshot))} on the blocking repo set."
        };
        var blockingConditions = BuildBlockingConditions(
            blockingCount == 0 ? "no_current_blocking_repo_recorded" : string.Empty,
            "per_repo_review_and_finalize_gates_remain_independent",
            snapshot.RejectedFiles > 0 ? "active_repo_rejections_still_block_progress" : string.Empty);
        var riskFlags = BuildRiskFlags(
            blockingCount > 1 ? "multiple_blocking_repos" : string.Empty,
            snapshot.FinalizeReadiness.Contains("rejection", StringComparison.OrdinalIgnoreCase) ? "orchestration_blocked_by_rejection" : string.Empty,
            snapshot.FinalizeReadiness.Contains("revision", StringComparison.OrdinalIgnoreCase) ? "orchestration_blocked_by_revision" : string.Empty);

        return new ScenarioProjection(
            predictedOutcome,
            blockingCount == 1 ? "resolved_block" : "partial_success",
            LikelihoodFromRate(successRate),
            LikelihoodFromRate(failureRate),
            DetermineConfidenceLevel(playbook, snapshot.OrchestrationRecommendations is not null, blockingCount, snapshot.PendingFiles),
            blockingCount > 1 ? "medium" : "low",
            riskFlags,
            expectedStateChanges,
            blockingConditions,
            NextCrossRepoGate(snapshot),
            $"Cross-repo execution currently records {blockingCount} blocking repo(s) and recommends {recommendedSequence}.");
    }

    private static ScenarioProjection BuildIsolateHighRiskProjection(
        WorkspaceSimulationSnapshot snapshot,
        BuilderRecoveryPlaybookRecord playbook)
    {
        var successRate = snapshot.HighRiskFiles > 0 ? 82d : 28d;
        var failureRate = snapshot.HighRiskFiles > 0 ? 18d : 42d;
        var predictedOutcome = snapshot.HighRiskFiles > 0
            ? $"Isolating the {snapshot.HighRiskFiles} high-risk file(s) is likely to improve review focus while keeping lower-risk work separate."
            : "The current workspace does not record a strong high-risk cluster, so isolation would provide limited additional clarity.";
        var expectedStateChanges = new[]
        {
            $"High-risk subset remains the focus for explicit approval in {snapshot.Descriptor.WorkspaceId}.",
            "Lower-risk files can remain out of the immediate recovery scope.",
            "Finalize remains blocked until the isolated high-risk cluster receives explicit review decisions."
        };
        var blockingConditions = BuildBlockingConditions(
            "high_risk_files_require_explicit_approval",
            snapshot.RejectedFiles > 0 ? "rejected_files_still_block_finalize" : string.Empty,
            snapshot.RevisionFiles > 0 ? "revision_requests_remain_open" : string.Empty);
        var riskFlags = BuildRiskFlags(
            snapshot.HighRiskFiles > 0 ? "high_risk_change_cluster" : string.Empty,
            snapshot.ChangedFiles > snapshot.HighRiskFiles && snapshot.HighRiskFiles > 0 ? "mixed_risk_patch_surface" : string.Empty);

        return new ScenarioProjection(
            predictedOutcome,
            snapshot.HighRiskFiles > 0 ? "partial_success" : "new_failure_pattern",
            LikelihoodFromRate(successRate),
            LikelihoodFromRate(failureRate),
            DetermineConfidenceLevel(playbook, snapshot.HighRiskFiles > 0, snapshot.HighRiskFiles, snapshot.RejectedFiles),
            snapshot.HighRiskFiles > 0 ? "low" : "medium",
            riskFlags,
            expectedStateChanges,
            blockingConditions,
            "approval_gate",
            $"High-risk file flags record {snapshot.HighRiskFiles} explicit-approval file(s) for this workspace.");
    }

    private static BuilderRouteRecommendationEntryRecord? FindRecommendation(BuilderRouteRecommendationsRecord? recommendations, string route)
        => recommendations?.RecommendedRoutes.FirstOrDefault(entry =>
            string.Equals(entry.Route, route, StringComparison.OrdinalIgnoreCase));

    private static string LikelihoodFromRate(double rate)
        => rate switch
        {
            >= 75d => "high",
            >= 45d => "medium",
            > 0d => "low",
            _ => "very_low"
        };

    private static double ConfidenceScoreFromLevel(string confidenceLevel)
        => confidenceLevel switch
        {
            "high" => 0.85d,
            "medium" => 0.60d,
            "low" => 0.35d,
            _ => 0.15d
        };

    private static string DetermineConfidenceLevel(BuilderRecoveryPlaybookRecord playbook, bool hasHistoricalRouteData, int signalA, int signalB)
    {
        var evidenceCount = playbook.EvidenceSources.Count + (hasHistoricalRouteData ? 1 : 0) + (signalA > 0 ? 1 : 0) + (signalB > 0 ? 1 : 0);
        return evidenceCount switch
        {
            >= 5 => "high",
            >= 3 => "medium",
            _ => "low"
        };
    }

    private static IReadOnlyList<string> BuildRiskFlags(params string[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BuildBlockingConditions(params string[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NextReviewGate(WorkspaceSimulationSnapshot snapshot)
    {
        if (snapshot.RejectedFiles > 0 || snapshot.PendingFiles > 0)
        {
            return "review_gate";
        }

        if (snapshot.RevisionFiles > 0 || snapshot.HighRiskFiles > 0)
        {
            return "approval_gate";
        }

        return "finalize_gate";
    }

    private static string NextCrossRepoGate(WorkspaceSimulationSnapshot snapshot)
        => snapshot.BlockingRepoIds.Count > 0 ? NextReviewGate(snapshot) : "routing_policy";

    private static string BuildSummary(string workspaceId, string activeConstraintProfileId, int playbookCount, int simulationCount, int blockedSimulationCount)
        => simulationCount == 0
            ? $"No deterministic recovery simulations are currently recorded for {workspaceId}. Analysis remains advisory only."
            : $"Generated {simulationCount} advisory what-if simulation(s) from {playbookCount} recovery playbook(s) for {workspaceId}. Active constraint profile: {(string.IsNullOrWhiteSpace(activeConstraintProfileId) ? "none" : activeConstraintProfileId)}. Blocked by constraints: {blockedSimulationCount}.";

    private static bool IsBlockingWorkspace(BuilderCrossRepoWorkspaceStatusRecord status)
        => status.RejectedSegment ||
           string.Equals(status.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase);

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

    private sealed record ScenarioProjection(
        string PredictedOutcome,
        string PredictedOutcomeClass,
        string SuccessLikelihood,
        string FailureLikelihood,
        string ConfidenceLevel,
        string RiskEscalation,
        IReadOnlyList<string> RiskFlags,
        IReadOnlyList<string> ExpectedStateChanges,
        IReadOnlyList<string> BlockingConditions,
        string ExpectedNextBlockingGate,
        string ReasoningSummary);

    private sealed record WorkspaceSimulationSnapshot(
        BuilderWorkspaceDescriptor Descriptor,
        string RequestId,
        string CurrentRoute,
        string CurrentModelTier,
        string FinalizeReadiness,
        int PendingFiles,
        int RejectedFiles,
        int RevisionFiles,
        int HighRiskFiles,
        int ChangedFiles,
        IReadOnlyList<string> BlockingRepoIds,
        string ActiveConstraintProfileId,
        BuilderOperatorConstraintsRecord? Constraints,
        BuilderRecoveryPlaybooksRecord? Playbooks,
        BuilderRouteRecommendationsRecord? RouteRecommendations,
        BuilderRouteRiskWarningsRecord? RouteWarnings,
        BuilderOrchestrationRecommendationsRecord? OrchestrationRecommendations,
        BuilderFailurePatternsRecord? FailurePatterns,
        BuilderExecutionPatternsRecord? ExecutionPatterns,
        IReadOnlyList<string> ArtifactLinks);
}
