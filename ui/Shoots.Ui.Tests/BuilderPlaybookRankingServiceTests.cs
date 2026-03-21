using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderPlaybookRankingServiceTests
{
    [Fact]
    public void Refresh_playbook_rankings_generates_deterministic_scores_and_orders_higher_evidence_playbooks_first()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedRankedRecoveryState(repoA, repoB);

            var first = BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(14));
            var firstJson = File.ReadAllText(BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoB));
            var second = BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(14));
            var secondJson = File.ReadAllText(BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(seeded.Playbooks!.Playbooks.Count, first!.Rankings.Count);

            var patchRejected = Assert.Single(first.Rankings, entry => string.Equals(entry.PlaybookId, seeded.PatchRejected.PlaybookId, StringComparison.OrdinalIgnoreCase));
            var routeFailed = Assert.Single(first.Rankings, entry => string.Equals(entry.PlaybookId, seeded.RouteFailed.PlaybookId, StringComparison.OrdinalIgnoreCase));

            Assert.True(patchRejected.RankingScore > routeFailed.RankingScore);
            Assert.True(patchRejected.RankingPosition < routeFailed.RankingPosition);
            Assert.True(patchRejected.OutcomeSuccessRate > routeFailed.OutcomeSuccessRate);
            Assert.Contains(BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoB), patchRejected.EvidenceLinks);
            Assert.Contains(BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoB), patchRejected.EvidenceLinks);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Playbook_rankings_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedRankedRecoveryState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.ApplyEligibilityState, afterApply!.ApplyEligibilityState);
            Assert.Equal(beforeApply.FinalizationState, afterApply.FinalizationState);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    private static RankedRecoverySeed SeedRankedRecoveryState(string repoA, string repoB)
    {
        var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
        var playbooks = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
        var simulations = BuilderRecoverySimulationService.RefreshRecoverySimulations(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));

        Assert.NotNull(playbooks);
        Assert.NotNull(simulations);
        var patchRejected = playbooks!.Playbooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
        var routeFailed = playbooks.Playbooks.First(entry => string.Equals(entry.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
        var patchSimulation = simulations!.Simulations.First(entry =>
            string.Equals(entry.PlaybookId, patchRejected.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
        var routeSimulation = simulations.Simulations.First(entry =>
            string.Equals(entry.PlaybookId, routeFailed.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase));

        RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-1001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
        RecordDecision(repoB, patchSimulation, "approve_pending_in_directory", "run-1002", "partial_success", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
        RecordDecision(repoB, routeSimulation, "launch_override_route", "run-1003", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));
        Assert.NotNull(accuracy);

        return new RankedRecoverySeed(playbooks, simulations, accuracy!, patchRejected, routeFailed);
    }

    internal static void RecordDecision(
        string repoRoot,
        BuilderRecoverySimulationRecord simulation,
        string actionTaken,
        string resultRunId,
        string resultState,
        bool successFlag,
        string failureClass,
        DateTimeOffset observedUtc)
    {
        BuilderOperatorDecisionService.RecordDecision(
            repoRoot,
            new BuilderOperatorDecisionRequest(
                simulation.PlaybookId,
                simulation.SimulationId,
                actionTaken,
                BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
                simulation.TargetRoute,
                new[]
                {
                    BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                    BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot)
                },
                resultRunId,
                resultState,
                successFlag,
                failureClass,
                new[]
                {
                    BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                    BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot)
                },
                simulation.Scenario,
                simulation.PredictedOutcome,
                simulation.PredictedOutcomeClass,
                simulation.ConfidenceLevel,
                simulation.ConfidenceScore),
            observedUtc);
    }

    private sealed record RankedRecoverySeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderSimulationAccuracyReport Accuracy,
        BuilderRecoveryPlaybookRecord PatchRejected,
        BuilderRecoveryPlaybookRecord RouteFailed);
}

public sealed class MainWindowViewModelBuilderPlaybookRankingTests
{
    [Fact]
    public async Task Builder_recovery_panel_displays_ranked_order_and_explainable_breakdown()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            var descriptors = new[]
            {
                BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
                BuilderWorkspaceService.CreateDescriptor(repoB, "host-b")
            };
            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));
            Assert.NotNull(orchestration);
            var playbooks = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                descriptors,
                orchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
            var simulations = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                descriptors,
                orchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            Assert.NotNull(playbooks);
            Assert.NotNull(simulations);

            var patchRejected = playbooks!.Playbooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
            var routeFailed = playbooks.Playbooks.First(entry => string.Equals(entry.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
            var patchSimulation = simulations!.Simulations.First(entry =>
                string.Equals(entry.PlaybookId, patchRejected.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
            var routeSimulation = simulations.Simulations.First(entry =>
                string.Equals(entry.PlaybookId, routeFailed.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase));

            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-2001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, routeSimulation, "launch_override_route", "run-2002", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
            BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

            var workspaceProvider = new MultiWorkspaceProvider(
                new ProjectWorkspace("runtime-a", repoA, BuilderWorkspaceTestData.ObservedUtc, ProjectId: "runtime-a"),
                new ProjectWorkspace("host-b", repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1), ProjectId: "host-b"));
            var scanner = BuilderWorkspaceTestData.CreateScanner(
                repoA,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));
            scanner.AddObservations(
                repoB,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));

            var viewModel = BuilderWorkspaceTestData.CreateViewModel(repoA, workspaceProvider, scanner);
            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.True(viewModel.HasBuilderRecoveryRankingSummary);
            Assert.True(viewModel.HasBuilderRecoveryRankingArtifactPath);
            Assert.Contains("#1", viewModel.BuilderRecoveryPlaybooks.First().RankingBadge, StringComparison.OrdinalIgnoreCase);

            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(viewModel.BuilderRecoveryPlaybooks.First());

            Assert.True(viewModel.HasBuilderRecoverySelectedRankingSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedRankingBreakdown);
            Assert.True(viewModel.HasBuilderRecoverySelectedRankingIndicator);

            await viewModel.OpenBuilderRecoveryRankingArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
