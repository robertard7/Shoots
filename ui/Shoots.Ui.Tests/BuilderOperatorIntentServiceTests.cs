using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderOperatorIntentServiceTests
{
    [Fact]
    public void Set_operator_intent_writes_deterministic_artifact_for_supported_values()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("intent-a");
        try
        {
            var first = BuilderOperatorIntentService.SetOperatorIntent(
                repoRoot,
                BuilderOperatorIntentService.SafeRecoveryIntent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20));
            var firstJson = File.ReadAllText(BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot));
            var second = BuilderOperatorIntentService.SetOperatorIntent(
                repoRoot,
                BuilderOperatorIntentService.SafeRecoveryIntent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20));
            var secondJson = File.ReadAllText(BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot));

            Assert.Equal(firstJson, secondJson);
            Assert.Equal(BuilderOperatorIntentService.SafeRecoveryIntent, first.Intent);
            Assert.Equal(first.IntentTimestamp, second.IntentTimestamp);
            Assert.True(first.AdvisoryOnly);
            Assert.Contains("Safe Recovery", first.Summary, StringComparison.OrdinalIgnoreCase);

            var loaded = BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
            Assert.NotNull(loaded);
            Assert.Equal(first, loaded);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Intent_alignment_updates_ranking_overlay_without_mutating_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedIntentState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            var withoutIntent = BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(14));
            BuilderOperatorIntentService.SetOperatorIntent(
                repoB,
                BuilderOperatorIntentService.UnblockOrchestrationIntent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));
            var withIntent = BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(16));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            Assert.NotNull(withoutIntent);
            Assert.NotNull(withIntent);
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);

            Assert.Equal(string.Empty, withoutIntent!.SelectedIntent);
            Assert.Equal(BuilderOperatorIntentService.UnblockOrchestrationIntent, withIntent!.SelectedIntent);
            Assert.Contains(withIntent.Rankings, entry => entry.BestForIntents.Contains(BuilderOperatorIntentService.UnblockOrchestrationIntent, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(withIntent.Rankings, entry => !entry.BestForIntents.Contains(BuilderOperatorIntentService.UnblockOrchestrationIntent, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(withIntent.Rankings, entry => entry.IntentAdjustedScore > entry.RankingScore);
            Assert.All(withIntent.Rankings, entry =>
            {
                Assert.True(entry.IntentAdjustedScore >= 0d);
                Assert.True(entry.IntentAdjustedScore <= 100d);
            });
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

    [Fact]
    public void Intent_aligned_context_filters_remain_reversible_and_keep_all_playbooks_accessible()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedIntentState(repoA, repoB);
            BuilderOperatorIntentService.SetOperatorIntent(
                repoB,
                BuilderOperatorIntentService.SafeRecoveryIntent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));
            var rankings = BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(16));
            var first = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                rankings,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));
            var firstJson = File.ReadAllText(BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoB));
            var second = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                rankings,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));
            var secondJson = File.ReadAllText(BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(BuilderOperatorIntentService.SafeRecoveryIntent, first!.ContextSnapshot.OperatorIntent);
            Assert.Contains(first.RelevanceScores, entry => entry.IntentAlignmentScore > 0d);
            Assert.Contains(first.RelevanceScores, entry => entry.ActiveContextFlags.Contains("is_intent_aligned", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(seeded.Playbooks.Playbooks.Count, first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowAllModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount);
            Assert.True(first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowRelevantModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount <= seeded.Playbooks.Playbooks.Count);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    private static IntentSeed SeedIntentState(string repoA, string repoB)
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

        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-5001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_filter", "run-5002", "partial_success", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, routeSimulation, "launch_override_route", "run-5003", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));
        Assert.NotNull(accuracy);

        return new IntentSeed(playbooks, simulations, accuracy!);
    }

    private sealed record IntentSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderSimulationAccuracyReport Accuracy);
}

public sealed class MainWindowViewModelBuilderOperatorIntentTests
{
    [Fact]
    public async Task Builder_recovery_panel_supports_intent_selection_and_goal_aligned_simulation_summary()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
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

            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-6001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, routeSimulation, "launch_override_route", "run-6002", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
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

            Assert.Equal(string.Empty, viewModel.SelectedBuilderRecoveryIntent);
            Assert.Equal(string.Empty, viewModel.BuilderRecoveryIntentOptions.First().Value);
            Assert.False(viewModel.HasBuilderRecoveryIntentArtifactPath);

            viewModel.SelectedBuilderRecoveryIntent = BuilderOperatorIntentService.SafeRecoveryIntent;

            Assert.Equal(BuilderOperatorIntentService.SafeRecoveryIntent, viewModel.SelectedBuilderRecoveryIntent);
            Assert.True(viewModel.HasBuilderRecoveryIntentSummary);
            Assert.True(viewModel.HasBuilderRecoveryIntentArtifactPath);
            Assert.All(viewModel.BuilderRecoveryPlaybooks, entry => Assert.False(string.IsNullOrWhiteSpace(entry.IntentAlignmentBadge)));

            var selectedPlaybook = viewModel.BuilderRecoveryPlaybooks.First();
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(selectedPlaybook);
            var selectedSimulation = viewModel.BuilderRecoverySimulations.First();
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(selectedSimulation);

            Assert.True(viewModel.HasBuilderRecoverySelectedIntentSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedIntentReason);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationIntentSummary);

            viewModel.SelectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowAllModeId;
            Assert.Equal(playbooks.Playbooks.Count, viewModel.BuilderRecoveryPlaybooks.Count);

            await viewModel.OpenBuilderRecoveryIntentArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
