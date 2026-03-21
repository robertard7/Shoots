using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderRecoveryPlaybookServiceTests
{
    [Fact]
    public void Refresh_recovery_playbooks_generates_deterministic_playbooks_for_rejected_route_failure_and_high_risk_stall()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);

            var first = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));
            var second = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(7));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.True(File.Exists(BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoB)));
            Assert.Contains(first!.Playbooks, playbook => string.Equals(playbook.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Playbooks, playbook => string.Equals(playbook.FailureClass, "route_failed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Playbooks, playbook => string.Equals(playbook.FailureClass, "high_risk_change_stalled", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Playbooks, playbook => string.Equals(playbook.FailureClass, "orchestration_blocked", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                first.Playbooks.Select(playbook => playbook.PlaybookId).ToArray(),
                second!.Playbooks.Select(playbook => playbook.PlaybookId).ToArray());
            Assert.Equal(
                first.FailurePatterns.Select(pattern => pattern.PatternId).ToArray(),
                second.FailurePatterns.Select(pattern => pattern.PatternId).ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Repeated_failure_history_produces_stable_playbook_with_evidence_and_steps()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            var workspaceIdB = BuilderWorkspaceService.ResolveWorkspaceId(repoB);
            BuilderRecoveryPlaybookTestData.AppendFailurePattern(
                repoB,
                new BuilderFailurePatternRecord(
                    "failure-repeat-manual",
                    workspaceIdB,
                    "builder_review_queue",
                    "floor",
                    "Second blocked outcome in repeated cycle.",
                    "blocked_by_rejection",
                    BuilderWorkspaceTestData.ObservedUtc.AddMinutes(8)));
            BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(9));

            var recovery = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(10));

            Assert.NotNull(recovery);
            var repeated = Assert.Single(recovery!.Playbooks, playbook => string.Equals(playbook.FailureClass, "repeated_failure_pattern", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("2 recorded occurrence", repeated.EvidenceBasis, StringComparison.OrdinalIgnoreCase);
            Assert.True(repeated.AdvisoryOnly);
            Assert.NotEmpty(repeated.RecommendedSteps);
            Assert.NotEmpty(repeated.ArtifactLinks);
            Assert.Contains(repeated.RecommendedSteps, step => string.Equals(step.ActionType, "stop_blind_retries", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Recovery_playbooks_do_not_change_route_resolution_or_finalize_gates()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(11));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;
            Assert.NotNull(beforeRoute);
            Assert.NotNull(afterRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.ApplyEligibilityState, afterApply!.ApplyEligibilityState);
            Assert.Equal(beforeApply.FinalizationState, afterApply.FinalizationState);
            Assert.Equal(beforeApply.BlockReasons.ToArray(), afterApply.BlockReasons.ToArray());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Cross_repo_coordination_respects_repo_independence_and_preserves_unaffected_workspace_state()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: false);
            var recovery = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                seeded.Descriptors,
                seeded.Orchestration,
                seeded.ActiveWorkspaceId,
                seeded.RequestId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(12));

            Assert.NotNull(recovery);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoB), recovery!.CrossRepoCoordination.BlockingRepoIds);
            Assert.Equal(BuilderWorkspaceService.ResolveWorkspaceId(repoB), recovery.CrossRepoCoordination.RecommendedRecoveryOrder[0]);
            Assert.Contains(recovery.CrossRepoCoordination.RepoIndependenceNotes, note => note.Contains("independent", StringComparison.OrdinalIgnoreCase));

            var applyA = BuilderReviewWorkspaceService.LoadArtifacts(repoA).PatchApplyDecision;
            Assert.NotNull(applyA);
            Assert.Equal("ready_to_finalize", applyA!.ApplyEligibilityState);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderRecoveryTests
{
    [Fact]
    public async Task Builder_recovery_guidance_panel_supports_filters_selection_and_artifact_links()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);

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

            Assert.True(viewModel.HasBuilderRecoveryPlaybooks);
            Assert.Contains("advisory only", viewModel.BuilderRecoveryAdvisoryBanner, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.HasBuilderRecoveryCoordinationSummary);

            viewModel.SelectedBuilderRecoveryFailureClassFilter = "patch_rejected";
            Assert.All(viewModel.BuilderRecoveryPlaybooks, playbook => Assert.Equal("patch_rejected", playbook.FailureClass));

            viewModel.SelectedBuilderRecoveryFailureClassFilter = "all";
            viewModel.SelectedBuilderRecoveryScopeFilter = "cross_repo_only";
            Assert.All(viewModel.BuilderRecoveryPlaybooks, playbook => Assert.True(playbook.IsCrossRepo));

            viewModel.SelectedBuilderRecoveryScopeFilter = "all";
            var selected = viewModel.BuilderRecoveryPlaybooks.First();
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(selected);

            Assert.True(viewModel.HasBuilderRecoverySelectedSteps);
            Assert.True(viewModel.HasBuilderRecoverySelectedArtifactLinks);
            Assert.True(viewModel.HasBuilderRecoverySelectedEvidenceBasis);
            Assert.Contains(selected.Playbook.Title, viewModel.BuilderRecoverySelectedTitle, StringComparison.OrdinalIgnoreCase);

            await viewModel.OpenBuilderRecoveryArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderRecoveryArtifactLinkCommand.ExecuteAsync(viewModel.BuilderRecoverySelectedArtifactLinks.First());
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

internal static class BuilderRecoveryPlaybookTestData
{
    public static (BuilderWorkspaceDescriptor[] Descriptors, BuilderCrossRepoOrchestrationContext Orchestration, string ActiveWorkspaceId, string RequestId) Seed(
        string repoA,
        string repoB,
        bool selectRepoB)
    {
        var descriptors = BuilderCrossRepoTestData.SeedCrossRepoWorkspaces(repoA, repoB);
        var activeWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(selectRepoB ? repoB : repoA);
        const string requestId = "runtime-host-change";
        var orchestration = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
            descriptors,
            activeWorkspaceId,
            requestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(4));
        Assert.NotNull(orchestration);

        BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
            descriptors,
            orchestration!,
            activeWorkspaceId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(5));
        BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
            descriptors,
            orchestration!,
            activeWorkspaceId,
            requestId,
            BuilderWorkspaceTestData.ObservedUtc.AddMinutes(6));

        return (descriptors, orchestration!, activeWorkspaceId, requestId);
    }

    public static void AppendFailurePattern(string repoRoot, BuilderFailurePatternRecord failure)
    {
        var existing = BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
        Assert.NotNull(existing);
        var updated = existing! with
        {
            Entries = existing.Entries
                .Concat(new[] { failure })
                .OrderByDescending(entry => entry.ObservedUtc)
                .ThenBy(entry => entry.Workspace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.RouteAttempted, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Summary = $"Builder failure memory retains {existing.Entries.Count + 1} deterministic failure pattern(s).",
            ObservedUtc = failure.ObservedUtc
        };

        File.WriteAllText(
            BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoRoot),
            JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
    }
}
