using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderKnowledgeGraphServiceTests
{
    [Fact]
    public void Refresh_knowledge_artifacts_creates_graph_failure_memory_and_deterministic_queries()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));

            Assert.NotNull(orchestration);

            var first = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));
            var second = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.True(File.Exists(BuilderKnowledgeGraphService.KnowledgeGraphPathForRepo(repoA)));
            Assert.True(File.Exists(BuilderKnowledgeGraphService.ExecutionPatternsPathForRepo(repoA)));
            Assert.True(File.Exists(BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoB)));
            Assert.Contains(second!.KnowledgeGraph.Entries, entry => string.Equals(entry.RelationshipType, "depends_on_workspace", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(second.KnowledgeGraph.Entries, entry => string.Equals(entry.NodeType, "file", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(second.ExecutionPatterns.Entries);
            Assert.Single(second.FailurePatterns.Entries);
            Assert.Equal(
                first!.WorkspaceDependencies.Select(entry => entry.Summary).ToArray(),
                second.WorkspaceDependencies.Select(entry => entry.Summary).ToArray());
            Assert.Equal(
                first.KnownFailureRoutes.Select(entry => entry.Summary).ToArray(),
                second.KnownFailureRoutes.Select(entry => entry.Summary).ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Execution_patterns_are_recorded_only_after_finalize()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            var initialOrchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));
            Assert.NotNull(initialOrchestration);

            var initialKnowledge = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                initialOrchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));
            Assert.NotNull(initialKnowledge);
            Assert.Empty(initialKnowledge!.ExecutionPatterns.Entries);

            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoA, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));
            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));

            var finalizedOrchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));
            Assert.NotNull(finalizedOrchestration);

            var finalizedKnowledge = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                finalizedOrchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));

            Assert.NotNull(finalizedKnowledge);
            var pattern = Assert.Single(finalizedKnowledge!.ExecutionPatterns.Entries);
            Assert.Equal("finalized", pattern.FinalizeResult);
            Assert.Equal(2, pattern.WorkspaceSequence.Count);
            Assert.NotEmpty(finalizedKnowledge.PriorSuccessfulRoutes);
            Assert.Contains("builder_prepared_route", finalizedKnowledge.PriorSuccessfulRoutes[0].Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Failure_patterns_capture_route_reason_and_rejection_state_without_changing_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));

            Assert.NotNull(orchestration);

            var before = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(before);

            var knowledge = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration!,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));

            Assert.NotNull(knowledge);
            var failure = Assert.Single(knowledge!.FailurePatterns.Entries);
            Assert.Equal(BuilderWorkspaceService.ResolveWorkspaceId(repoB), failure.Workspace);
            Assert.Equal("builder_review_queue", failure.RouteAttempted);
            Assert.Equal("blocked_by_rejection", failure.RejectionState);
            Assert.Contains(@"docs\legacy.md", failure.FailureReason, StringComparison.OrdinalIgnoreCase);

            var after = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(after);
            Assert.Equal(before!.ApplyEligibilityState, after!.ApplyEligibilityState);
            Assert.Equal(before.FinalizationState, after.FinalizationState);
            Assert.Equal(before.BlockReasons.ToArray(), after.BlockReasons.ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderKnowledgeTests
{
    [Fact]
    public async Task Builder_knowledge_panel_shows_patterns_dependencies_and_trends()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoA, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));
            BuilderKnowledgeGraphTestData.MarkWorkspaceFinalized(repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
            BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
                descriptors,
                BuilderWorkspaceService.ResolveWorkspaceId(repoA),
                "runtime-host-change",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8));

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

            Assert.True(viewModel.HasBuilderKnowledgePatterns);
            Assert.True(viewModel.HasBuilderKnowledgeDependencies);
            Assert.True(viewModel.HasBuilderKnowledgeSuccessfulRoutes);
            Assert.Contains("Successful route trends", viewModel.BuilderKnowledgeSuccessSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BuilderKnowledgeGraphService.KnowledgeGraphPathForRepo(repoA), viewModel.BuilderKnowledgeGraphArtifactPath);

            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.Equal(BuilderKnowledgeGraphService.KnowledgeGraphPathForRepo(repoB), viewModel.BuilderKnowledgeGraphArtifactPath);

            await viewModel.OpenBuilderKnowledgeGraphArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

internal static class BuilderKnowledgeGraphTestData
{
    public static void MarkWorkspaceFinalized(string repoRoot, DateTimeOffset observedUtc)
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
                SessionState = "finalized",
                ReviewState = "approved",
                ReviewNote = "All files approved and finalized.",
                Summary = "Patch review outcome is finalized.",
                ObservedUtc = observedUtc
            });
        WriteJson(
            BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot),
            artifacts.PatchApplyDecision! with
            {
                OverallFileApprovalState = "approved",
                ApplyEligibilityState = "finalized",
                BlockReasons = Array.Empty<string>(),
                FinalizationState = "finalized",
                Summary = "Finalize completed after every changed file was approved.",
                ObservedUtc = observedUtc
            });

        BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
            repoRoot,
            new BuilderReviewWorkspacePreferences("all", "directory", string.Empty),
            observedUtc: observedUtc);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
