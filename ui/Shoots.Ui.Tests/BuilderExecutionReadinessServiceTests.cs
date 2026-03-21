using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderExecutionReadinessServiceTests
{
    [Fact]
    public void Refresh_execution_readiness_returns_no_go_for_blocked_review_state()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "phase-68-no-go");
            BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "directory", seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc);

            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoRoot,
                observedUtc: seeded.ObservedUtc.AddMinutes(1));

            Assert.NotNull(readiness);
            Assert.Equal("no_go", readiness!.ReadinessState);
            Assert.NotEmpty(readiness.BlockingConditions);
            Assert.Contains(readiness.BlockingConditions, entry => entry.Reason.Contains("blocked", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("NO-GO", readiness.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_execution_readiness_returns_caution_when_workspace_is_clean_but_route_warning_exists()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "phase-68-caution");
            BuilderExecutionReadinessTestData.MarkWorkspaceReadyToFinalize(repoRoot, seeded.ObservedUtc.AddMinutes(1));
            BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "directory", seeded.ApprovedFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(2));

            var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
            var warningsPath = BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot);
            BuilderExecutionReadinessTestData.WriteJson(
                warningsPath,
                new BuilderRouteRiskWarningsRecord(
                    "phase-68-caution",
                    workspaceId,
                    new[]
                    {
                        new BuilderRouteRiskWarningEntryRecord(
                            workspaceId,
                            "builder_prepared_route",
                            "Route has weak historical success in this workspace.",
                            "knowledge-node-68",
                            seeded.ObservedUtc.AddMinutes(3))
                    },
                    "Recorded one deterministic route risk warning.",
                    warningsPath,
                    seeded.ObservedUtc.AddMinutes(3)));

            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoRoot,
                observedUtc: seeded.ObservedUtc.AddMinutes(4));

            Assert.NotNull(readiness);
            Assert.Equal("caution", readiness!.ReadinessState);
            Assert.Empty(readiness.BlockingConditions);
            Assert.NotEmpty(readiness.Warnings);
            Assert.Contains(readiness.Warnings, entry => entry.Reason.Contains("Route warning", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_execution_readiness_returns_go_when_workspace_is_clean_and_no_warnings_exist()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "phase-68-go");
            BuilderExecutionReadinessTestData.MarkWorkspaceReadyToFinalize(repoRoot, seeded.ObservedUtc.AddMinutes(1));
            BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "directory", seeded.ApprovedFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(2));

            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoRoot,
                observedUtc: seeded.ObservedUtc.AddMinutes(3));

            Assert.NotNull(readiness);
            Assert.Equal("go", readiness!.ReadinessState);
            Assert.Empty(readiness.BlockingConditions);
            Assert.Empty(readiness.Warnings);
            Assert.Contains("GO", readiness.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Execution_readiness_does_not_change_route_resolution_or_finalize_gates()
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

            BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoB,
                selectedPlaybookId: seeded.Playbooks.Playbooks.First().PlaybookId,
                selectedSimulationId: seeded.Simulations.Simulations.First().SimulationId,
                selectedComparisonId: comparisons?.ComparisonSets.FirstOrDefault()?.ComparisonId ?? string.Empty,
                playbooks: seeded.Playbooks,
                simulations: seeded.Simulations,
                rankings: seeded.Rankings,
                contextFilters: seeded.ContextFilters,
                comparisons: comparisons,
                accuracy: seeded.Accuracy,
                decisions: seeded.Decisions,
                routeWarnings: BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoB),
                justifications: BuilderDecisionJustificationService.RefreshDecisionJustifications(
                    repoB,
                    seeded.Playbooks,
                    seeded.Simulations,
                    seeded.Rankings,
                    seeded.ContextFilters,
                    comparisons,
                    seeded.Accuracy,
                    seeded.Intent,
                    BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                    BuilderWorkspaceTestData.ObservedUtc.AddMinutes(18)),
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(19));

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

public sealed class MainWindowViewModelBuilderExecutionReadinessTests
{
    [Fact]
    public async Task Builder_execution_readiness_panel_tracks_selected_recovery_path_and_artifacts()
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

            Assert.True(viewModel.HasBuilderExecutionReadiness);
            Assert.True(viewModel.HasBuilderExecutionReadinessArtifactPath);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.BuilderExecutionReadinessStateLabel));
            Assert.True(viewModel.HasBuilderExecutionReadinessBlockingConditions || viewModel.HasBuilderExecutionReadinessWarnings);

            var simulation = viewModel.BuilderRecoverySimulations.First();
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(simulation);
            Assert.Contains("simulation", viewModel.BuilderExecutionReadinessSelectionSummary, StringComparison.OrdinalIgnoreCase);

            var comparison = viewModel.BuilderRecoveryComparisonSets.First();
            await viewModel.SelectBuilderRecoveryComparisonSetCommand.ExecuteAsync(comparison);
            Assert.Contains("comparison", viewModel.BuilderExecutionReadinessSelectionSummary, StringComparison.OrdinalIgnoreCase);

            await viewModel.OpenBuilderExecutionReadinessArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExecutionReadinessArtifactLinkCommand.ExecuteAsync(viewModel.BuilderExecutionReadinessArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

internal static class BuilderExecutionReadinessTestData
{
    public static void MarkWorkspaceReadyToFinalize(string repoRoot, DateTimeOffset observedUtc)
    {
        var artifacts = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot);
        Assert.NotNull(artifacts.PatchDiffReview);
        Assert.NotNull(artifacts.FileReviewDecision);
        Assert.NotNull(artifacts.PatchReviewOutcome);
        Assert.NotNull(artifacts.PatchApplyDecision);

        WriteJson(
            BuilderReviewWorkspaceService.PatchDiffReviewPathForRepo(repoRoot),
            artifacts.PatchDiffReview! with
            {
                OverallFileReviewState = "approved",
                FileEntries = artifacts.PatchDiffReview.FileEntries
                    .Select(entry => entry with { ApprovalState = "approved", RejectionReason = string.Empty, ObservedUtc = observedUtc })
                    .ToArray(),
                Summary = "Diff review is fully approved.",
                ObservedUtc = observedUtc
            });
        WriteJson(
            BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot),
            artifacts.FileReviewDecision! with
            {
                OverallFileReviewState = "approved",
                Entries = artifacts.FileReviewDecision.Entries
                    .Select(entry => entry with { ApprovalState = "approved", RejectionReason = string.Empty, ObservedUtc = observedUtc })
                    .ToArray(),
                Summary = "All file review decisions are approved.",
                ObservedUtc = observedUtc
            });
        WriteJson(
            BuilderReviewWorkspaceService.PatchReviewOutcomePathForRepo(repoRoot),
            artifacts.PatchReviewOutcome! with
            {
                ReviewDecisionState = "approved",
                SessionState = "ready_to_finalize",
                ReviewState = "approved",
                ReviewNote = "All files approved.",
                Summary = "Patch review outcome is ready to finalize.",
                ObservedUtc = observedUtc
            });
        WriteJson(
            BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot),
            artifacts.PatchApplyDecision! with
            {
                OverallFileApprovalState = "approved",
                ApplyEligibilityState = "ready_to_finalize",
                BlockReasons = Array.Empty<string>(),
                FinalizationState = "ready_to_finalize",
                Summary = "Finalize is ready because every changed file is approved.",
                ObservedUtc = observedUtc
            });
    }

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
