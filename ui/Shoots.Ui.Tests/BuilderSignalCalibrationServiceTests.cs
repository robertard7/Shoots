using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderSignalCalibrationServiceTests
{
    [Fact]
    public void Refresh_signal_calibration_generates_deterministic_normalized_weights()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);

            var first = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(49));
            var firstJson = File.ReadAllText(BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoB));
            var second = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(49));
            var secondJson = File.ReadAllText(BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(6, first!.Weights.Count);
            Assert.InRange(first.Weights.Sum(entry => entry.AdjustedWeight), 0.999d, 1.001d);
            Assert.Contains(first.Weights, entry => string.Equals(entry.SignalId, BuilderSignalCalibrationService.GuardrailSignalId, StringComparison.OrdinalIgnoreCase) && entry.AdjustedWeight > entry.BaseWeight);
            Assert.Contains(first.Weights, entry => string.Equals(entry.SignalId, BuilderSignalCalibrationService.DriftSignalId, StringComparison.OrdinalIgnoreCase) && entry.AdjustedWeight > entry.BaseWeight);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Auto_suggestions_include_calibrated_signal_contributions()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var calibration = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(50));

            var report = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                seeded.Decisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(51),
                seeded.Trust,
                BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                calibration);

            Assert.NotNull(report);
            var primary = report!.Suggestions.First(entry =>
                string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(calibration.CalibrationProfile, primary.CalibrationProfile);
            Assert.Equal(6, primary.SignalContributions.Count);
            Assert.Contains("Composite score", primary.SignalBalanceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(primary.SignalContributions, entry => string.Equals(entry.SignalId, BuilderSignalCalibrationService.DriftSignalId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Recovery_comparisons_include_signal_balance_breakdown()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var calibration = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(52));

            var report = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(53),
                3,
                seeded.Trust,
                BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                seeded.Guardrails,
                calibration);

            Assert.NotNull(report);
            var metric = report!.ComparisonSets.SelectMany(set => set.ComparisonMetrics).First();
            Assert.Equal(calibration.CalibrationProfile, metric.CalibrationProfile);
            Assert.Equal(6, metric.SignalContributions.Count);
            Assert.Contains("Composite score", metric.SignalBalanceSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Execution_readiness_records_signal_balance_without_mutating_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoB,
                selectedPlaybookId: seeded.RetrySameRoute.PlaybookId,
                selectedSimulationId: seeded.RetrySameRoute.SimulationId,
                selectedComparisonId: seeded.Comparisons.ComparisonSets.First().ComparisonId,
                playbooks: seeded.Playbooks,
                simulations: seeded.Simulations,
                rankings: BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                contextFilters: BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                comparisons: seeded.Comparisons,
                accuracy: seeded.Accuracy,
                decisions: seeded.Decisions,
                routeWarnings: BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoB),
                justifications: BuilderDecisionJustificationService.LoadDecisionJustifications(repoB),
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(54),
                guardrails: seeded.Guardrails,
                trust: seeded.Trust,
                predictiveDrift: BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                calibration: BuilderSignalCalibrationService.LoadSignalCalibration(repoB));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            Assert.NotNull(readiness);
            Assert.Equal(6, readiness!.SignalContributions.Count);
            Assert.Contains("Signal balance", readiness.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Composite score", readiness.SignalBalanceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.FinalizationState, afterApply!.FinalizationState);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderSignalCalibrationTests
{
    [Fact]
    public async Task Builder_workspace_shows_signal_balance_panel_and_option_contributions()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            _ = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoB),
                BuilderOperatorDecisionService.LoadOperatorDecisions(repoB),
                BuilderExecutionAuditService.LoadExecutionAudit(repoB),
                BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoB),
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(55));

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

            Assert.True(viewModel.HasBuilderSignalCalibration);
            Assert.True(viewModel.HasBuilderSignalCalibrationArtifactPath);
            Assert.True(viewModel.HasBuilderSignalCalibrationWeights);

            var playbook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.HasSuggestedRecommendation);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(playbook);
            Assert.True(viewModel.HasBuilderRecoverySelectedSignalBalanceSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedSignalContributions);

            viewModel.ShowBuilderRecoveryViolatingOptions = true;
            var simulation = viewModel.BuilderRecoverySimulations.First();
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(simulation);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationSignalBalanceSummary);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationSignalContributions);

            await viewModel.OpenBuilderSignalCalibrationArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
