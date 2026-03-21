using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderRouteIntelligenceServiceTests
{
    [Fact]
    public void Refresh_route_intelligence_artifacts_generates_recommendations_and_warnings_deterministically()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var workspaceIdB = BuilderWorkspaceService.ResolveWorkspaceId(repoB);
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                workspaceIdB,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));
            Assert.NotNull(orchestration);

            BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                workspaceIdB,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));

            var first = BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                descriptors,
                orchestration!,
                workspaceIdB,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));
            var second = BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                descriptors,
                orchestration!,
                workspaceIdB,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.True(File.Exists(BuilderRouteIntelligenceService.RouteRecommendationsPathForRepo(repoB)));
            Assert.True(File.Exists(BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoB)));
            Assert.True(File.Exists(BuilderRouteIntelligenceService.OrchestrationRecommendationsPathForRepo(repoB)));
            var route = Assert.Single(first!.RouteRecommendations.RecommendedRoutes);
            Assert.Equal("builder_review_queue", route.Route);
            Assert.True(route.HistoricalFailureRate > 0d);
            Assert.NotEmpty(first.RiskWarnings.Entries);
            Assert.Equal(
                first.RouteRecommendations.RecommendedRoutes.Select(entry => entry.Summary).ToArray(),
                second!.RouteRecommendations.RecommendedRoutes.Select(entry => entry.Summary).ToArray());
            Assert.Equal(
                first.RiskWarnings.Entries.Select(entry => entry.Summary).ToArray(),
                second.RiskWarnings.Entries.Select(entry => entry.Summary).ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Successful_patterns_produce_preferred_route_recommendations_and_orchestration_sequence()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var workspaceIdA = BuilderWorkspaceService.ResolveWorkspaceId(repoA);
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoA, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));
            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));

            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                workspaceIdA,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            Assert.NotNull(orchestration);

            BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                workspaceIdA,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));
            var intelligence = BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                descriptors,
                orchestration!,
                workspaceIdA,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

            Assert.NotNull(intelligence);
            var recommendation = Assert.Single(intelligence!.RouteRecommendations.RecommendedRoutes);
            Assert.Equal("builder_prepared_route", recommendation.Route);
            Assert.Equal(100d, recommendation.HistoricalSuccessRate);
            Assert.Equal(0d, recommendation.HistoricalFailureRate);
            Assert.Contains("builder_prepared_route:floor", intelligence.RouteRecommendations.ModelTierSuggestions);
            Assert.Equal(
                orchestration.Plan.ParticipatingWorkspaceIds.ToArray(),
                intelligence.OrchestrationRecommendations.RecommendedOrchestrationSequence.ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Route_intelligence_refresh_does_not_override_workspace_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var workspaceIdB = BuilderWorkspaceService.ResolveWorkspaceId(repoB);
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                workspaceIdB,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));
            Assert.NotNull(orchestration);

            BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                workspaceIdB,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));

            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(beforeRoute);
            Assert.NotNull(beforeApply);

            BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                descriptors,
                orchestration!,
                workspaceIdB,
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(afterRoute);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.ApplyEligibilityState, afterApply!.ApplyEligibilityState);
            Assert.Equal(beforeApply.BlockReasons.ToArray(), afterApply.BlockReasons.ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderRouteIntelligenceTests
{
    [Fact]
    public async Task Builder_route_insight_panel_displays_recommendations_warnings_and_orchestration_guidance()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);

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

            Assert.True(viewModel.HasBuilderRouteRecommendations);
            Assert.True(viewModel.HasBuilderRouteRiskWarnings);
            Assert.True(viewModel.HasBuilderRecommendedOrchestrationSequence);
            Assert.Contains("builder_review_queue", viewModel.BuilderRouteRecommendations[0].Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BuilderRouteIntelligenceService.RouteRecommendationsPathForRepo(repoB), viewModel.BuilderRouteRecommendationArtifactPath);

            await viewModel.OpenBuilderRouteRecommendationArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
