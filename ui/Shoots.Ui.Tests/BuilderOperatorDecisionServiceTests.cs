using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderOperatorDecisionServiceTests
{
    [Fact]
    public void Record_decisions_is_append_only_deduplicated_and_linked_to_playbooks_and_simulations()
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
            var simulations = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));

            Assert.NotNull(recovery);
            Assert.NotNull(simulations);
            var playbook = recovery!.Playbooks.First(playbook => string.Equals(playbook.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
            var simulation = simulations!.Simulations.First(entry => string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase));

            var first = BuilderOperatorDecisionService.RecordDecision(
                repoB,
                new BuilderOperatorDecisionRequest(
                    playbook.PlaybookId,
                    simulation.SimulationId,
                    "launch_override_route",
                    BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                    playbook.AppliesToRoutes.FirstOrDefault() ?? "builder_review_queue",
                    playbook.ArtifactLinks.Concat(simulation.ArtifactLinks).ToArray(),
                    "run-0001",
                    "failed_same_pattern",
                    false,
                    "route_failed",
                    new[]
                    {
                        BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoB),
                        BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB)
                    }),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            var duplicate = BuilderOperatorDecisionService.RecordDecision(
                repoB,
                new BuilderOperatorDecisionRequest(
                    playbook.PlaybookId,
                    simulation.SimulationId,
                    "launch_override_route",
                    BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                    playbook.AppliesToRoutes.FirstOrDefault() ?? "builder_review_queue",
                    playbook.ArtifactLinks.Concat(simulation.ArtifactLinks).ToArray(),
                    "run-0001",
                    "failed_same_pattern",
                    false,
                    "route_failed",
                    new[]
                    {
                        BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoB),
                        BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB)
                    }),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            var second = BuilderOperatorDecisionService.RecordDecision(
                repoB,
                new BuilderOperatorDecisionRequest(
                    playbook.PlaybookId,
                    simulation.SimulationId,
                    "launch_prepared_route",
                    BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                    "prepared_route",
                    playbook.ArtifactLinks.Concat(simulation.ArtifactLinks).ToArray(),
                    "run-0002",
                    "success",
                    true,
                    string.Empty,
                    new[]
                    {
                        BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoB),
                        BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB)
                    }),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

            Assert.Single(first.Decisions);
            Assert.Single(duplicate.Decisions);
            Assert.Equal(2, second.Decisions.Count);
            Assert.Equal(first.Decisions[0].DecisionId, second.Decisions[0].DecisionId);
            Assert.Equal("launch_override_route", second.Decisions[0].ActionTaken);
            Assert.Equal("launch_prepared_route", second.Decisions[1].ActionTaken);
            Assert.Equal(playbook.PlaybookId, second.Decisions[0].PlaybookId);
            Assert.Equal(simulation.SimulationId, second.Decisions[0].SimulationId);
            Assert.True(File.Exists(BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoB)));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Recording_decisions_does_not_change_route_resolution_or_finalize_state()
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
            BuilderRecoverySimulationService.RefreshRecoverySimulations(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderOperatorDecisionService.RecordDecision(
                repoB,
                new BuilderOperatorDecisionRequest(
                    "playbook-1",
                    "simulation-1",
                    "approve_pending_in_group",
                    BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                    beforeRoute?.RouteDecision ?? "builder_review_queue",
                    new[]
                    {
                        BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoB),
                        BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoB)
                    },
                    beforeApply?.SessionId ?? "session-b",
                    "partial_success",
                    true,
                    "review_blocked",
                    new[] { BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoB) }),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));

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
}

public sealed class MainWindowViewModelBuilderOperatorDecisionTests
{
    [Fact]
    public async Task Decision_timeline_records_review_actions_with_playbook_and_simulation_links()
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

            Assert.True(viewModel.HasBuilderRecoveryPlaybooks);
            var playbook = viewModel.BuilderRecoveryPlaybooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(playbook);
            var simulation = viewModel.BuilderRecoverySimulations.First();
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(simulation);

            if (!viewModel.HasBuilderReviewCurrentFile)
            {
                await viewModel.SelectBuilderReviewFileCommand.ExecuteAsync(viewModel.BuilderReviewGroups.First().Files.First());
            }

            await viewModel.ApprovePendingBuilderReviewGroupCommand.ExecuteAsync();

            Assert.True(viewModel.HasBuilderOperatorDecisions);
            var latest = viewModel.BuilderOperatorDecisionRows.Last();
            Assert.Equal("approve_pending_in_group", latest.Decision.ActionTaken);
            Assert.Equal(playbook.PlaybookId, latest.Decision.PlaybookId);
            Assert.Equal(simulation.SimulationId, latest.Decision.SimulationId);
            Assert.True(viewModel.HasBuilderOperatorDecisionSelectedOutcome);
            Assert.True(viewModel.HasBuilderOperatorDecisionTriggerArtifacts);
            Assert.True(viewModel.HasBuilderOperatorDecisionResultArtifacts);

            await viewModel.OpenBuilderOperatorDecisionArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderOperatorDecisionArtifactLinkCommand.ExecuteAsync(viewModel.BuilderOperatorDecisionResultArtifacts.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
