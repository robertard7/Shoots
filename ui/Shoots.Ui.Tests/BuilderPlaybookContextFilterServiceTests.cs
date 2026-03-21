using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderPlaybookContextFilterServiceTests
{
    [Fact]
    public void Refresh_context_filters_generates_deterministic_relevance_scores_and_visibility_modes()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedContextFilterState(repoA, repoB);

            var first = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                seeded.Rankings,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));
            var firstJson = File.ReadAllText(BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoB));
            var second = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                seeded.Rankings,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));
            var secondJson = File.ReadAllText(BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(seeded.Playbooks.Playbooks.Count, first!.RelevanceScores.Count);
            Assert.Equal(seeded.Playbooks.Playbooks.Count, first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowAllModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount);
            Assert.True(first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowRelevantModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount <= seeded.Playbooks.Playbooks.Count);
            Assert.True(first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowHighPriorityOnlyModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount <= first.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowRelevantModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount);
            Assert.Contains(first.RelevanceScores, entry => string.Equals(entry.PriorityBand, "high", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.RelevanceScores, entry => !string.Equals(entry.PriorityBand, "high", StringComparison.OrdinalIgnoreCase));

            var currentFailureMatch = first.RelevanceScores
                .Where(entry => seeded.Playbooks.Playbooks.Any(playbook =>
                    string.Equals(playbook.PlaybookId, entry.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(playbook.FailureClass, first.ContextSnapshot.CurrentFailureClass, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => entry.RelevanceScore)
                .First();
            var nonCurrentFailure = first.RelevanceScores
                .Where(entry => seeded.Playbooks.Playbooks.Any(playbook =>
                    string.Equals(playbook.PlaybookId, entry.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(playbook.FailureClass, first.ContextSnapshot.CurrentFailureClass, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.RelevanceScore)
                .First();

            Assert.True(currentFailureMatch.RelevanceScore >= nonCurrentFailure.RelevanceScore);
            Assert.Contains(BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoB), currentFailureMatch.EvidenceLinks);
            Assert.Contains(BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoB), currentFailureMatch.EvidenceLinks);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Context_filters_do_not_remove_playbooks_or_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedContextFilterState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            var artifact = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                seeded.Rankings,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(16));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(artifact);
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.Equal(seeded.Playbooks.Playbooks.Count, artifact!.Filters.Single(entry => string.Equals(entry.ModeId, BuilderPlaybookContextFilterService.ShowAllModeId, StringComparison.OrdinalIgnoreCase)).VisiblePlaybookCount);
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

    private static ContextFilterSeed SeedContextFilterState(string repoA, string repoB)
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

        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-3001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_directory", "run-3002", "partial_success", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
        BuilderPlaybookRankingServiceTests.RecordDecision(repoB, routeSimulation, "launch_override_route", "run-3003", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));
        Assert.NotNull(accuracy);

        var rankings = BuilderPlaybookRankingService.RefreshPlaybookRankings(
            repoB,
            playbooks,
            simulations,
            accuracy,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(13));
        Assert.NotNull(rankings);

        return new ContextFilterSeed(playbooks, rankings!);
    }

    private sealed record ContextFilterSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderPlaybookRankingsRecord Rankings);
}

public sealed class MainWindowViewModelBuilderPlaybookContextFilterTests
{
    [Fact]
    public async Task Builder_recovery_panel_supports_contextual_narrowing_without_removing_playbooks()
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

            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, patchSimulation, "approve_pending_in_group", "run-4001", "resolved_block", true, patchRejected.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            BuilderPlaybookRankingServiceTests.RecordDecision(repoB, routeSimulation, "launch_override_route", "run-4002", "failed_same_pattern", false, routeFailed.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
            BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));
            BuilderPlaybookRankingService.RefreshPlaybookRankings(
                repoB,
                playbooks,
                simulations,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));

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

            Assert.True(viewModel.HasBuilderRecoveryContextFilterSummary);
            Assert.True(viewModel.HasBuilderRecoveryContextSnapshotSummary);
            Assert.True(viewModel.HasBuilderRecoveryContextFilterArtifactPath);

            var allCount = viewModel.BuilderRecoveryPlaybooks.Count;
            var nonHighPriority = viewModel.BuilderRecoveryPlaybooks.First(entry => !entry.IsHighPriorityContext);

            viewModel.SelectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowHighPriorityOnlyModeId;
            Assert.All(viewModel.BuilderRecoveryPlaybooks, entry => Assert.True(entry.IsHighPriorityContext));
            Assert.DoesNotContain(viewModel.BuilderRecoveryPlaybooks, entry => string.Equals(entry.PlaybookId, nonHighPriority.PlaybookId, StringComparison.OrdinalIgnoreCase));

            viewModel.SelectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowRelevantModeId;
            Assert.All(viewModel.BuilderRecoveryPlaybooks, entry => Assert.True(entry.IsRelevantContext));
            Assert.True(viewModel.BuilderRecoveryPlaybooks.Count <= allCount);

            viewModel.SelectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowAllModeId;
            Assert.Equal(allCount, viewModel.BuilderRecoveryPlaybooks.Count);
            Assert.Contains(viewModel.BuilderRecoveryPlaybooks, entry => string.Equals(entry.PlaybookId, nonHighPriority.PlaybookId, StringComparison.OrdinalIgnoreCase));

            var selected = viewModel.BuilderRecoveryPlaybooks.First();
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(selected);

            Assert.True(viewModel.HasBuilderRecoverySelectedContextualSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedContextReason);
            Assert.True(viewModel.HasBuilderRecoverySelectedContextFlags);
            Assert.All(viewModel.BuilderRecoverySimulations, entry => Assert.Equal(selected.PlaybookId, entry.PlaybookId));

            await viewModel.OpenBuilderRecoveryContextFilterArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
