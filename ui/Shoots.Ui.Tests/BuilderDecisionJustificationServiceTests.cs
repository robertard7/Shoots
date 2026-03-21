using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderDecisionJustificationServiceTests
{
    [Fact]
    public void Refresh_decision_justifications_generates_deterministic_reasoning_chains_for_playbooks_simulations_and_comparisons()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
            var comparisons = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.ContextFilters,
                seeded.Intent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));

            Assert.NotNull(comparisons);
            var constraints = BuilderOperatorConstraintService.LoadOperatorConstraints(repoB);

            var first = BuilderDecisionJustificationService.RefreshDecisionJustifications(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                comparisons,
                seeded.Accuracy,
                seeded.Intent,
                constraints,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(18));
            var firstJson = File.ReadAllText(BuilderDecisionJustificationService.DecisionJustificationsPathForRepo(repoB));
            var second = BuilderDecisionJustificationService.RefreshDecisionJustifications(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                comparisons,
                seeded.Accuracy,
                seeded.Intent,
                constraints,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(18));
            var secondJson = File.ReadAllText(BuilderDecisionJustificationService.DecisionJustificationsPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.Contains(first!.Justifications, entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Justifications, entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Justifications, entry => string.Equals(entry.TargetType, "comparison", StringComparison.OrdinalIgnoreCase));
            Assert.All(first.Justifications, entry =>
            {
                Assert.NotEmpty(entry.ReasoningChain);
                Assert.NotEmpty(entry.EvidenceLinks);
                Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
                Assert.Contains("advisory only", entry.AuditNarrative, StringComparison.OrdinalIgnoreCase);
            });

            var playbookJustification = first.Justifications.First(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(playbookJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "fixed_ranking_formula_v2", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(playbookJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "contextual_visibility_v3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(playbookJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "hard_bound_constraint_profile_v1", StringComparison.OrdinalIgnoreCase));

            var simulationJustification = first.Justifications.First(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(simulationJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "simulation_projection_v2", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(simulationJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "calibration_tracking_v1", StringComparison.OrdinalIgnoreCase));

            var comparisonJustification = first.Justifications.First(entry => string.Equals(entry.TargetType, "comparison", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(comparisonJustification.ReasoningChain, step => string.Equals(step.AppliedRule, "tradeoff_analysis_v1", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoB), comparisonJustification.EvidenceLinks);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Decision_justifications_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryComparisonServiceTests.SeedComparisonState(repoA, repoB);
            var comparisons = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.Accuracy,
                seeded.Decisions,
                seeded.ContextFilters,
                seeded.Intent,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(17));
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderDecisionJustificationService.RefreshDecisionJustifications(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                comparisons,
                seeded.Accuracy,
                seeded.Intent,
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(19));

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

public sealed class MainWindowViewModelBuilderDecisionJustificationTests
{
    [Fact]
    public async Task Builder_recovery_workspace_exposes_deterministic_justifications_and_audit_narratives()
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

            Assert.True(viewModel.HasBuilderDecisionJustifications);
            Assert.True(viewModel.HasBuilderDecisionJustificationArtifactPath);
            Assert.True(viewModel.HasSelectedBuilderDecisionJustification);
            Assert.True(viewModel.HasBuilderSelectedDecisionJustificationSteps);
            Assert.True(viewModel.HasBuilderSelectedDecisionJustificationAuditNarrative);

            var playbookRow = viewModel.BuilderDecisionJustifications.First(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderDecisionJustificationCommand.ExecuteAsync(playbookRow);
            Assert.Contains(playbookRow.TargetLabel, viewModel.BuilderSelectedDecisionJustificationTitle, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.HasBuilderRecoverySelectedPlaybook);

            var simulationRow = viewModel.BuilderDecisionJustifications.First(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderDecisionJustificationCommand.ExecuteAsync(simulationRow);
            Assert.Contains("simulation", viewModel.BuilderDecisionJustificationSelectionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulation);

            var comparisonRow = viewModel.BuilderDecisionJustifications.First(entry => string.Equals(entry.TargetType, "comparison", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderDecisionJustificationCommand.ExecuteAsync(comparisonRow);
            Assert.Contains("comparison", viewModel.BuilderDecisionJustificationSelectionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.HasSelectedBuilderRecoveryComparisonSet);

            await viewModel.OpenBuilderDecisionJustificationArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderDecisionJustificationArtifactLinkCommand.ExecuteAsync(viewModel.BuilderSelectedDecisionJustificationArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
