using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderRecoveryComparisonServiceTests
{
    [Fact]
    public void Refresh_recovery_comparisons_generates_deterministic_metric_sets_and_tradeoffs()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedComparisonState(repoA, repoB);
            var first = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.ContextFilters,
                seeded.Intent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));
            var firstJson = File.ReadAllText(BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoB));
            var second = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.ContextFilters,
                seeded.Intent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));
            var secondJson = File.ReadAllText(BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(first!.ComparisonSets);

            var currentFocus = Assert.Single(first.ComparisonSets, set => string.Equals(set.BranchId, "all_candidates", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(currentFocus.ComparisonMetrics);
            Assert.Contains(currentFocus.Tradeoffs, entry => string.Equals(entry.Dimension, "speed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(currentFocus.Tradeoffs, entry => string.Equals(entry.Dimension, "safety", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(currentFocus.Tradeoffs, entry => string.Equals(entry.Dimension, "scope", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(currentFocus.Tradeoffs, entry => string.Equals(entry.Dimension, "risk", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.ComparisonSets.SelectMany(set => set.ComparisonMetrics), entry =>
                string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.ComparisonSets.SelectMany(set => set.ComparisonMetrics), entry =>
                string.Equals(entry.ConstraintCompatibility, "compatible", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.ComparisonSets.SelectMany(set => set.ComparisonMetrics), entry =>
                entry.ScoreSummary.Contains("Predicted success", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Recovery_comparisons_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedComparisonState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.ContextFilters,
                seeded.Intent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(18));

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

    internal static ComparisonSeed SeedComparisonState(string repoA, string repoB)
    {
        var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
        var playbooks = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
        Assert.NotNull(playbooks);

        var intent = BuilderOperatorIntentService.SetOperatorIntent(
            repoB,
            BuilderOperatorIntentService.SafeRecoveryIntent,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
        BuilderOperatorConstraintService.CreateOrUpdateProfile(
            repoB,
            "Comparison Constraints",
            new[]
            {
                BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockPartialOrchestrationConstraint)
            },
            makeActive: true,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));

        var simulations = BuilderRecoverySimulationService.RefreshRecoverySimulations(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

        Assert.NotNull(simulations);

        var routeFailed = playbooks!.Playbooks.First(entry => string.Equals(entry.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
        var patchRejected = playbooks.Playbooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
        var retrySameRoute = simulations!.Simulations.First(entry =>
            string.Equals(entry.PlaybookId, routeFailed.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase));
        var reduceScope = simulations.Simulations.First(entry =>
            string.Equals(entry.PlaybookId, patchRejected.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
        var stagedOrchestration = simulations.Simulations.First(entry => string.Equals(entry.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase));

        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, retrySameRoute, "launch_override_route", "run-7001", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, reduceScope, "approve_pending_in_filter", "run-7002", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, stagedOrchestration, "launch_prepared_route", "run-7003", "partial_success", true, stagedOrchestration.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(13));

        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(14));
        Assert.NotNull(accuracy);

        var rankings = BuilderPlaybookRankingService.RefreshPlaybookRankings(
            repoB,
            playbooks,
            simulations,
            accuracy,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));
        var decisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoB);
        var contextFilters = BuilderPlaybookContextFilterService.RefreshContextFilters(
            repoB,
            playbooks,
            rankings,
            decisions,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(16));

        Assert.NotNull(rankings);
        Assert.NotNull(decisions);
        Assert.NotNull(contextFilters);
        return new ComparisonSeed(playbooks, simulations, accuracy!, rankings!, decisions!, contextFilters!, intent);
    }

    internal sealed record ComparisonSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderSimulationAccuracyReport Accuracy,
        BuilderPlaybookRankingsRecord Rankings,
        BuilderOperatorDecisionsRecord Decisions,
        BuilderPlaybookContextFiltersRecord ContextFilters,
        BuilderOperatorIntentRecord Intent);
}

public sealed class MainWindowViewModelBuilderRecoveryComparisonTests
{
    [Fact]
    public async Task Builder_recovery_comparison_panel_supports_branch_selection_and_constraint_override()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);

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

            Assert.True(viewModel.HasBuilderRecoveryComparisonSets);
            Assert.True(viewModel.HasBuilderRecoveryComparisonArtifactPath);
            Assert.True(viewModel.HasBuilderRecoverySelectedComparisonScenarios);
            Assert.All(viewModel.BuilderRecoverySelectedComparisonScenarios, entry => Assert.False(entry.IsBlockedByConstraints));

            viewModel.ShowBuilderRecoveryViolatingOptions = true;
            var blockedSet = viewModel.BuilderRecoveryComparisonSets.First(entry => entry.BlockedScenarioCount > 0);
            await viewModel.SelectBuilderRecoveryComparisonSetCommand.ExecuteAsync(blockedSet);

            Assert.Contains(viewModel.BuilderRecoverySelectedComparisonScenarios, entry => entry.IsBlockedByConstraints);
            Assert.True(viewModel.HasBuilderRecoverySelectedComparisonTradeoffs);
            Assert.True(viewModel.HasBuilderRecoverySelectedComparisonArtifactLinks);

            var scenario = viewModel.BuilderRecoverySelectedComparisonScenarios.First();
            await viewModel.FocusBuilderRecoveryComparisonPlaybookCommand.ExecuteAsync(scenario);
            await viewModel.FocusBuilderRecoveryComparisonSimulationCommand.ExecuteAsync(scenario);

            Assert.True(viewModel.HasBuilderRecoverySelectedPlaybook);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulation);

            await viewModel.OpenBuilderRecoveryComparisonArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
