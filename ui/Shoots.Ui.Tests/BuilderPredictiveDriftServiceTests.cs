using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderPredictiveDriftServiceTests
{
    [Fact]
    public void Refresh_predictive_drift_generates_deterministic_forecasts_and_trend_classification()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedPredictiveDriftState(repoA, repoB);

            var first = BuilderPredictiveDriftService.RefreshPredictiveDrift(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Suggestions,
                seeded.Trust,
                seeded.Guardrails,
                seeded.Audit,
                seeded.Readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(46));
            var firstJson = File.ReadAllText(BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoB));
            var second = BuilderPredictiveDriftService.RefreshPredictiveDrift(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Suggestions,
                seeded.Trust,
                seeded.Guardrails,
                seeded.Audit,
                seeded.Readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(46));
            var secondJson = File.ReadAllText(BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(first!.Predictions);

            var riskySimulation = Assert.Single(first.Predictions, record =>
                string.Equals(record.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, seeded.RetrySameRoute.SimulationId, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("critical_trajectory", riskySimulation.DriftTrend);
            Assert.InRange(riskySimulation.FailureProbability, 0.75d, 1d);
            Assert.NotEmpty(riskySimulation.EvidenceChain);
            Assert.Contains(BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoB), riskySimulation.LinkedArtifacts);

            var comparisonPrediction = first.Predictions.First(record =>
                string.Equals(record.TargetType, "comparison", StringComparison.OrdinalIgnoreCase));
            Assert.True(comparisonPrediction.FailureProbability > 0d);
            Assert.NotEmpty(comparisonPrediction.EvidenceChain);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Predictive_drift_uses_guardrails_trust_and_divergence_to_raise_forecast_risk()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedPredictiveDriftState(repoA, repoB);
            var report = BuilderPredictiveDriftService.RefreshPredictiveDrift(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Suggestions,
                seeded.Trust,
                seeded.Guardrails,
                seeded.Audit,
                seeded.Readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(47));

            Assert.NotNull(report);
            var riskySimulation = Assert.Single(report!.Predictions, record =>
                string.Equals(record.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, seeded.RetrySameRoute.SimulationId, StringComparison.OrdinalIgnoreCase));
            var saferSimulation = Assert.Single(report.Predictions, record =>
                string.Equals(record.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, seeded.ReduceScope.SimulationId, StringComparison.OrdinalIgnoreCase));

            Assert.True(riskySimulation.FailureProbability > saferSimulation.FailureProbability);
            Assert.True(RiskRank(riskySimulation.RiskEscalation) <= RiskRank(saferSimulation.RiskEscalation));
            Assert.Contains(riskySimulation.EvidenceChain, step => step.AppliedRule.Contains("amplify_known_risk_patterns", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(riskySimulation.EvidenceChain, step => step.AppliedRule.Contains("apply_trust_penalty", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Predictive_drift_does_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedPredictiveDriftState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderPredictiveDriftService.RefreshPredictiveDrift(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Suggestions,
                seeded.Trust,
                seeded.Guardrails,
                seeded.Audit,
                seeded.Readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(48));

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

    internal static PredictiveDriftSeed SeedPredictiveDriftState(string repoA, string repoB)
    {
        var seeded = BuilderAutoSuggestionServiceTests.SeedAutoSuggestionState(repoA, repoB);
        BuilderPlaybookRankingServiceTests.RecordDecision(
            repoB,
            seeded.RetrySameRoute,
            "launch_override_route",
            "run-7301",
            "failed_same_pattern",
            false,
            seeded.RetrySameRoute.FailureClass,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(40));
        BuilderPlaybookRankingServiceTests.RecordDecision(
            repoB,
            seeded.RetrySameRoute,
            "launch_override_route",
            "run-7302",
            "new_failure_pattern",
            false,
            seeded.RetrySameRoute.FailureClass,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(41));

        var decisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoB);
        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            seeded.Simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(42));
        var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
            repoB,
            selectedPlaybookId: seeded.RetrySameRoute.PlaybookId,
            selectedSimulationId: seeded.RetrySameRoute.SimulationId,
            playbooks: seeded.Playbooks,
            simulations: seeded.Simulations,
            rankings: seeded.Rankings,
            contextFilters: seeded.ContextFilters,
            comparisons: seeded.Comparisons,
            accuracy: accuracy,
            decisions: decisions,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(43));
        var audit = BuilderExecutionAuditService.RefreshExecutionAudit(
            repoB,
            decisions,
            seeded.Simulations,
            accuracy,
            readiness,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(44));
        var guardrails = BuilderPreventativeGuardrailService.RefreshPreventativeGuardrails(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            accuracy,
            decisions,
            BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
            readiness,
            audit,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(44));
        var suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            seeded.Rankings,
            seeded.ContextFilters,
            seeded.Comparisons,
            accuracy,
            readiness,
            guardrails,
            decisions,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(45));
        var trust = BuilderTrustIndexService.RefreshTrustIndex(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            accuracy,
            decisions,
            suggestions,
            readiness,
            guardrails,
            audit,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(45));

        Assert.NotNull(decisions);
        Assert.NotNull(accuracy);
        Assert.NotNull(readiness);
        Assert.NotNull(audit);
        Assert.NotNull(guardrails);
        Assert.NotNull(suggestions);
        Assert.NotNull(trust);
        var reduceScope = seeded.Simulations.Simulations.First(entry =>
            string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
        return new PredictiveDriftSeed(
            seeded.Playbooks,
            seeded.Simulations,
            seeded.Comparisons,
            decisions!,
            accuracy!,
            readiness!,
            audit!,
            guardrails!,
            suggestions!,
            trust!,
            seeded.RetrySameRoute,
            reduceScope);
    }

    internal sealed record PredictiveDriftSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderRecoveryComparisonsRecord Comparisons,
        BuilderOperatorDecisionsRecord Decisions,
        BuilderSimulationAccuracyReport Accuracy,
        BuilderExecutionReadinessRecord Readiness,
        BuilderExecutionAuditReport Audit,
        BuilderPreventativeGuardrailsReport Guardrails,
        BuilderAutoSuggestionsRecord Suggestions,
        BuilderTrustIndexRecord Trust,
        BuilderRecoverySimulationRecord RetrySameRoute,
        BuilderRecoverySimulationRecord ReduceScope);

    private static int RiskRank(string value)
        => value switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };
}

