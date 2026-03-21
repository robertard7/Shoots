using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderAutoSuggestionServiceTests
{
    [Fact]
    public void Refresh_auto_suggestions_generates_deterministic_primary_and_alternate_recommendations()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedAutoSuggestionState(repoA, repoB);

            var first = BuilderAutoSuggestionService.RefreshAutoSuggestions(
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
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(28));
            var firstJson = File.ReadAllText(BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoB));
            var second = BuilderAutoSuggestionService.RefreshAutoSuggestions(
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
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(28));
            var secondJson = File.ReadAllText(BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoB));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(first!.Suggestions);

            var primaryPlaybook = Assert.Single(first.Suggestions, entry =>
                string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
            var primarySimulation = Assert.Single(first.Suggestions, entry =>
                string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain("blocked_by_constraints", primaryPlaybook.ConstraintStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blocked_by_constraints", primarySimulation.ConstraintStatus, StringComparison.OrdinalIgnoreCase);
            if (first.Suggestions.Any(entry =>
                    string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.ConstraintStatus, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.DoesNotContain("critical", primarySimulation.RiskLevel, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains(BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoB), primarySimulation.SupportingEvidence);
            Assert.Contains(BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoB), primarySimulation.SupportingEvidence);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Auto_suggestions_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedAutoSuggestionState(repoA, repoB);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderAutoSuggestionService.RefreshAutoSuggestions(
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
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(29));

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

    [Fact]
    public void Auto_suggestions_record_latest_decision_divergence_when_operator_ignores_primary_recommendation()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = SeedAutoSuggestionState(repoA, repoB);
            var baseline = BuilderAutoSuggestionService.RefreshAutoSuggestions(
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
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(30));
            Assert.NotNull(baseline);

            var primarySimulationId = baseline!.Suggestions.First(entry =>
                string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase)).TargetId;
            var ignoredSimulation = seeded.StagedOrchestration.SimulationId.Equals(primarySimulationId, StringComparison.OrdinalIgnoreCase)
                ? seeded.RetrySameRoute
                : seeded.StagedOrchestration;
            BuilderPlaybookRankingServiceTests.RecordDecision(
                repoB,
                ignoredSimulation,
                "launch_override_route",
                "run-7201",
                "failed_same_pattern",
                false,
                ignoredSimulation.FailureClass,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(31));

            var refreshedDecisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoB);
            var withDivergence = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                seeded.Rankings,
                seeded.ContextFilters,
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                refreshedDecisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(32));

            Assert.NotNull(withDivergence);
            Assert.NotNull(withDivergence!.LatestDecisionDivergence);
            Assert.True(withDivergence.LatestDecisionDivergence!.DivergedFromPrimary);
            Assert.Contains("instead of recommended", withDivergence.LatestDecisionDivergence.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    internal static AutoSuggestionSeed SeedAutoSuggestionState(string repoA, string repoB)
    {
        var seeded = BuilderPreventativeGuardrailServiceTests.SeedGuardrailState(repoA, repoB);
        var rankings = BuilderPlaybookRankingService.LoadPlaybookRankings(repoB);
        var contextFilters = BuilderPlaybookContextFilterService.LoadContextFilters(repoB);
        var comparisons = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
            repoB,
            seeded.Playbooks,
            seeded.Simulations,
            rankings,
            seeded.Accuracy,
            seeded.Decisions,
            contextFilters,
            BuilderOperatorIntentService.LoadOperatorIntent(repoB),
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(27));

        Assert.NotNull(rankings);
        Assert.NotNull(contextFilters);
        Assert.NotNull(comparisons);
        return new AutoSuggestionSeed(
            seeded.Playbooks,
            seeded.Simulations,
            rankings!,
            contextFilters!,
            comparisons!,
            seeded.Accuracy,
            seeded.Decisions,
            seeded.Readiness,
            seeded.Guardrails,
            seeded.RetrySameRoute,
            seeded.StagedOrchestration);
    }

    internal sealed record AutoSuggestionSeed(
        BuilderRecoveryPlaybooksRecord Playbooks,
        BuilderRecoverySimulationsRecord Simulations,
        BuilderPlaybookRankingsRecord Rankings,
        BuilderPlaybookContextFiltersRecord ContextFilters,
        BuilderRecoveryComparisonsRecord Comparisons,
        BuilderSimulationAccuracyReport Accuracy,
        BuilderOperatorDecisionsRecord Decisions,
        BuilderExecutionReadinessRecord Readiness,
        BuilderPreventativeGuardrailsReport Guardrails,
        BuilderRecoverySimulationRecord RetrySameRoute,
        BuilderRecoverySimulationRecord StagedOrchestration);
}

public sealed class MainWindowViewModelBuilderAutoSuggestionTests
{
    [Fact]
    public async Task Builder_workspace_shows_recommended_playbook_simulation_and_comparison_highlights_without_auto_selection()
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

            Assert.True(viewModel.HasBuilderAutoSuggestions);
            Assert.True(viewModel.HasBuilderAutoSuggestionArtifactPath);
            Assert.True(viewModel.HasBuilderAutoSuggestionPrimarySummary);

            var recommendedPlaybook = viewModel.BuilderRecoveryPlaybooks.First(entry => entry.HasSuggestedRecommendation && entry.IsPrimarySuggestedRecommendation);
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(recommendedPlaybook);

            Assert.Contains(viewModel.BuilderRecoverySimulations, entry => entry.HasSuggestedRecommendation);
            Assert.Contains(viewModel.BuilderRecoveryComparisonSets, entry => entry.HasSuggestedRecommendation);

            await viewModel.OpenBuilderAutoSuggestionArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
