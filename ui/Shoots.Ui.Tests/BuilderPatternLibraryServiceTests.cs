using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderPatternLibraryServiceTests
{
    [Fact]
    public void Approved_pattern_entries_require_evaluated_pinned_sources_and_stay_deterministic()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-host");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("pattern-source");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "pattern-host");
            BuilderExternalReconService.SetReconMode(
                repoRoot,
                BuilderExternalReconService.ReconModeManualOnly,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(100));

            var request = new BuilderExternalIntakeRequest(
                externalSourceRoot,
                BuilderExternalReconService.SourceKindRepo,
                string.Empty,
                BuilderExternalReconService.IntakeModeReferenceOnly,
                "Capture approved reference patterns.");
            BuilderExternalReconService.RecordMetadataDiscovery(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(101));
            var snapshots = BuilderExternalReconService.CreateSnapshot(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(102));
            var snapshot = Assert.Single(snapshots.Snapshots);

            var beforeEvaluation = BuilderPatternLibraryService.ApproveSnapshotAsPatternEntry(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(103));
            Assert.Empty(beforeEvaluation.Entries);

            BuilderExternalReconService.EvaluateSnapshot(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(104));

            var approvedFirst = BuilderPatternLibraryService.ApproveSnapshotAsPatternEntry(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(105));
            var firstJson = File.ReadAllText(BuilderPatternLibraryService.PatternLibraryEntriesPathForRepo(repoRoot));
            var approvedSecond = BuilderPatternLibraryService.ApproveSnapshotAsPatternEntry(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(105));
            var secondJson = File.ReadAllText(BuilderPatternLibraryService.PatternLibraryEntriesPathForRepo(repoRoot));

            Assert.Equal(firstJson, secondJson);
            Assert.NotEmpty(approvedFirst.Entries);
            Assert.All(approvedFirst.Entries, entry =>
            {
                Assert.Equal(snapshot.SnapshotId, entry.SourceSnapshotId);
                Assert.Equal("approved", entry.Eligibility.ApprovalStatus);
                Assert.Equal("operator_reviewed", entry.Eligibility.ReviewStatus);
                Assert.Equal("license_clear", entry.LicenseStatus);
            });
            Assert.True(File.Exists(BuilderPatternLibraryService.PatternLibraryIndexPathForRepo(repoRoot)));
            Assert.True(File.Exists(BuilderPatternLibraryService.PatternLibraryProvenancePathForRepo(repoRoot)));
            Assert.Equal(
                approvedFirst.Entries.Select(entry => entry.PatternEntryId).ToArray(),
                approvedSecond.Entries.Select(entry => entry.PatternEntryId).ToArray());
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }

            if (Directory.Exists(externalSourceRoot))
            {
                Directory.Delete(externalSourceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Approved_vendor_candidates_preserve_provenance_and_generate_stable_workspace_matches()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-vendor-host");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("vendor-source");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "pattern-vendor-host");
            var snapshot = BuilderPatternLibraryTestData.SeedEvaluatedSnapshot(
                repoRoot,
                externalSourceRoot,
                BuilderExternalReconService.IntakeModeVendorCandidate,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(110));
            var vendorCandidates = BuilderExternalReconService.StageVendorCandidate(
                repoRoot,
                snapshot.SnapshotId,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(113));
            var candidate = Assert.Single(vendorCandidates.Candidates);

            var entries = BuilderPatternLibraryService.ApproveVendorCandidateAsPatternEntry(
                repoRoot,
                candidate.CandidateId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(114));
            var matchesFirst = BuilderPatternLibraryService.RefreshPatternLibraryMatches(
                repoRoot,
                entries,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(115));
            var matchesJsonFirst = File.ReadAllText(BuilderPatternLibraryService.PatternLibraryMatchesPathForRepo(repoRoot));
            var matchesSecond = BuilderPatternLibraryService.RefreshPatternLibraryMatches(
                repoRoot,
                entries,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(115));
            var matchesJsonSecond = File.ReadAllText(BuilderPatternLibraryService.PatternLibraryMatchesPathForRepo(repoRoot));
            var provenance = BuilderPatternLibraryService.LoadPatternLibraryProvenance(repoRoot);

            Assert.NotNull(matchesFirst);
            Assert.NotNull(matchesSecond);
            Assert.NotNull(provenance);
            Assert.Equal(matchesJsonFirst, matchesJsonSecond);
            Assert.NotEmpty(entries.Entries);
            Assert.All(entries.Entries, entry => Assert.Equal(candidate.CandidateId, entry.VendorCandidateId));
            Assert.Equal(entries.Entries.Count, provenance!.Entries.Count);
            Assert.All(provenance.Entries, entry =>
            {
                Assert.Equal("license_clear", entry.LicenseStatus);
                Assert.False(string.IsNullOrWhiteSpace(entry.ResolvedCommitOrContentHash));
                Assert.False(string.IsNullOrWhiteSpace(entry.CanonicalSourceId));
            });
            Assert.Equal(
                matchesFirst!.Matches.Select(match => match.MatchId).ToArray(),
                matchesSecond!.Matches.Select(match => match.MatchId).ToArray());
            Assert.Contains(matchesFirst.Matches, match => match.MatchScore > 0d);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }

            if (Directory.Exists(externalSourceRoot))
            {
                Directory.Delete(externalSourceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Pattern_library_is_inactive_when_unused_and_does_not_change_route_or_finalize_state()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-runtime");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-host");
        try
        {
            var seeded = BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);
            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            var matches = BuilderPatternLibraryService.RefreshPatternLibraryMatches(repoB);

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoB);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoB).PatchApplyDecision;

            Assert.NotNull(seeded.Orchestration);
            Assert.Null(matches);
            Assert.Null(BuilderPatternLibraryService.LoadPatternLibraryEntries(repoB));
            Assert.Null(BuilderPatternLibraryService.LoadPatternLibraryIndex(repoB));
            Assert.Null(BuilderPatternLibraryService.LoadPatternLibraryProvenance(repoB));
            Assert.Null(BuilderPatternLibraryService.LoadPatternLibraryMatches(repoB));
            Assert.False(File.Exists(BuilderPatternLibraryService.PatternLibraryEntriesPathForRepo(repoB)));
            Assert.False(File.Exists(BuilderPatternLibraryService.PatternLibraryMatchesPathForRepo(repoB)));
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
            if (Directory.Exists(repoA))
            {
                Directory.Delete(repoA, recursive: true);
            }

            if (Directory.Exists(repoB))
            {
                Directory.Delete(repoB, recursive: true);
            }
        }
    }

}

