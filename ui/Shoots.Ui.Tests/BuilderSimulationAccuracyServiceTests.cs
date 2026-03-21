using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderSimulationAccuracyServiceTests
{
    [Fact]
    public void Refresh_simulation_accuracy_generates_deterministic_records_and_match_types()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var simulations = SeedRecoverySimulations(repoA, repoB);
            var routeFailedSimulations = simulations!.Simulations
                .Where(entry => string.Equals(entry.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var retrySameRoute = routeFailedSimulations.First(entry => string.Equals(entry.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase));
            var switchRoute = routeFailedSimulations.First(entry => string.Equals(entry.Scenario, "switch_route_manual", StringComparison.OrdinalIgnoreCase));

            RecordDecision(repoB, retrySameRoute, "launch_override_route", "run-0001", "failed_same_pattern", false, "route_failed", BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            RecordDecision(repoB, switchRoute, "launch_prepared_route", "run-0002", "new_failure_pattern", false, "route_failed", BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

            var first = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));
            var firstJson = File.ReadAllText(BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoB));
            var second = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));
            var secondJson = File.ReadAllText(BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Collection(
                first!.AccuracyRecords,
                record =>
                {
                    Assert.Equal(retrySameRoute.SimulationId, record.SimulationId);
                    Assert.Equal("exact_match", record.MatchType);
                    Assert.True(record.AccuracyFlag);
                    Assert.Equal("none", record.ErrorClass);
                },
                record =>
                {
                    Assert.Equal(switchRoute.SimulationId, record.SimulationId);
                    Assert.Equal("mismatch_new_failure", record.MatchType);
                    Assert.False(record.AccuracyFlag);
                    Assert.Equal("incorrect_success_prediction", record.ErrorClass);
                });
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Simulation_accuracy_calibration_aggregates_by_simulation_type_route_and_failure_class()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var simulations = SeedRecoverySimulations(repoA, repoB);
            var reduceScope = simulations!.Simulations.First(entry => string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));

            RecordDecision(repoB, reduceScope, "approve_pending_in_filter", "run-0101", "partial_success", true, reduceScope.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            RecordDecision(repoB, reduceScope, "approve_pending_in_group", "run-0102", "partial_success", true, reduceScope.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));
            RecordDecision(repoB, reduceScope, "approve_pending_in_directory", "run-0103", "partial_success", true, reduceScope.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

            var report = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));

            Assert.NotNull(report);
            var scenarioCalibration = Assert.Single(report!.SimulationTypeCalibration, entry => string.Equals(entry.Key, reduceScope.Scenario, StringComparison.OrdinalIgnoreCase));
            var routeCalibration = Assert.Single(report.RouteCalibration, entry => string.Equals(entry.Key, reduceScope.TargetRoute, StringComparison.OrdinalIgnoreCase));
            var failureCalibration = Assert.Single(report.FailureClassCalibration, entry => string.Equals(entry.Key, reduceScope.FailureClass, StringComparison.OrdinalIgnoreCase));

            Assert.Equal("high", scenarioCalibration.CalibratedConfidence);
            Assert.Equal(1d, scenarioCalibration.HistoricalAccuracyRate);
            Assert.Equal(3, scenarioCalibration.SampleSize);
            Assert.Equal("high", routeCalibration.CalibratedConfidence);
            Assert.Equal("high", failureCalibration.CalibratedConfidence);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Simulation_accuracy_refresh_does_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var simulations = SeedRecoverySimulations(repoA, repoB);
            var simulation = simulations!.Simulations.First(entry => string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            RecordDecision(repoB, simulation, "approve_pending_in_filter", "run-0201", "partial_success", true, simulation.FailureClass, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

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

    private static BuilderRecoverySimulationsRecord? SeedRecoverySimulations(string repoA, string repoB)
    {
        var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
        BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
        return BuilderRecoverySimulationService.RefreshRecoverySimulations(
            seeded.Descriptors,
            seeded.Orchestration,
            seeded.ActiveWorkspaceId,
            seeded.RequestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
    }

    private static void RecordDecision(
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
}

public sealed class MainWindowViewModelBuilderSimulationAccuracyTests
{
    [Fact]
    public async Task Builder_recovery_simulation_panel_displays_confidence_calibration_and_accuracy_history()
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

            var patchRejectedPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(patchRejectedPlaybook);
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(viewModel.BuilderRecoverySimulations.First());

            if (!viewModel.HasBuilderReviewCurrentFile)
            {
                await viewModel.SelectBuilderReviewFileCommand.ExecuteAsync(viewModel.BuilderReviewGroups.First().Files.First());
            }

            await viewModel.ApprovePendingBuilderReviewGroupCommand.ExecuteAsync();

            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationConfidenceSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationAccuracySummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationAccuracyHistory);
            Assert.True(viewModel.HasBuilderRecoverySimulationAccuracyArtifactPath);
            Assert.Contains("unstable", viewModel.BuilderRecoverySelectedSimulationTrustIndicator, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Recorded 1 simulation accuracy comparison", viewModel.BuilderRecoverySimulationAccuracySummary, StringComparison.OrdinalIgnoreCase);

            await viewModel.OpenBuilderRecoverySimulationAccuracyArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
