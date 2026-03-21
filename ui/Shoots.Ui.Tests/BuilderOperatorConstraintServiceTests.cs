using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderOperatorConstraintServiceTests
{
    [Fact]
    public void Create_or_update_constraint_profiles_writes_deterministic_artifact()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("constraints-a");
        try
        {
            var observedUtc = BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20);
            var first = BuilderOperatorConstraintService.CreateOrUpdateProfile(
                repoRoot,
                "Strict Recovery",
                new[]
                {
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockHighRiskFilesConstraint),
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint)
                },
                makeActive: true,
                observedUtc: observedUtc);
            var firstJson = File.ReadAllText(BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot));
            var second = BuilderOperatorConstraintService.CreateOrUpdateProfile(
                repoRoot,
                "Strict Recovery",
                new[]
                {
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint),
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockHighRiskFilesConstraint)
                },
                makeActive: true,
                observedUtc: observedUtc);
            var secondJson = File.ReadAllText(BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot));

            Assert.Equal(firstJson, secondJson);
            Assert.Equal(first.ActiveProfileId, second.ActiveProfileId);
            Assert.True(first.AdvisoryOnly);
            var activeProfile = Assert.Single(second.Profiles);
            Assert.Equal("Strict Recovery", activeProfile.ProfileName);
            Assert.Equal(2, activeProfile.Constraints.Count);
            Assert.Contains("hard-bound constraint", second.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Constraint_evaluation_marks_playbooks_and_simulations_without_changing_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedConstraintState(repoA, repoB);
            var blockedRoute = seeded.RouteFailed.AppliesToRoutes.First();
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            var constraints = BuilderOperatorConstraintService.CreateOrUpdateProfile(
                repoB,
                "Strict Recovery",
                new[]
                {
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockSpecificRouteConstraint, blockedRoute, "route"),
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint)
                },
                makeActive: true,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(13));
            var contextFilters = BuilderPlaybookContextFilterService.RefreshContextFilters(
                repoB,
                seeded.Playbooks,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(14));
            var simulations = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                repoB,
                seeded.Playbooks,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(15));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.NotNull(contextFilters);
            Assert.NotNull(simulations);
            Assert.Equal(constraints.ActiveProfileId, contextFilters!.ContextSnapshot.ActiveConstraintProfileId);
            Assert.True(contextFilters.ContextSnapshot.ActiveConstraintCount >= 2);
            Assert.Contains(contextFilters.RelevanceScores, entry =>
                string.Equals(entry.PlaybookId, seeded.RouteFailed.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                entry.ViolatesConstraints &&
                entry.ConstraintReason.Contains(blockedRoute, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(simulations!.Simulations, entry =>
                string.Equals(entry.PlaybookId, seeded.RouteFailed.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) &&
                entry.ConstraintReason.Contains(blockedRoute, StringComparison.OrdinalIgnoreCase));
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

    private static ConstraintSeed SeedConstraintState(string repoA, string repoB)
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
        return new ConstraintSeed(playbooks, simulations!, patchRejected, routeFailed);
    }

    private sealed record ConstraintSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderRecoveryPlaybookRecord PatchRejected,
        BuilderRecoveryPlaybookRecord RouteFailed);
}

public sealed class MainWindowViewModelBuilderOperatorConstraintTests
{
    [Fact]
    public async Task Builder_recovery_panel_supports_constraint_profiles_and_show_violating_override()
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

            var routeFailed = playbooks!.Playbooks.First(entry => string.Equals(entry.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
            BuilderOperatorConstraintService.CreateOrUpdateProfile(
                repoB,
                "Strict Recovery",
                new[]
                {
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockSpecificRouteConstraint, routeFailed.AppliesToRoutes.First(), "route"),
                    BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint)
                },
                makeActive: true,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));

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

            Assert.True(viewModel.HasBuilderRecoveryConstraintSummary);
            Assert.True(viewModel.HasBuilderRecoveryConstraintArtifactPath);
            Assert.True(viewModel.HasBuilderRecoveryActiveConstraints);
            Assert.False(viewModel.ShowBuilderRecoveryViolatingOptions);
            Assert.All(viewModel.BuilderRecoveryPlaybooks, entry => Assert.False(entry.ViolatesConstraints));

            var hiddenCount = playbooks.Playbooks.Count - viewModel.BuilderRecoveryPlaybooks.Count;
            Assert.True(hiddenCount > 0);

            viewModel.ShowBuilderRecoveryViolatingOptions = true;

            Assert.Contains(viewModel.BuilderRecoveryPlaybooks, entry => entry.ViolatesConstraints);
            Assert.Contains("Show Violating Options is enabled", viewModel.BuilderRecoveryConstraintVisibilitySummary, StringComparison.OrdinalIgnoreCase);

            var violatingPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.ViolatesConstraints);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(violatingPlaybook);

            Assert.True(viewModel.HasBuilderRecoverySelectedConstraintSummary);
            Assert.Contains("blocks this playbook", viewModel.BuilderRecoverySelectedConstraintSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(viewModel.BuilderRecoverySimulations, entry => entry.IsBlockedByConstraints);

            var blockedSimulation = viewModel.BuilderRecoverySimulations.First(entry => entry.IsBlockedByConstraints);
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(blockedSimulation);

            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationConstraintSummary);
            Assert.Contains("blocks this what-if scenario", viewModel.BuilderRecoverySelectedSimulationConstraintSummary, StringComparison.OrdinalIgnoreCase);

            viewModel.ShowBuilderRecoveryViolatingOptions = false;
            Assert.All(viewModel.BuilderRecoveryPlaybooks, entry => Assert.False(entry.ViolatesConstraints));

            await viewModel.OpenBuilderRecoveryConstraintArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
