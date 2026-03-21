using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Blueprints;
using Shoots.UI.Builder;
using Shoots.UI.Environment;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Services.Backends;
using Shoots.UI.Settings;
using Shoots.UI.ViewModels;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderReviewWorkspaceServiceTests
{
    [Fact]
    public void Refresh_workspace_artifacts_writes_workspace_navigation_efficiency_and_history()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-52");

            var initial = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "review_state", string.Empty),
                observedUtc: seeded.ObservedUtc);

            Assert.NotNull(initial);
            Assert.Equal("session-52", initial!.Workspace.ExecutionSessionId);
            Assert.Equal("review_state", initial.Workspace.GroupingUsed);
            Assert.Equal(4, initial.Workspace.ReviewCounts.TotalChangedFiles);
            Assert.Equal(1, initial.Workspace.ReviewCounts.PendingFiles);
            Assert.Equal(1, initial.Workspace.ReviewCounts.ApprovedFiles);
            Assert.Equal(1, initial.Workspace.ReviewCounts.RejectedFiles);
            Assert.Equal(1, initial.Workspace.ReviewCounts.NeedsRevisionFiles);
            Assert.Equal("blocked_by_rejection", initial.Workspace.ReviewCounts.FinalizeEligibilityState);
            Assert.Equal(seeded.PendingFilePath, initial.NavigationState.CurrentFilePath);
            Assert.Equal(seeded.PendingFilePath, initial.NavigationState.NextPendingFile);
            Assert.Equal(seeded.RejectedFilePath, initial.NavigationState.NextRejectedFile);
            Assert.Equal(75d, initial.EfficiencySummary.ApprovalCompletionPercentage);
            Assert.Equal(seeded.RelatedTestFilePath, initial.Workspace.FileGroups
                .SelectMany(group => group.Files)
                .Single(file => string.Equals(file.RelativePath, seeded.ApprovedFilePath, StringComparison.Ordinal))
                .RelatedTestFilePath);
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewNavigationStatePathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewEfficiencySummaryPathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewWorkspaceHistoryPathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewQueuePathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.ReviewQueueNavigationPathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderReviewWorkspaceService.BatchReviewActionsPathForRepo(repoRoot)));

            var filtered = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("pending_only", "directory", seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(1));

            Assert.NotNull(filtered);
            Assert.Equal("pending_only", filtered!.NavigationState.CurrentFilter);
            Assert.Single(filtered.Workspace.FileGroups);
            Assert.Equal(seeded.PendingFilePath, filtered.Workspace.FileGroups[0].Files.Single().RelativePath);
            Assert.Equal(2, filtered.WorkspaceHistory.Entries.Count);
            Assert.Contains("all", filtered.WorkspaceHistory.Entries[0].FiltersUsed);
            Assert.Contains("pending_only", filtered.WorkspaceHistory.Entries[0].FiltersUsed);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_workspace_artifacts_prunes_history_to_requested_retention()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-a");
            BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "directory", string.Empty),
                historyRetentionCount: 2,
                observedUtc: new DateTimeOffset(2026, 03, 14, 18, 0, 0, TimeSpan.Zero));

            BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-b");
            BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("approved_only", "file_category", string.Empty),
                historyRetentionCount: 2,
                observedUtc: new DateTimeOffset(2026, 03, 14, 18, 5, 0, TimeSpan.Zero));

            BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-c");
            var latest = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("rejected_only", "review_state", string.Empty),
                historyRetentionCount: 2,
                observedUtc: new DateTimeOffset(2026, 03, 14, 18, 10, 0, TimeSpan.Zero));

            Assert.NotNull(latest);
            Assert.Equal(new[] { "session-c", "session-b" }, latest!.WorkspaceHistory.Entries.Select(entry => entry.SessionId).ToArray());
            Assert.Contains("rejected_only", latest.WorkspaceHistory.Entries[0].FiltersUsed);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_workspace_artifacts_builds_priority_queue_and_high_risk_flags()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-53");

            var context = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences("all", "directory", seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc);

            Assert.NotNull(context);
            Assert.Equal(
                new[]
                {
                    seeded.RejectedFilePath,
                    seeded.NeedsRevisionFilePath,
                    seeded.HighRiskFilePath,
                    seeded.PendingFilePath,
                    seeded.ApprovedFilePath
                },
                context!.Queue.QueueOrder.ToArray());
            var highRisk = Assert.Single(context.HighRiskFlags.Entries);
            Assert.Equal(seeded.HighRiskFilePath, highRisk.FilePath);
            Assert.Equal("build_system", highRisk.RiskCategory);
            Assert.True(highRisk.RequiresExplicitApproval);
            Assert.Equal(seeded.RejectedFilePath, context.QueueNavigation.NextPriorityFile);
            Assert.Equal(seeded.HighRiskFilePath, context.QueueNavigation.NextHighRiskFile);
            Assert.Equal(seeded.RejectedFilePath, context.QueueNavigation.NextRejectedFile);
            Assert.Equal(seeded.NeedsRevisionFilePath, context.QueueNavigation.NextRevisionFile);
            Assert.Empty(context.BatchReviewActions.Entries);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Apply_batch_review_action_approves_non_high_risk_pending_files_and_keeps_high_risk_pending()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-53");

            var context = BuilderReviewWorkspaceService.ApplyBatchReviewAction(
                repoRoot,
                new BuilderBatchReviewActionRequest(
                    "approve_pending_in_filter",
                    "filter",
                    "pending_only",
                    "pending_only",
                    "directory",
                    seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(1));

            Assert.NotNull(context);
            var decisions = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).FileReviewDecision;
            Assert.NotNull(decisions);
            Assert.Equal("approved", decisions!.Entries.Single(entry => entry.RelativePath == seeded.PendingFilePath).ApprovalState);
            Assert.Equal("pending_review", decisions.Entries.Single(entry => entry.RelativePath == seeded.HighRiskFilePath).ApprovalState);

            var batchActions = BuilderReviewWorkspaceService.LoadBatchReviewActions(repoRoot);
            var batchAction = Assert.Single(batchActions.Entries);
            Assert.Equal("approve_pending_in_filter", batchAction.ActionType);
            Assert.Equal(new[] { seeded.PendingFilePath }, batchAction.AffectedFiles.ToArray());

            var applyDecision = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).PatchApplyDecision;
            Assert.NotNull(applyDecision);
            Assert.Equal("blocked_by_rejection", applyDecision!.ApplyEligibilityState);
            Assert.Contains($"Rejected file {seeded.RejectedFilePath} must be resolved before finalize.", applyDecision.BlockReasons);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Apply_batch_review_action_can_reverse_group_state_before_finalize()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-53");

            BuilderReviewWorkspaceService.ApplyBatchReviewAction(
                repoRoot,
                new BuilderBatchReviewActionRequest(
                    "reject_all_in_group",
                    "group",
                    @"ui\Shoots.Ui",
                    "all",
                    "directory",
                    seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(1));

            BuilderReviewWorkspaceService.ApplyBatchReviewAction(
                repoRoot,
                new BuilderBatchReviewActionRequest(
                    "mark_group_needs_revision",
                    "group",
                    @"ui\Shoots.Ui",
                    "all",
                    "directory",
                    seeded.PendingFilePath),
                observedUtc: seeded.ObservedUtc.AddMinutes(2));

            var decisions = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).FileReviewDecision;
            Assert.NotNull(decisions);
            Assert.Equal("needs_revision", decisions!.Entries.Single(entry => entry.RelativePath == seeded.PendingFilePath).ApprovalState);

            var batchActions = BuilderReviewWorkspaceService.LoadBatchReviewActions(repoRoot);
            Assert.Equal(2, batchActions.Entries.Count);
            Assert.Equal("mark_group_needs_revision", batchActions.Entries[0].ActionType);
            Assert.Equal("reject_all_in_group", batchActions.Entries[1].ActionType);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderReviewWorkspaceTests
{
    [Fact]
    public async Task Builder_review_workspace_loads_navigation_and_copy_surface_from_artifacts()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-52");
            var shell = new RecordingWorkspaceShellService();
            var viewModel = BuilderReviewWorkspaceTestData.CreateViewModel(repoRoot, shell);

            Assert.True(viewModel.HasBuilderReviewGroups);
            Assert.Equal("Blocked by rejection", viewModel.BuilderReviewFinalizeBadge);
            Assert.Contains(seeded.PendingFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);

            await viewModel.SelectFirstBuilderReviewRejectedFileCommand.ExecuteAsync();
            Assert.Contains(seeded.RejectedFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);

            await viewModel.CopyBuilderCurrentReviewSummaryCommand.ExecuteAsync();
            Assert.Single(shell.CopiedTexts);
            Assert.Contains("Rejection reason: Operator rejected deleted documentation.", shell.CopiedTexts[0], StringComparison.Ordinal);

            await viewModel.OpenBuilderReviewWorkspaceArtifactCommand.ExecuteAsync();
            Assert.Contains(BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot), shell.OpenedPaths);

            await viewModel.SelectNextBuilderReviewPendingFileCommand.ExecuteAsync();
            Assert.Contains(seeded.PendingFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_review_workspace_filter_and_group_selection_rebuilds_visible_rows()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedArtifacts(repoRoot, "session-52");
            var viewModel = BuilderReviewWorkspaceTestData.CreateViewModel(repoRoot, new RecordingWorkspaceShellService());

            viewModel.SelectedBuilderReviewFilter = "approved_only";
            viewModel.SelectedBuilderReviewGrouping = "file_category";

            var approvedGroup = Assert.Single(viewModel.BuilderReviewGroups);
            Assert.Single(approvedGroup.Files);
            Assert.Equal(seeded.ApprovedFilePath, approvedGroup.Files[0].RelativePath);

            await viewModel.SelectFirstBuilderReviewFileInGroupCommand.ExecuteAsync(approvedGroup);
            Assert.Contains(seeded.ApprovedFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);
            Assert.Equal(seeded.RelatedTestFilePath, viewModel.BuilderReviewCurrentFileRelatedTestPath);

            viewModel.SelectedBuilderReviewFilter = "pending_only";
            var pendingGroup = Assert.Single(viewModel.BuilderReviewGroups);
            Assert.Single(pendingGroup.Files);
            Assert.Equal(seeded.PendingFilePath, pendingGroup.Files[0].RelativePath);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_review_workspace_queue_navigation_reveals_priority_and_filtered_targets()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-53");
            var viewModel = BuilderReviewWorkspaceTestData.CreateViewModel(repoRoot, new RecordingWorkspaceShellService());

            Assert.Contains("High-risk=1", viewModel.BuilderReviewCountsSummary, StringComparison.Ordinal);

            await viewModel.SelectHighestPriorityBuilderReviewFileCommand.ExecuteAsync();
            Assert.Contains(seeded.RejectedFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);

            viewModel.SelectedBuilderReviewFilter = "approved_only";
            await viewModel.SelectNextBuilderReviewHighRiskFileCommand.ExecuteAsync();

            Assert.Equal("all", viewModel.SelectedBuilderReviewFilter);
            Assert.Contains(seeded.HighRiskFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);
            Assert.Contains("High risk", viewModel.BuilderReviewCurrentFileHighRiskBadge, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_review_workspace_batch_approve_skips_high_risk_and_keeps_finalize_blocked()
    {
        var repoRoot = BuilderReviewWorkspaceTestData.CreateRepoRoot();
        try
        {
            var seeded = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-53");
            var viewModel = BuilderReviewWorkspaceTestData.CreateViewModel(repoRoot, new RecordingWorkspaceShellService());

            await viewModel.SelectFirstBuilderReviewPendingFileCommand.ExecuteAsync();
            Assert.True(viewModel.HasBuilderReviewCurrentFile);

            await viewModel.ApprovePendingBuilderReviewFilterCommand.ExecuteAsync();

            Assert.Contains("Latest batch action", viewModel.BuilderReviewBatchActionSummary, StringComparison.Ordinal);
            Assert.Contains("Pending=1", viewModel.BuilderReviewCountsSummary, StringComparison.Ordinal);
            Assert.Equal("Blocked by rejection", viewModel.BuilderReviewFinalizeBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }
}

internal static class BuilderReviewWorkspaceTestData
{
    public static string CreateRepoRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-builder-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        return root;
    }

    public static SeededBuilderReviewArtifacts SeedArtifacts(string repoRoot, string sessionId)
    {
        var observedUtc = new DateTimeOffset(2026, 03, 14, 18, 30, 00, TimeSpan.Zero);
        var builderRoot = BuilderExecutionService.BuilderProofRootForRepo(repoRoot);
        Directory.CreateDirectory(builderRoot);

        var approvedFilePath = @"src\Shoots.Core\NewFeature.cs";
        var pendingFilePath = @"ui\Shoots.Ui\MainWindow.xaml";
        var rejectedFilePath = @"docs\legacy.md";
        var needsRevisionFilePath = @"src\Shoots.Core.Tests\NewFeatureTests.cs";

        WriteFile(repoRoot, approvedFilePath, "namespace Shoots.Core;\npublic sealed class NewFeature { }\n");
        WriteFile(repoRoot, pendingFilePath, "<Grid />\n");
        WriteFile(repoRoot, needsRevisionFilePath, "namespace Shoots.Core.Tests;\npublic sealed class NewFeatureTests { }\n");

        var executionSessionPath = BuilderReviewWorkspaceService.ConversationExecutionSessionPathForRepo(repoRoot);
        var patchReviewPath = BuilderReviewWorkspaceService.PatchReviewPathForRepo(repoRoot);
        var patchDiffReviewPath = BuilderReviewWorkspaceService.PatchDiffReviewPathForRepo(repoRoot);
        var fileDecisionPath = BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot);
        var patchOutcomePath = BuilderReviewWorkspaceService.PatchReviewOutcomePathForRepo(repoRoot);
        var patchApplyPath = BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot);
        var patchBundlePath = BuilderReviewWorkspaceService.PatchBundlePathForRepo(repoRoot);

        WriteJson(executionSessionPath, new BuilderConversationExecutionSessionRecord(
            sessionId, "intake-52", "handoff-52", "Improve operator review workspace.", "ui_workflow", "builder", "dotnet", ".NET",
            "WPF workspace", "awaiting_review", "patch_review", "Patch review", "pending_review", "Validation passed.", string.Empty,
            string.Empty, string.Empty, patchReviewPath, patchOutcomePath,
            new[]
            {
                new BuilderConversationChangedFileRecord(approvedFilePath, "source_code", "created", "Adds the new feature source file.", true),
                new BuilderConversationChangedFileRecord(pendingFilePath, "ui_markup", "modified", "Adds the review workspace panel.", true),
                new BuilderConversationChangedFileRecord(rejectedFilePath, "docs", "deleted", "Removes legacy operator notes.", true),
                new BuilderConversationChangedFileRecord(needsRevisionFilePath, "test_code", "modified", "Extends regression coverage.", true)
            },
            Array.Empty<BuilderConversationStageRecord>(),
            new[] { patchReviewPath, patchDiffReviewPath, fileDecisionPath, patchApplyPath },
            "Execution session is awaiting operator review.", executionSessionPath, observedUtc));

        WriteJson(patchReviewPath, new BuilderPatchReviewRecord(
            sessionId, "intake-52", "handoff-52", "builder", "dotnet", ".NET", "Validation passed.", "ready",
            new[]
            {
                new BuilderPatchReviewChangedFileRecord(approvedFilePath, "source_code", "created", "Adds the new feature source file.", true),
                new BuilderPatchReviewChangedFileRecord(pendingFilePath, "ui_markup", "modified", "Adds the review workspace panel.", true),
                new BuilderPatchReviewChangedFileRecord(rejectedFilePath, "docs", "deleted", "Removes legacy operator notes.", true),
                new BuilderPatchReviewChangedFileRecord(needsRevisionFilePath, "test_code", "modified", "Extends regression coverage.", true)
            },
            new[] { patchDiffReviewPath, fileDecisionPath, patchApplyPath },
            "Patch review captured four changed files.", patchReviewPath, observedUtc));

        WriteJson(patchDiffReviewPath, new BuilderPatchDiffReviewRecord(
            sessionId, $"patch-review-{sessionId}", patchReviewPath, "rejected_file_present", "ready",
            new[]
            {
                new BuilderPatchDiffReviewFileEntryRecord(approvedFilePath, "source_code", "created", "Adds the NewFeature type.",
                    "--- a/src/Shoots.Core/NewFeature.cs\n+++ b/src/Shoots.Core/NewFeature.cs\n@@ -0,0 +1,2 @@\n+namespace Shoots.Core;\n+public sealed class NewFeature { }\n",
                    "approved", string.Empty, observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(pendingFilePath, "ui_markup", "modified", "Adds the review workspace expander to the main window.", string.Empty, "pending_review", string.Empty, observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(rejectedFilePath, "docs", "deleted", "Deletes obsolete operator notes.", string.Empty, "rejected", "Operator rejected deleted documentation.", observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(needsRevisionFilePath, "test_code", "modified", "Extends coverage for the new feature.", string.Empty, "needs_revision", "Add one more regression assertion.", observedUtc)
            },
            new[] { patchReviewPath, fileDecisionPath, patchApplyPath },
            "Diff review is ready for file-level decisions.", patchDiffReviewPath, observedUtc));

        WriteJson(fileDecisionPath, new BuilderFileReviewDecisionRecord(
            sessionId, $"patch-diff-review-{sessionId}", "rejected_file_present",
            new[]
            {
                new BuilderFileReviewDecisionEntryRecord(approvedFilePath, "approved", "operator", string.Empty, Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(pendingFilePath, "pending_review", "operator", string.Empty, Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(rejectedFilePath, "rejected", "operator", "Operator rejected deleted documentation.", Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(needsRevisionFilePath, "needs_revision", "operator", "Add one more regression assertion.", Array.Empty<string>(), observedUtc)
            },
            new[] { patchDiffReviewPath, patchApplyPath },
            "File review decisions recorded.", fileDecisionPath, observedUtc));

        WriteJson(patchOutcomePath, new BuilderPatchReviewOutcomeRecord(
            sessionId, "rejected_file_present", "awaiting_revision", "blocked", "A rejected file keeps finalization blocked.",
            string.Empty, new[] { fileDecisionPath, patchApplyPath }, "Patch review outcome remains blocked by file review.", patchOutcomePath, observedUtc));

        WriteJson(patchApplyPath, new BuilderPatchApplyDecisionRecord(
            sessionId, "rejected_file_present", "blocked_by_rejection",
            new[] { "Rejected file docs\\legacy.md must be resolved before finalize." }, "blocked_by_rejection",
            new[] { fileDecisionPath, patchOutcomePath }, "Finalize is blocked while any file remains rejected.", patchApplyPath, observedUtc));

        File.WriteAllText(patchBundlePath, string.Join(System.Environment.NewLine, new[]
        {
            "--- a/src/Shoots.Core/NewFeature.cs",
            "+++ b/src/Shoots.Core/NewFeature.cs",
            "@@ -0,0 +1,2 @@",
            "+namespace Shoots.Core;",
            "+public sealed class NewFeature { }",
            "--- a/ui/Shoots.Ui/MainWindow.xaml",
            "+++ b/ui/Shoots.Ui/MainWindow.xaml",
            "@@ -8,0 +9,12 @@",
            "+<Expander Header=\"Review Workspace\">",
            "+  <TextBlock Text=\"Bounded diff excerpt\" />",
            "+</Expander>"
        }));

        return new SeededBuilderReviewArtifacts(approvedFilePath, pendingFilePath, rejectedFilePath, needsRevisionFilePath, needsRevisionFilePath, observedUtc);
    }

    public static SeededBuilderReviewQueueArtifacts SeedQueueArtifacts(string repoRoot, string sessionId)
    {
        var observedUtc = new DateTimeOffset(2026, 03, 14, 19, 00, 00, TimeSpan.Zero);
        var builderRoot = BuilderExecutionService.BuilderProofRootForRepo(repoRoot);
        Directory.CreateDirectory(builderRoot);

        var approvedFilePath = @"src\Shoots.Core\NewFeature.cs";
        var pendingFilePath = @"ui\Shoots.Ui\MainWindow.xaml";
        var rejectedFilePath = @"docs\legacy.md";
        var needsRevisionFilePath = @"src\Shoots.Core.Tests\NewFeatureTests.cs";
        var highRiskFilePath = @"src\Shoots.Core\Shoots.Core.csproj";

        WriteFile(repoRoot, approvedFilePath, "namespace Shoots.Core;\npublic sealed class NewFeature { }\n");
        WriteFile(repoRoot, pendingFilePath, "<Grid />\n");
        WriteFile(repoRoot, needsRevisionFilePath, "namespace Shoots.Core.Tests;\npublic sealed class NewFeatureTests { }\n");
        WriteFile(repoRoot, highRiskFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        var executionSessionPath = BuilderReviewWorkspaceService.ConversationExecutionSessionPathForRepo(repoRoot);
        var patchReviewPath = BuilderReviewWorkspaceService.PatchReviewPathForRepo(repoRoot);
        var patchDiffReviewPath = BuilderReviewWorkspaceService.PatchDiffReviewPathForRepo(repoRoot);
        var fileDecisionPath = BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot);
        var patchOutcomePath = BuilderReviewWorkspaceService.PatchReviewOutcomePathForRepo(repoRoot);
        var patchApplyPath = BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot);
        var patchBundlePath = BuilderReviewWorkspaceService.PatchBundlePathForRepo(repoRoot);

        WriteJson(executionSessionPath, new BuilderConversationExecutionSessionRecord(
            sessionId, "intake-53", "handoff-53", "Scale the review queue.", "ui_workflow", "builder", "dotnet", ".NET",
            "WPF workspace", "awaiting_review", "patch_review", "Patch review", "pending_review", "Validation passed.", string.Empty,
            string.Empty, string.Empty, patchReviewPath, patchOutcomePath,
            new[]
            {
                new BuilderConversationChangedFileRecord(approvedFilePath, "source_code", "created", "Adds the new feature source file.", true),
                new BuilderConversationChangedFileRecord(pendingFilePath, "ui_markup", "modified", "Adds the review queue panel.", true),
                new BuilderConversationChangedFileRecord(rejectedFilePath, "docs", "deleted", "Removes legacy operator notes.", true),
                new BuilderConversationChangedFileRecord(needsRevisionFilePath, "test_code", "modified", "Extends regression coverage.", true),
                new BuilderConversationChangedFileRecord(highRiskFilePath, "build_config", "modified", "Updates the core project file.", true)
            },
            Array.Empty<BuilderConversationStageRecord>(),
            new[] { patchReviewPath, patchDiffReviewPath, fileDecisionPath, patchApplyPath },
            "Execution session is awaiting operator review.", executionSessionPath, observedUtc));

        WriteJson(patchReviewPath, new BuilderPatchReviewRecord(
            sessionId, "intake-53", "handoff-53", "builder", "dotnet", ".NET", "Validation passed.", "ready",
            new[]
            {
                new BuilderPatchReviewChangedFileRecord(approvedFilePath, "source_code", "created", "Adds the new feature source file.", true),
                new BuilderPatchReviewChangedFileRecord(pendingFilePath, "ui_markup", "modified", "Adds the review queue panel.", true),
                new BuilderPatchReviewChangedFileRecord(rejectedFilePath, "docs", "deleted", "Removes legacy operator notes.", true),
                new BuilderPatchReviewChangedFileRecord(needsRevisionFilePath, "test_code", "modified", "Extends regression coverage.", true),
                new BuilderPatchReviewChangedFileRecord(highRiskFilePath, "build_config", "modified", "Updates the core project file.", true)
            },
            new[] { patchDiffReviewPath, fileDecisionPath, patchApplyPath },
            "Patch review captured five changed files.", patchReviewPath, observedUtc));

        WriteJson(patchDiffReviewPath, new BuilderPatchDiffReviewRecord(
            sessionId, $"patch-review-{sessionId}", patchReviewPath, "rejected_file_present", "ready",
            new[]
            {
                new BuilderPatchDiffReviewFileEntryRecord(approvedFilePath, "source_code", "created", "Adds the NewFeature type.",
                    "--- a/src/Shoots.Core/NewFeature.cs\n+++ b/src/Shoots.Core/NewFeature.cs\n@@ -0,0 +1,2 @@\n+namespace Shoots.Core;\n+public sealed class NewFeature { }\n",
                    "approved", string.Empty, observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(pendingFilePath, "ui_markup", "modified", "Adds the review queue expander to the main window.", string.Empty, "pending_review", string.Empty, observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(rejectedFilePath, "docs", "deleted", "Deletes obsolete operator notes.", string.Empty, "rejected", "Operator rejected deleted documentation.", observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(needsRevisionFilePath, "test_code", "modified", "Extends coverage for the new feature.", string.Empty, "needs_revision", "Add one more regression assertion.", observedUtc),
                new BuilderPatchDiffReviewFileEntryRecord(highRiskFilePath, "build_config", "modified", "Updates the core project file and build graph.", string.Empty, "pending_review", string.Empty, observedUtc)
            },
            new[] { patchReviewPath, fileDecisionPath, patchApplyPath },
            "Diff review is ready for file-level decisions.", patchDiffReviewPath, observedUtc));

        WriteJson(fileDecisionPath, new BuilderFileReviewDecisionRecord(
            sessionId, $"patch-diff-review-{sessionId}", "rejected_file_present",
            new[]
            {
                new BuilderFileReviewDecisionEntryRecord(approvedFilePath, "approved", "operator", string.Empty, Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(pendingFilePath, "pending_review", "operator", string.Empty, Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(rejectedFilePath, "rejected", "operator", "Operator rejected deleted documentation.", Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(needsRevisionFilePath, "needs_revision", "operator", "Add one more regression assertion.", Array.Empty<string>(), observedUtc),
                new BuilderFileReviewDecisionEntryRecord(highRiskFilePath, "pending_review", "operator", string.Empty, Array.Empty<string>(), observedUtc)
            },
            new[] { patchDiffReviewPath, patchApplyPath },
            "File review decisions recorded.", fileDecisionPath, observedUtc));

        WriteJson(patchOutcomePath, new BuilderPatchReviewOutcomeRecord(
            sessionId, "rejected_file_present", "awaiting_revision", "blocked", "A rejected file keeps finalization blocked.",
            string.Empty, new[] { fileDecisionPath, patchApplyPath }, "Patch review outcome remains blocked by file review.", patchOutcomePath, observedUtc));

        WriteJson(patchApplyPath, new BuilderPatchApplyDecisionRecord(
            sessionId, "rejected_file_present", "blocked_by_rejection",
            new[] { "Rejected file docs\\legacy.md must be resolved before finalize." }, "blocked_by_rejection",
            new[] { fileDecisionPath, patchOutcomePath }, "Finalize is blocked while any file remains rejected.", patchApplyPath, observedUtc));

        File.WriteAllText(patchBundlePath, string.Join(System.Environment.NewLine, new[]
        {
            "--- a/src/Shoots.Core/NewFeature.cs",
            "+++ b/src/Shoots.Core/NewFeature.cs",
            "@@ -0,0 +1,2 @@",
            "+namespace Shoots.Core;",
            "+public sealed class NewFeature { }",
            "--- a/src/Shoots.Core/Shoots.Core.csproj",
            "+++ b/src/Shoots.Core/Shoots.Core.csproj",
            "@@ -1,1 +1,2 @@",
            " <Project Sdk=\"Microsoft.NET.Sdk\">",
            "+  <PropertyGroup />",
            "--- a/ui/Shoots.Ui/MainWindow.xaml",
            "+++ b/ui/Shoots.Ui/MainWindow.xaml",
            "@@ -8,0 +9,12 @@",
            "+<Expander Header=\"Review Queue\">",
            "+  <TextBlock Text=\"Bounded diff excerpt\" />",
            "+</Expander>"
        }));

        return new SeededBuilderReviewQueueArtifacts(
            approvedFilePath,
            pendingFilePath,
            rejectedFilePath,
            needsRevisionFilePath,
            highRiskFilePath,
            needsRevisionFilePath,
            observedUtc);
    }

    public static MainWindowViewModel CreateViewModel(string repoRoot, RecordingWorkspaceShellService workspaceShell)
    {
        var stateRoot = Path.Combine(repoRoot, ".test-state");
        Directory.CreateDirectory(stateRoot);

        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new TestEnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            new TestWorkspaceProvider(repoRoot),
            workspaceShell,
            new InMemoryDatabaseIntentStore(),
            new ToolTierPrompt(),
            new SystemBlueprintStore(Path.Combine(stateRoot, "blueprints")),
            new ExecutionEnvironmentSettingsStore(Path.Combine(stateRoot, "execution")),
            new AiPolicyStore(Path.Combine(stateRoot, "ai-policy")),
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            new TestBackendProbeService(),
            new TestOllamaClient(),
            validationSettingsStore: new ValidationSettingsStore(Path.Combine(stateRoot, "validation-settings")),
            validationRunnerService: new ValidationRunnerService(repoRoot),
            autoRefreshBackends: false,
            builderToolchainCapabilityScanner: BuilderWorkspaceTestData.CreateScanner(
                repoRoot,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc)));
    }

    private static void WriteFile(string repoRoot, string relativePath, string contents)
    {
        var path = Path.Combine(repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents.Replace("\n", System.Environment.NewLine, StringComparison.Ordinal));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record SeededBuilderReviewArtifacts(
    string ApprovedFilePath,
    string PendingFilePath,
    string RejectedFilePath,
    string NeedsRevisionFilePath,
    string RelatedTestFilePath,
    DateTimeOffset ObservedUtc);

internal sealed record SeededBuilderReviewQueueArtifacts(
    string ApprovedFilePath,
    string PendingFilePath,
    string RejectedFilePath,
    string NeedsRevisionFilePath,
    string HighRiskFilePath,
    string RelatedTestFilePath,
    DateTimeOffset ObservedUtc);

internal sealed class RecordingWorkspaceShellService : IWorkspaceShellService
{
    public List<string> OpenedPaths { get; } = new();
    public List<string> CopiedTexts { get; } = new();
    public bool OpenFolder(string path) { OpenedPaths.Add(path); return true; }
    public Task OpenFolderAsync(string path, CancellationToken ct = default) { OpenedPaths.Add(path); return Task.CompletedTask; }
    public Task CopyTextAsync(string text, CancellationToken ct = default) { CopiedTexts.Add(text); return Task.CompletedTask; }
}

internal sealed class TestEnvironmentProfileService : IEnvironmentProfileService
{
    private static readonly IEnvironmentProfile Profile = new TestEnvironmentProfile();
    public IReadOnlyList<IEnvironmentProfile> Profiles { get; } = new[] { Profile };
    public EnvironmentProfileResult? LastResult => null;
    public EnvironmentCapability AvailableCapabilities => EnvironmentCapability.None;
    public EnvironmentProfileResult ApplyProfile(string sandboxRoot, IEnvironmentProfile profile)
        => new(profile.Name, Array.Empty<string>(), profile.DeclaredCapabilities, DateTimeOffset.UtcNow);
}

internal sealed class TestEnvironmentProfile : IEnvironmentProfile
{
    public string Name => "deterministic";
    public string Description => "Deterministic test profile.";
    public EnvironmentCapability DeclaredCapabilities => EnvironmentCapability.None;
    public IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps => Array.Empty<SandboxPreparationStep>();
}

internal sealed class TestWorkspaceProvider : IProjectWorkspaceProvider
{
    private readonly ProjectWorkspace _activeWorkspace;
    public TestWorkspaceProvider(string repoRoot)
        => _activeWorkspace = new ProjectWorkspace(Name: "builder-review", RootPath: repoRoot, LastOpenedUtc: DateTimeOffset.UtcNow, ProjectId: "builder-review-project");
    public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces() => new[] { _activeWorkspace };
    public ProjectWorkspace? GetActiveWorkspace() => _activeWorkspace;
    public void SetActiveWorkspace(ProjectWorkspace workspace) { }
    public void RemoveWorkspace(ProjectWorkspace workspace) { }
    public void UpdateWorkspace(ProjectWorkspace workspace) { }
}

internal sealed class TestBackendProbeService : IBackendProbeService
{
    public Task<BackendStatus> ProbeOllamaAsync(CancellationToken cancellationToken)
        => Task.FromResult(new BackendStatus(BackendKind.Ollama, true, null, "Ollama healthy.", DateTimeOffset.UtcNow, "http://localhost:11434", null));
    public Task<BackendStatus> ProbeQdrantAsync(CancellationToken cancellationToken)
        => Task.FromResult(new BackendStatus(BackendKind.Qdrant, true, null, "Qdrant healthy.", DateTimeOffset.UtcNow, "http://localhost:6333", null));
}

internal sealed class TestOllamaClient : IOllamaClient
{
    public Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new OllamaTagsResult(true, new[] { "builder-floor" }, null, "Models loaded."));
}
