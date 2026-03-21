using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderExecutionAuditServiceTests
{
    [Fact]
    public void Refresh_execution_audit_generates_deterministic_records_and_classifies_expected_drift()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
            File.Delete(BuilderOperatorIntentService.OperatorIntentPathForRepo(repoB));
            var reduceScope = seeded.Simulations.Simulations.First(entry =>
                string.Equals(entry.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase));
            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoB,
                selectedPlaybookId: reduceScope.PlaybookId,
                selectedSimulationId: reduceScope.SimulationId,
                playbooks: seeded.Playbooks,
                simulations: seeded.Simulations,
                rankings: seeded.Rankings,
                contextFilters: seeded.ContextFilters,
                accuracy: seeded.Accuracy,
                decisions: seeded.Decisions,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20));

            var first = BuilderExecutionAuditService.RefreshExecutionAudit(
                repoB,
                seeded.Decisions,
                seeded.Simulations,
                seeded.Accuracy,
                readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(21));
            var firstJson = File.ReadAllText(BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoB));
            var second = BuilderExecutionAuditService.RefreshExecutionAudit(
                repoB,
                seeded.Decisions,
                seeded.Simulations,
                seeded.Accuracy,
                readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(21));
            var secondJson = File.ReadAllText(BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(seeded.Decisions.Decisions.Count, first!.AuditRecords.Count);
            Assert.All(first.AuditRecords, record => Assert.NotEmpty(record.EvidenceChain));

            var routeFailureAudit = Assert.Single(first.AuditRecords, record =>
                record.ActualOutcome.Contains("failed same pattern", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("no_drift", routeFailureAudit.DriftType);
            Assert.Equal("high", routeFailureAudit.ImpactLevel);

            var resolvedBlockAudit = Assert.Single(first.AuditRecords, record =>
                record.ActualOutcome.Contains("resolved block", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("minor_drift", resolvedBlockAudit.DriftType);
            Assert.Contains(BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoB), resolvedBlockAudit.LinkedArtifacts);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Refresh_execution_audit_detects_unexpected_failure_with_high_impact_and_constraint_drift()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
            var stagedOrchestration = seeded.Simulations.Simulations.First(entry =>
                string.Equals(entry.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("blocked_by_constraints", stagedOrchestration.ConstraintCompatibility);

            BuilderPlaybookRankingServiceTests.RecordDecision(
                repoB,
                stagedOrchestration,
                "launch_prepared_route",
                "run-7010",
                "new_failure_pattern",
                false,
                stagedOrchestration.FailureClass,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(20));

            var decisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoB);
            var accuracy = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
                repoB,
                seeded.Simulations,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(21));
            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoB,
                selectedPlaybookId: stagedOrchestration.PlaybookId,
                selectedSimulationId: stagedOrchestration.SimulationId,
                playbooks: seeded.Playbooks,
                simulations: seeded.Simulations,
                rankings: seeded.Rankings,
                contextFilters: seeded.ContextFilters,
                accuracy: accuracy,
                decisions: decisions,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(22));

            var audit = BuilderExecutionAuditService.RefreshExecutionAudit(
                repoB,
                decisions,
                seeded.Simulations,
                accuracy,
                readiness,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(23));

            Assert.NotNull(decisions);
            Assert.NotNull(accuracy);
            Assert.NotNull(audit);
            var targetDecision = Assert.Single(decisions!.Decisions, entry => string.Equals(entry.ResultRunId, "run-7010", StringComparison.OrdinalIgnoreCase));
            var targetAudit = Assert.Single(audit!.AuditRecords, entry => string.Equals(entry.DecisionId, targetDecision.DecisionId, StringComparison.OrdinalIgnoreCase));

            Assert.True(targetAudit.DriftDetected);
            Assert.Equal("unexpected_failure", targetAudit.DriftType);
            Assert.Equal("high", targetAudit.ImpactLevel);
            Assert.True(targetAudit.ConstraintDriftDetected);
            Assert.True(targetAudit.IntentDriftDetected);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Execution_audit_does_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderExecutionAuditService.RefreshExecutionAudit(
                repoB,
                seeded.Decisions,
                seeded.Simulations,
                seeded.Accuracy,
                BuilderExecutionReadinessService.LoadExecutionReadiness(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(24));

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

public sealed class MainWindowViewModelBuilderExecutionAuditTests
{
    [Fact]
    public async Task Builder_execution_audit_panel_tracks_operator_decisions_and_artifacts()
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

            Assert.True(viewModel.HasBuilderExecutionAudits);
            Assert.True(viewModel.HasBuilderExecutionAuditArtifactPath);
            Assert.True(viewModel.HasSelectedBuilderExecutionAudit);
            Assert.True(viewModel.HasBuilderSelectedExecutionAuditEvidenceSteps);
            Assert.True(viewModel.HasBuilderSelectedExecutionAuditArtifactLinks);

            var latestDecision = viewModel.BuilderOperatorDecisionRows.Last();
            await viewModel.SelectBuilderOperatorDecisionCommand.ExecuteAsync(latestDecision);
            Assert.Contains(latestDecision.DecisionId, viewModel.BuilderExecutionAuditSelectionSummary, StringComparison.OrdinalIgnoreCase);

            var auditRow = viewModel.BuilderExecutionAuditRows.First();
            await viewModel.SelectBuilderExecutionAuditCommand.ExecuteAsync(auditRow);
            Assert.Contains(auditRow.DecisionId, viewModel.BuilderExecutionAuditSelectionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.HasSelectedBuilderOperatorDecision);

            await viewModel.OpenBuilderExecutionAuditArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExecutionAuditArtifactLinkCommand.ExecuteAsync(viewModel.BuilderSelectedExecutionAuditArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