public sealed class MainWindowViewModelBuilderPatternLibraryTests
{
    [Fact]
    public async Task Builder_workspace_browses_attaches_patterns_and_records_reference_context_with_decisions()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-runtime");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("pattern-host");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("pattern-vm-source");
        try
        {
            var scanner = new DeterministicBuilderToolchainCapabilityScanner();
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoA, "pattern-runtime", scanner, BuilderWorkspaceTestData.ObservedUtc);
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoB, "pattern-host", scanner, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));
            BuilderRecoveryPlaybookTestData.Seed(repoA, repoB, selectRepoB: true);

            var snapshot = BuilderPatternLibraryTestData.SeedEvaluatedSnapshot(
                repoB,
                externalSourceRoot,
                BuilderExternalReconService.IntakeModeVendorCandidate,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(120));
            BuilderExternalReconService.StageVendorCandidate(
                repoB,
                snapshot.SnapshotId,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(124));
            var entries = BuilderPatternLibraryService.ApproveSnapshotAsPatternEntry(
                repoB,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(125));
            var matches = BuilderPatternLibraryService.RefreshPatternLibraryMatches(
                repoB,
                entries,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(126));

            var workspaceProvider = new MultiWorkspaceProvider(
                new ProjectWorkspace("pattern-runtime", repoA, BuilderWorkspaceTestData.ObservedUtc, ProjectId: "pattern-runtime"),
                new ProjectWorkspace("pattern-host", repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1), ProjectId: "pattern-host"));
            var viewModel = BuilderWorkspaceTestData.CreateViewModel(repoA, workspaceProvider, scanner);
            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.NotNull(matches);
            Assert.True(viewModel.HasBuilderPatternLibraryEntries);
            Assert.True(viewModel.HasBuilderPatternLibraryMatches);
            Assert.True(viewModel.HasBuilderPatternLibraryEntriesArtifactPath);
            Assert.True(viewModel.HasBuilderPatternLibraryMatchesArtifactPath);

            var selectedEntry = viewModel.BuilderPatternLibraryEntries.First();
            await viewModel.SelectBuilderPatternLibraryEntryCommand.ExecuteAsync(selectedEntry);
            var selectedMatch = viewModel.BuilderPatternLibraryMatches.First(match =>
                string.Equals(match.PatternEntryId, selectedEntry.PatternEntryId, StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderPatternLibraryMatchCommand.ExecuteAsync(selectedMatch);
            await viewModel.AttachBuilderPatternReferenceCommand.ExecuteAsync();

            Assert.Contains(selectedEntry.PatternName, viewModel.BuilderPatternLibraryAttachmentSummary, StringComparison.OrdinalIgnoreCase);

            var playbook = viewModel.BuilderRecoveryPlaybooks.First(entry => string.Equals(entry.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderRecoveryPlaybookCommand.ExecuteAsync(playbook);
            var simulation = viewModel.BuilderRecoverySimulations.First(entry => string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase));
            await viewModel.SelectBuilderRecoverySimulationCommand.ExecuteAsync(simulation);

            if (!viewModel.HasBuilderReviewCurrentFile)
            {
                await viewModel.SelectBuilderReviewFileCommand.ExecuteAsync(viewModel.BuilderReviewGroups.First().Files.First());
            }

            await viewModel.ApprovePendingBuilderReviewGroupCommand.ExecuteAsync();

            Assert.True(viewModel.HasBuilderOperatorDecisions);
            var latestDecision = viewModel.BuilderOperatorDecisionRows.Last().Decision;
            Assert.Equal(selectedEntry.PatternEntryId, latestDecision.PatternEntryId);
            Assert.False(string.IsNullOrWhiteSpace(latestDecision.PatternMatchId));
            Assert.Equal(snapshot.SnapshotId, latestDecision.PatternLibrarySnapshotId);
            Assert.Contains(selectedEntry.PatternEntryId, viewModel.BuilderOperatorDecisionSelectedContext, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(repoA))
            {
                Directory.Delete(repoA, recursive: true);
            }

            if (Directory.Exists(repoB))
            {
                Directory.Delete(repoB, recursive: true);
            }

            if (Directory.Exists(externalSourceRoot))
            {
                Directory.Delete(externalSourceRoot, recursive: true);
            }
        }
    }
}

internal static class BuilderPatternLibraryTestData
{
    public static BuilderExternalSourceSnapshotRecord SeedEvaluatedSnapshot(
        string repoRoot,
        string externalSourceRoot,
        string intakeMode,
        DateTimeOffset observedUtc)
    {
        BuilderExternalReconService.SetReconMode(
            repoRoot,
            BuilderExternalReconService.ReconModeManualOnly,
            observedUtc);
        var request = new BuilderExternalIntakeRequest(
            externalSourceRoot,
            BuilderExternalReconService.SourceKindRepo,
            string.Empty,
            intakeMode,
            "Stage approved external code for local pattern reuse.");
        BuilderExternalReconService.RecordMetadataDiscovery(
            repoRoot,
            request,
            observedUtc.AddMinutes(1));
        var snapshots = BuilderExternalReconService.CreateSnapshot(
            repoRoot,
            request,
            observedUtc.AddMinutes(2));
        var snapshot = Assert.Single(snapshots.Snapshots);
        BuilderExternalReconService.EvaluateSnapshot(
            repoRoot,
            snapshot.SnapshotId,
            observedUtc.AddMinutes(3));
        return snapshot;
    }
}
