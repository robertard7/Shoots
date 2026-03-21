using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderCrossRepoOrchestrationServiceTests
{
    [Fact]
    public void Refresh_orchestration_artifacts_generates_plan_segments_and_execution_state()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);

            var context = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(2));

            Assert.NotNull(context);
            Assert.Equal("runtime-host-change", context!.Plan.RequestId);
            Assert.Equal(2, context.Plan.ParticipatingWorkspaceIds.Count);
            Assert.Equal(10, context.Plan.StepSequence.Count);
            Assert.Equal(repoB, context.Plan.RepoOrder[0]);
            Assert.Equal(2, context.Segments.Segments.Count);
            Assert.Contains(context.Segments.Segments, segment => string.Equals(segment.WorkspaceId, BuilderWorkspaceService.ResolveWorkspaceId(repoA), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(context.Segments.Segments, segment => string.Equals(segment.WorkspaceId, BuilderWorkspaceService.ResolveWorkspaceId(repoB), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("blocked_by_rejection", context.ExecutionState.FinalizeReadiness);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoB), context.ExecutionState.RejectedSegments);

            Assert.True(File.Exists(BuilderCrossRepoOrchestrationService.CrossRepoPlanPathForRepo(repoA)));
            Assert.True(File.Exists(BuilderCrossRepoOrchestrationService.CrossRepoPlanPathForRepo(repoB)));
            Assert.True(File.Exists(BuilderCrossRepoOrchestrationService.WorkspaceTaskSegmentsPathForRepo(repoA)));
            Assert.True(File.Exists(BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoB)));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Orchestration_does_not_auto_finalize_ready_workspace_when_another_repo_is_rejected()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);

            var context = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(3));

            Assert.NotNull(context);
            var repoAApplyDecision = BuilderReviewWorkspaceService.LoadArtifacts(repoA).PatchApplyDecision;
            Assert.NotNull(repoAApplyDecision);
            Assert.Equal("ready_to_finalize", repoAApplyDecision!.ApplyEligibilityState);
            Assert.Equal("ready_to_finalize", repoAApplyDecision.FinalizationState);

            var repoAStatus = Assert.Single(context!.ExecutionState.WorkspaceStatusList, status => string.Equals(status.WorkspaceId, BuilderWorkspaceService.ResolveWorkspaceId(repoA), StringComparison.OrdinalIgnoreCase));
            var repoBStatus = Assert.Single(context.ExecutionState.WorkspaceStatusList, status => string.Equals(status.WorkspaceId, BuilderWorkspaceService.ResolveWorkspaceId(repoB), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("in_review", repoAStatus.ExecutionState);
            Assert.False(repoAStatus.Finalized);
            Assert.Equal("rejected", repoBStatus.ExecutionState);
            Assert.True(repoBStatus.RejectedSegment);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderCrossRepoTests
{
    [Fact]
    public async Task Builder_cross_repo_panel_reflects_per_workspace_execution_state()
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

            Assert.True(viewModel.HasBuilderCrossRepoWorkspaceStatuses);
            Assert.Equal("Blocked by rejection", viewModel.BuilderCrossRepoFinalizeReadinessBadge);
            Assert.Contains("2 workspace(s)", viewModel.BuilderCrossRepoPlanSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, viewModel.BuilderCrossRepoWorkspaceStatuses.Count);
            Assert.Contains(viewModel.BuilderCrossRepoWorkspaceStatuses, row => row.IsRejectedBlocker);

            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.Equal(BuilderCrossRepoOrchestrationService.CrossRepoPlanPathForRepo(repoB), viewModel.BuilderCrossRepoPlanArtifactPath);
            Assert.Equal(2, viewModel.BuilderCrossRepoWorkspaceStatuses.Count);
            Assert.Contains(viewModel.BuilderCrossRepoWorkspaceStatuses, row => row.IsSelectedWorkspace && string.Equals(row.WorkspaceId, BuilderWorkspaceService.ResolveWorkspaceId(repoB), StringComparison.OrdinalIgnoreCase));

            await viewModel.OpenBuilderCrossRepoPlanArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

internal static class BuilderCrossRepoTestData
{
    public static BuilderWorkspaceDescriptor[] SeedCrossRepoWorkspaces(string repoA, string repoB)
    {
        var scanner = BuilderWorkspaceTestData.CreateScanner(
            repoA,
            new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));
        scanner.AddObservations(
            repoB,
            new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));

        BuilderReviewWorkspaceTestData.SeedArtifacts(repoA, "cross-runtime");
        BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoB, "cross-host");
        MarkWorkspaceReadyToFinalize(repoA, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

        var descriptors = new[]
        {
            BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
            BuilderWorkspaceService.CreateDescriptor(repoB, "host-b")
        };

        var contextA = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
            descriptors,
            new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoA),
            scanner,
            BuilderWorkspaceTestData.ObservedUtc,
            forceCapabilityScan: true);
        var contextB = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
            descriptors,
            new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoB),
            scanner,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1),
            forceCapabilityScan: true);
        Assert.NotNull(contextA);
        Assert.NotNull(contextB);

        BuilderWorkspaceService.RecordRouteResolution(contextA!.Context, "runtime-host-change", "builder_prepared_route", BuilderWorkspaceTestData.ObservedUtc);
        BuilderWorkspaceService.RecordRouteResolution(contextB!.Context, "runtime-host-change", "builder_review_queue", BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

        BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
            repoA,
            new BuilderReviewWorkspacePreferences("all", "directory", string.Empty),
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));
        BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
            repoB,
            new BuilderReviewWorkspacePreferences("all", "directory", string.Empty),
            observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

        return descriptors;
    }

    private static void MarkWorkspaceReadyToFinalize(string repoRoot, DateTimeOffset observedUtc)
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

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
