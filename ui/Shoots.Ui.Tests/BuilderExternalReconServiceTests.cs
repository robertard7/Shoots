using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderExternalReconServiceTests
{
    [Fact]
    public void Manual_only_intake_generates_deterministic_snapshot_evaluation_vendor_and_provenance_artifacts()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("consumer-app");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("external-lib");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "consumer-app");
            var request = new BuilderExternalIntakeRequest(
                externalSourceRoot,
                BuilderExternalReconService.SourceKindRepo,
                string.Empty,
                BuilderExternalReconService.IntakeModeVendorCandidate,
                "Review as a vendor candidate.");

            var reconMode = BuilderExternalReconService.SetReconMode(
                repoRoot,
                BuilderExternalReconService.ReconModeManualOnly,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(81));
            var metadataFirst = BuilderExternalReconService.RecordMetadataDiscovery(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(82));
            var metadataJsonFirst = File.ReadAllText(BuilderExternalReconService.ExternalReconPathForRepo(repoRoot));
            var metadataSecond = BuilderExternalReconService.RecordMetadataDiscovery(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(82));
            var metadataJsonSecond = File.ReadAllText(BuilderExternalReconService.ExternalReconPathForRepo(repoRoot));

            var snapshotsFirst = BuilderExternalReconService.CreateSnapshot(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(83));
            var snapshotJsonFirst = File.ReadAllText(BuilderExternalReconService.ExternalSourceSnapshotsPathForRepo(repoRoot));
            var snapshotsSecond = BuilderExternalReconService.CreateSnapshot(
                repoRoot,
                request,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(83));
            var snapshotJsonSecond = File.ReadAllText(BuilderExternalReconService.ExternalSourceSnapshotsPathForRepo(repoRoot));

            var snapshot = Assert.Single(snapshotsSecond.Snapshots);
            var evaluationsFirst = BuilderExternalReconService.EvaluateSnapshot(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(84));
            var evaluationJsonFirst = File.ReadAllText(BuilderExternalReconService.ExternalCodeEvaluationsPathForRepo(repoRoot));
            var evaluationsSecond = BuilderExternalReconService.EvaluateSnapshot(
                repoRoot,
                snapshot.SnapshotId,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(84));
            var evaluationJsonSecond = File.ReadAllText(BuilderExternalReconService.ExternalCodeEvaluationsPathForRepo(repoRoot));

            var vendorFirst = BuilderExternalReconService.StageVendorCandidate(
                repoRoot,
                snapshot.SnapshotId,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(85));
            var vendorJsonFirst = File.ReadAllText(BuilderExternalReconService.VendorCandidatesPathForRepo(repoRoot));
            var vendorSecond = BuilderExternalReconService.StageVendorCandidate(
                repoRoot,
                snapshot.SnapshotId,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(85));
            var vendorJsonSecond = File.ReadAllText(BuilderExternalReconService.VendorCandidatesPathForRepo(repoRoot));

            var evaluation = Assert.Single(evaluationsSecond.Evaluations);
            var candidate = Assert.Single(vendorSecond.Candidates);
            var provenance = BuilderExternalReconService.LoadExternalProvenanceIndex(repoRoot);
            var provenanceEntry = Assert.Single(provenance!.Entries);

            Assert.Equal(BuilderExternalReconService.ReconModeManualOnly, reconMode.ReconMode);
            Assert.Empty(reconMode.Suggestions);
            Assert.Equal(metadataJsonFirst, metadataJsonSecond);
            Assert.Equal(snapshotJsonFirst, snapshotJsonSecond);
            Assert.Equal(evaluationJsonFirst, evaluationJsonSecond);
            Assert.Equal(vendorJsonFirst, vendorJsonSecond);
            Assert.Equal("metadata_recorded", metadataSecond.Entries.Last().Status);
            Assert.Equal("license_clear", snapshot.LicenseStatus);
            Assert.True(Directory.Exists(snapshot.SnapshotRoot));
            Assert.Equal("vendor_candidate", evaluation.RecommendedUsage);
            Assert.False(evaluation.RequiresManualReview);
            Assert.False(candidate.ReviewRequired);
            Assert.NotEmpty(candidate.SelectedPaths);
            Assert.Equal(snapshot.SnapshotId, provenanceEntry.SnapshotId);
            Assert.Equal(snapshot.ResolvedCommitOrContentHash, provenanceEntry.ResolvedCommitOrContentHash);
            Assert.Equal(evaluation.EvaluationId, provenanceEntry.EvaluationId);
            Assert.Equal(candidate.CandidateId, provenanceEntry.VendorCandidateId);
            Assert.Equal("license_clear", provenanceEntry.LicenseStatus);
            Assert.False(Directory.Exists(Path.Combine(repoRoot, "vendor", "external-lib")));
            Assert.NotNull(snapshotsFirst);
            Assert.NotNull(evaluationsFirst);
            Assert.NotNull(vendorFirst);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            Directory.Delete(externalSourceRoot, recursive: true);
        }
    }

    [Fact]
    public void Recon_mode_off_blocks_intake_and_leaves_route_and_finalize_state_unchanged()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-app");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("off-mode-source");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "host-app");
            var context = BuilderWorkspaceService.LoadContext(repoRoot);
            Assert.NotNull(context);
            BuilderWorkspaceService.RecordRouteResolution(
                context!,
                "request-phase-76",
                "builder_proof_matrix",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(86));
            BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-76");

            var beforeRoute = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
            var beforeApply = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).PatchApplyDecision;

            BuilderExternalReconService.SetReconMode(
                repoRoot,
                BuilderExternalReconService.ReconModeOff,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(87));
            var recon = BuilderExternalReconService.RecordMetadataDiscovery(
                repoRoot,
                new BuilderExternalIntakeRequest(
                    externalSourceRoot,
                    BuilderExternalReconService.SourceKindRepo,
                    string.Empty,
                    BuilderExternalReconService.IntakeModeMetadataOnly,
                    "Do not fetch while recon is off."),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(88));

            var afterRoute = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
            var afterApply = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).PatchApplyDecision;

            Assert.NotNull(beforeRoute);
            Assert.NotNull(beforeApply);
            Assert.NotNull(afterRoute);
            Assert.NotNull(afterApply);
            Assert.Equal(beforeRoute!.RouteDecision, afterRoute!.RouteDecision);
            Assert.Equal(beforeApply!.ApplyEligibilityState, afterApply!.ApplyEligibilityState);
            Assert.Equal(beforeApply.FinalizationState, afterApply.FinalizationState);

            var entry = Assert.Single(recon.Entries);
            Assert.Equal("recon_mode_off", entry.FailureClassification);
            Assert.Equal("failed", entry.Status);
            Assert.False(File.Exists(BuilderExternalReconService.ExternalSourceSnapshotsPathForRepo(repoRoot)));
            Assert.False(File.Exists(BuilderExternalReconService.ExternalCodeEvaluationsPathForRepo(repoRoot)));
            Assert.False(File.Exists(BuilderExternalReconService.VendorCandidatesPathForRepo(repoRoot)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            Directory.Delete(externalSourceRoot, recursive: true);
        }
    }

    [Fact]
    public void Suggest_only_mode_surfaces_suggestions_without_running_manual_intake()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("suggest-host");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("suggest-source");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "suggest-host");

            var recon = BuilderExternalReconService.SetReconMode(
                repoRoot,
                BuilderExternalReconService.ReconModeSuggestOnly,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(89));
            var updated = BuilderExternalReconService.RecordMetadataDiscovery(
                repoRoot,
                new BuilderExternalIntakeRequest(
                    externalSourceRoot,
                    BuilderExternalReconService.SourceKindRepo,
                    string.Empty,
                    BuilderExternalReconService.IntakeModeReferenceOnly,
                    "Suggestions only."),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(90));

            Assert.NotEmpty(recon.Suggestions);
            Assert.Contains(recon.Suggestions, suggestion => string.Equals(suggestion.SuggestionId, "suggest_dotnet_reference", StringComparison.OrdinalIgnoreCase));

            var failure = updated.Entries.Last();
            Assert.Equal("suggest_only_mode", failure.FailureClassification);
            Assert.Equal("failed", failure.Status);
            Assert.False(File.Exists(BuilderExternalReconService.ExternalSourceSnapshotsPathForRepo(repoRoot)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            Directory.Delete(externalSourceRoot, recursive: true);
        }
    }

    [Fact]
    public void Normal_builder_operations_leave_external_recon_inactive_until_operator_uses_it()
    {
        var repoRoot = BuilderWorkspaceTestData.CreateWorkspaceRoot("normal-host");
        try
        {
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoRoot, "normal-host");
            BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoRoot, "session-normal");
            BuilderWorkspaceService.RecordRouteResolution(
                BuilderWorkspaceService.LoadContext(repoRoot)!,
                "request-normal",
                "builder_proof_matrix",
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(91));

            Assert.Null(BuilderExternalReconService.LoadExternalRecon(repoRoot));
            Assert.False(Directory.Exists(BuilderExternalReconService.ExternalRootForRepo(repoRoot)));
            Assert.NotNull(BuilderWorkspaceService.LoadRouteResolution(repoRoot));
            Assert.NotNull(BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).PatchApplyDecision);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderExternalReconTests
{
    [Fact]
    public async Task Builder_workspace_surfaces_mode_gating_and_manual_external_intake()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("workspace-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("workspace-b");
        var externalSourceRoot = BuilderExternalReconTestData.CreateExternalSourceRoot("vm-source");
        try
        {
            var scanner = new DeterministicBuilderToolchainCapabilityScanner();
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoA, "workspace-a", scanner, BuilderWorkspaceTestData.ObservedUtc);
            BuilderExternalReconTestData.SeedCSharpWorkspace(repoB, "workspace-b", scanner, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

            var workspaceProvider = new MultiWorkspaceProvider(
                new ProjectWorkspace("workspace-a", repoA, BuilderWorkspaceTestData.ObservedUtc, ProjectId: "workspace-a"),
                new ProjectWorkspace("workspace-b", repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1), ProjectId: "workspace-b"));
            var viewModel = BuilderWorkspaceTestData.CreateViewModel(repoA, workspaceProvider, scanner);
            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.Equal(BuilderExternalReconService.ReconModeOff, viewModel.SelectedBuilderExternalReconMode);
            Assert.False(viewModel.CanBuilderExternalManualIntake);
            Assert.False(viewModel.FetchBuilderExternalMetadataCommand.CanExecute(null));
            Assert.Contains("inactive", viewModel.BuilderExternalDisabledReason, StringComparison.OrdinalIgnoreCase);

            viewModel.SelectedBuilderExternalReconMode = BuilderExternalReconService.ReconModeSuggestOnly;

            Assert.True(viewModel.CanBuilderExternalSuggestions);
            Assert.False(viewModel.CanBuilderExternalManualIntake);
            Assert.True(viewModel.HasBuilderExternalSuggestions);
            Assert.False(viewModel.FetchBuilderExternalMetadataCommand.CanExecute(null));

            viewModel.SelectedBuilderExternalReconMode = BuilderExternalReconService.ReconModeEnabled;
            viewModel.SelectedBuilderExternalSourceKind = BuilderExternalReconService.SourceKindRepo;
            viewModel.SelectedBuilderExternalIntakeMode = BuilderExternalReconService.IntakeModeVendorCandidate;
            viewModel.BuilderExternalSourceUrl = externalSourceRoot;
            viewModel.BuilderExternalOperatorNote = "Inspect this external library for staging.";

            Assert.True(viewModel.CanBuilderExternalManualIntake);
            Assert.True(viewModel.FetchBuilderExternalMetadataCommand.CanExecute(null));
            Assert.True(viewModel.SnapshotBuilderExternalSourceCommand.CanExecute(null));

            await viewModel.FetchBuilderExternalMetadataCommand.ExecuteAsync();
            await viewModel.SnapshotBuilderExternalSourceCommand.ExecuteAsync();

            Assert.True(viewModel.HasBuilderExternalSnapshots);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedBuilderExternalSnapshotId));
            Assert.True(viewModel.EvaluateBuilderExternalSnapshotCommand.CanExecute(null));

            await viewModel.EvaluateBuilderExternalSnapshotCommand.ExecuteAsync();
            await viewModel.StageBuilderExternalVendorCandidateCommand.ExecuteAsync();

            Assert.True(viewModel.HasBuilderExternalReconArtifactPath);
            Assert.True(viewModel.HasBuilderExternalSnapshotArtifactPath);
            Assert.True(viewModel.HasBuilderExternalEvaluationArtifactPath);
            Assert.True(viewModel.HasBuilderExternalVendorCandidateArtifactPath);
            Assert.True(viewModel.HasBuilderExternalProvenanceArtifactPath);
            Assert.True(viewModel.HasBuilderExternalEvaluations);
            Assert.True(viewModel.HasBuilderExternalVendorCandidates);
            Assert.True(viewModel.HasBuilderExternalProvenanceEntries);
            Assert.Contains("license", viewModel.BuilderExternalProvenanceEntries.First().LicenseSummary, StringComparison.OrdinalIgnoreCase);

            await viewModel.OpenBuilderExternalReconArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExternalSnapshotArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExternalEvaluationArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExternalVendorCandidateArtifactCommand.ExecuteAsync();
            await viewModel.OpenBuilderExternalProvenanceArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
            Directory.Delete(externalSourceRoot, recursive: true);
        }
    }
}

