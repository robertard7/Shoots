using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderRecoverySimulationServiceTests
{
    [Fact]
    public void Refresh_recovery_simulations_generates_deterministic_outputs_and_links_to_playbooks()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            var recovery = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));

            Assert.NotNull(recovery);
            var first = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            var firstJson = File.ReadAllText(BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB));
            var second = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            var secondJson = File.ReadAllText(BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(first!.Simulations);
            Assert.Contains(first.Simulations, simulation => string.Equals(simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase));

            foreach (var playbook in recovery!.Playbooks)
            {
                var actualIds = first.Simulations
                    .Where(simulation => string.Equals(simulation.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase))
                    .Select(simulation => simulation.SimulationId)
                    .ToArray();
                Assert.Equal(playbook.SimulationIds.ToArray(), actualIds);
            }
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Recovery_simulations_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderRecoverySimulationService.RefreshRecoverySimulations(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.ApplyEligibilityState, afterApply!.ApplyEligibilityState);
            Assert.Equal(beforeApply.FinalizationState, afterApply.FinalizationState);
            Assert.Equal(beforeApply.BlockReasons.ToArray(), afterApply.BlockReasons.ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderRecoverySimulationTests
{
    [Fact]
    public async Task Builder_recovery_simulation_panel_tracks_selected_playbook_and_artifact_links()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);

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

            Assert.True(viewModel.HasBuilderRecoverySimulations);
            var routePlaybook = viewModel.BuilderRecoveryPlaybooks.First(playbook => string.Equals(playbook.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(routePlaybook);

            Assert.All(viewModel.BuilderRecoverySimulations, simulation => Assert.Equal(routePlaybook.PlaybookId, simulation.PlaybookId));
            Assert.Equal("retry same route", viewModel.BuilderRecoverySimulations.First().ScenarioLabel, ignoreCase: true);

            var selectedSimulation = viewModel.BuilderRecoverySimulations.First();
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(selectedSimulation);

            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationPrediction);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationArtifactLinks);
            Assert.Contains("what-if", viewModel.BuilderRecoverySimulationAdvisoryBanner, StringComparison.OrdinalIgnoreCase);

            await viewModel.OpenBuilderRecoverySimulationArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderRecoverySimulationArtifactLinkCommand.ExecuteAsync(viewModel.BuilderRecoverySelectedSimulationArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