public sealed class MainWindowViewModelBuilderPredictiveDriftTests
{
    [Fact]
    public async Task Builder_workspace_shows_predictive_forecasts_without_auto_blocking_actions()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);

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

            Assert.True(viewModel.HasBuilderPredictiveDrift);
            Assert.True(viewModel.HasBuilderPredictiveDriftArtifactPath);
            Assert.Contains(viewModel.BuilderRecoveryPlaybooks, entry => entry.HasPredictedRisk);

            var riskyPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.HasPredictedRisk);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(riskyPlaybook);
            Assert.True(viewModel.HasBuilderRecoverySelectedPredictiveRiskSummary);

            viewModel.ShowBuilderRecoveryViolatingOptions = true;
            var riskySimulation = viewModel.BuilderRecoverySimulations.First(entry => entry.HasPredictedRisk);
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(riskySimulation);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationPredictiveRiskSummary);

            var comparisonSet = viewModel.BuilderRecoveryComparisonSets.First(entry => entry.HasPredictedRisk);
            await viewModel.SelectBuilderRecoveryComparisonSetCommand.ExecuteAsync(comparisonSet);
            Assert.Contains(viewModel.BuilderRecoverySelectedComparisonScenarios, entry => entry.HasPredictedRisk);

            await viewModel.OpenBuilderPredictiveDriftArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderPredictiveDriftArtifactLinkCommand.ExecuteAsync(viewModel.BuilderSelectedPredictiveDriftArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