internal static class BuilderExternalReconTestData
{
    public static string CreateExternalSourceRoot(string name)
    {
        var root = BuilderWorkspaceTestData.CreateWorkspaceRoot(name);
        BuilderWorkspaceTestData.WriteFile(root, "src/ExternalLib/ExternalLib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.0.0\" /></ItemGroup></Project>");
        BuilderWorkspaceTestData.WriteFile(root, "src/ExternalLib/ExternalThing.cs", "namespace ExternalLib;\npublic sealed class ExternalThing { public int Value => 42; }\n");
        BuilderWorkspaceTestData.WriteFile(root, "tests/ExternalLib.Tests/ExternalLib.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.0.0\" /></ItemGroup></Project>");
        BuilderWorkspaceTestData.WriteFile(root, "tests/ExternalLib.Tests/ExternalThingTests.cs", "namespace ExternalLib.Tests;\npublic sealed class ExternalThingTests { }\n");
        BuilderWorkspaceTestData.WriteFile(root, "LICENSE", "MIT License\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files.");
        return root;
    }

    public static void SeedCSharpWorkspace(
        string repoRoot,
        string projectId,
        DeterministicBuilderToolchainCapabilityScanner? scanner = null,
        DateTimeOffset? observedUtc = null)
    {
        var effectiveObservedUtc = observedUtc ?? BuilderWorkspaceTestData.ObservedUtc;
        BuilderWorkspaceTestData.WriteFile(repoRoot, "src/Workspace/Program.cs", "namespace Workspace;\npublic static class Program { }\n");
        BuilderWorkspaceTestData.WriteFile(repoRoot, "src/Workspace/Workspace.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var effectiveScanner = scanner ?? new DeterministicBuilderToolchainCapabilityScanner();
        effectiveScanner.AddObservations(
            repoRoot,
            new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, effectiveObservedUtc),
            new BuilderToolchainCapabilityObservation("msbuild", "build_tool", "msbuild", "17.0.0", true, true, "probe_succeeded", string.Empty, effectiveObservedUtc));

        BuilderWorkspaceService.RefreshWorkspaceArtifacts(
            new[] { BuilderWorkspaceService.CreateDescriptor(repoRoot, projectId) },
            new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoRoot),
            effectiveScanner,
            effectiveObservedUtc,
            forceCapabilityScan: true);
    }
}
