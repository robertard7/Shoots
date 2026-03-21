using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderTrustIndexServiceTests
{
    [Fact]
    public void Refresh_trust_index_generates_deterministic_scores_profiles_and_evidence()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderAutoSuggestionServiceTests.SeedAutoSuggestionState(repoA, repoB);
            var suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                seeded.Decisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(33));
            var audit = BuilderExecutionAuditService.LoadExecutionAudit(repoB);

            var first = BuilderTrustIndexService.RefreshTrustIndex(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                suggestions,
                seeded.Readiness,
                seeded.Guardrails,
                audit,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(34));
            var firstJson = File.ReadAllText(BuilderTrustIndexService.TrustIndexPathForRepo(repoB));
            var second = BuilderTrustIndexService.RefreshTrustIndex(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                suggestions,
                seeded.Readiness,
                seeded.Guardrails,
                audit,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(34));
            var secondJson = File.ReadAllText(BuilderTrustIndexService.TrustIndexPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.InRange(first!.TrustScore, 0d, 100d);
            Assert.InRange(first.OperatorAlignmentScore, 0d, 100d);
            Assert.False(string.IsNullOrWhiteSpace(first.ConfidenceProfile));
            Assert.NotEmpty(first.Metrics);
            Assert.Contains(first.Metrics, entry => string.Equals(entry.MetricId, "suggestion_success", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.TargetProfiles, entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.TargetProfiles, entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoB), first.EvidenceLinks);
            Assert.Contains(BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoB), first.EvidenceLinks);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Trust_index_uses_recorded_operator_outcomes_for_alignment_score()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderAutoSuggestionServiceTests.SeedAutoSuggestionState(repoA, repoB);
            var suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                seeded.Decisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(35));

            var report = BuilderTrustIndexService.RefreshTrustIndex(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                suggestions,
                seeded.Readiness,
                seeded.Guardrails,
                BuilderExecutionAuditService.LoadExecutionAudit(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(36));

            Assert.NotNull(report);
            var operatorAlignment = Assert.Single(report!.Metrics, entry => string.Equals(entry.MetricId, "operator_alignment", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(40d, operatorAlignment.Score);
            Assert.Equal(40d, report.OperatorAlignmentScore);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Trust_index_does_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderAutoSuggestionServiceTests.SeedAutoSuggestionState(repoA, repoB);
            var suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                seeded.Decisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(37));
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderTrustIndexService.RefreshTrustIndex(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Accuracy,
                seeded.Decisions,
                suggestions,
                seeded.Readiness,
                seeded.Guardrails,
                BuilderExecutionAuditService.LoadExecutionAudit(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(38));

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

public sealed class MainWindowViewModelBuilderTrustIndexTests
{
    [Fact]
    public async Task Builder_workspace_shows_global_and_target_trust_without_auto_adjusting_actions()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderAutoSuggestionServiceTests.SeedAutoSuggestionState(repoA, repoB);

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

            Assert.True(viewModel.HasBuilderTrustIndex);
            Assert.True(viewModel.HasBuilderTrustIndexArtifactPath);
            Assert.True(viewModel.HasBuilderTrustMetrics);
            Assert.Contains(viewModel.BuilderRecoveryPlaybooks, entry => entry.HasTrustProfile);

            var trustedPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.HasTrustProfile);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(trustedPlaybook);
            Assert.True(viewModel.HasBuilderRecoverySelectedTrustSummary);

            viewModel.ShowBuilderRecoveryViolatingOptions = true;
            var trustedSimulation = viewModel.BuilderRecoverySimulations.First(entry => entry.HasTrustProfile);
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(trustedSimulation);
            Assert.True(viewModel.HasBuilderRecoverySelectedSimulationTrustSummary);
            Assert.Contains(viewModel.BuilderRecoverySelectedComparisonScenarios, entry => entry.HasTrustProfile);

            await viewModel.OpenBuilderTrustIndexArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
