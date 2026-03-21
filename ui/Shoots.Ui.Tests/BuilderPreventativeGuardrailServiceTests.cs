using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderPreventativeGuardrailServiceTests
{
    [Fact]
    public void Refresh_preventative_guardrails_generates_deterministic_risk_escalation_from_history()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedGuardrailState(repoA, repoB);

            var firstJson = File.ReadAllText(BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoB));
            var second = BuilderPreventativeGuardrailService.RefreshPreventativeGuardrails(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Readiness,
                seeded.Audit,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(25));
            var secondJson = File.ReadAllText(BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoB));

            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(second!.Guardrails);

            var routeGuardrail = Assert.Single(second.Guardrails, entry =>
                string.Equals(entry.TargetScope, "route", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, seeded.RetrySameRoute.TargetRoute, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("repeated_failure_without_variation", routeGuardrail.TriggerPatterns);
            Assert.True(string.Equals(routeGuardrail.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(routeGuardrail.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));

            var simulationGuardrail = Assert.Single(second.Guardrails, entry =>
                string.Equals(entry.TargetScope, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, seeded.StagedOrchestration.SimulationId, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(simulationGuardrail.TriggerPatterns, pattern =>
                string.Equals(pattern, "constraint_blocked_simulation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pattern, "selected_no_go_path", StringComparison.OrdinalIgnoreCase));

            var repoGuardrail = Assert.Single(second.Guardrails, entry =>
                string.Equals(entry.TargetScope, "repo", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(repoGuardrail.TriggerPatterns, pattern =>
                string.Equals(pattern, "workspace_no_go_readiness", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pattern, "active_constraint_violations", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Preventative_guardrails_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedGuardrailState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderPreventativeGuardrailService.RefreshPreventativeGuardrails(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Readiness,
                seeded.Audit,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(26));

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

    internal static GuardrailSeed SeedGuardrailState(string repoA, string repoB)
    {
        var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
        var retrySameRoute = seeded.Simulations.Simulations.First(entry =>
            string.Equals(entry.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase));
        var stagedOrchestration = seeded.Simulations.Simulations.First(entry =>
            string.Equals(entry.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase));

        BuilderPlaybookRankingServiceTests.RecordDecision(
            repoB,
            retrySameRoute,
            "launch_override_route",
            "run-7101",
            "failed_same_pattern",
            false,
            retrySameRoute.FailureClass,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20));
        BuilderPlaybookRankingServiceTests.RecordDecision(
            repoB,
            stagedOrchestration,
            "launch_prepared_route",
            "run-7102",
            "new_failure_pattern",
            false,
            stagedOrchestration.FailureClass,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(21));

        var decisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoB);
        var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            repoB,
            seeded.Simulations,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(22));
        var rankings = BuilderPlaybookRankingService.RefreshPlaybookRankings(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            accuracy,
            decisions,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(22));
        var contextFilters = BuilderPlaybookContextFilterService.RefreshContextFilters(
            repoB,
            seeded.Playbooks,
            rankings,
            decisions,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(23));
        var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
            repoB,
            selectedPlaybookId: stagedOrchestration.PlaybookId,
            selectedSimulationId: stagedOrchestration.SimulationId,
            playbooks: seeded.Playbooks,
            simulations: seeded.Simulations,
            rankings: rankings,
            contextFilters: contextFilters,
            accuracy: accuracy,
            decisions: decisions,
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(24));
        var audit = BuilderExecutionAuditService.RefreshExecutionAudit(
            repoB,
            decisions,
            seeded.Simulations,
            accuracy,
            readiness,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(25));
        var guardrails = BuilderPreventativeGuardrailService.RefreshPreventativeGuardrails(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            accuracy,
            decisions,
            BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
            readiness,
            audit,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(25));

        Assert.NotNull(decisions);
        Assert.NotNull(accuracy);
        Assert.NotNull(rankings);
        Assert.NotNull(contextFilters);
        Assert.NotNull(readiness);
        Assert.NotNull(audit);
        Assert.NotNull(guardrails);
        return new GuardrailSeed(seeded.Playbooks, seeded.Simulations, decisions!, accuracy!, readiness!, audit!, guardrails!, retrySameRoute, stagedOrchestration);
    }

    internal sealed record GuardrailSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderOperatorDecisionsRecord Decisions,
        BuilderSimulationAccuracyReport Accuracy,
        BuilderExecutionReadinessRecord Readiness,
        BuilderExecutionAuditReport Audit,
        BuilderPreventativeGuardrailsReport Guardrails,
        BuilderRecoverySimulationRecord RetrySameRoute,
        BuilderRecoverySimulationRecord StagedOrchestration);
}

public sealed class MainWindowViewModelBuilderPreventativeGuardrailTests
{
    [Fact]
    public async Task Builder_guardrail_panel_and_recovery_surfaces_show_escalated_risk_without_execution_side_effects()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderPreventativeGuardrailServiceTests.SeedGuardrailState(repoA, repoB);

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

            Assert.True(viewModel.HasBuilderPreventativeGuardrails);
            Assert.True(viewModel.HasBuilderPreventativeGuardrailArtifactPath);
            Assert.True(viewModel.HasSelectedBuilderPreventativeGuardrail);

            var riskyPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.HasRiskEscalation);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(riskyPlaybook);
            Assert.True(viewModel.HasBuilderRecoverySelectedRiskEscalationSummary);

            viewModel.ShowBuilderRecoveryViolatingOptions = true;
            var riskySimulation = viewModel.BuilderRecoverySimulations.First(entry => entry.HasRiskEscalation);
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(riskySimulation);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationGuardrailSummary);

            var guardrail = viewModel.BuilderPreventativeGuardrails.First();
            await viewModel.SelectBuilderPreventativeGuardrailCommand.ExecuteAsync(guardrail);
            Assert.True(viewModel.HasBuilderSelectedPreventativeGuardrailTriggers);
            Assert.True(viewModel.HasBuilderSelectedPreventativeGuardrailArtifactLinks);

            await viewModel.OpenBuilderPreventativeGuardrailArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderPreventativeGuardrailArtifactLinkCommand.ExecuteAsync(viewModel.BuilderSelectedPreventativeGuardrailArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
