using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderExternalIntakeRequest(
    string SourceUrl,
    string SourceKind = "",
    string RequestedRef = "",
    string IntakeMode = "",
    string OperatorNote = "");

public sealed record BuilderExternalSourceMetadataRecord(
    string CanonicalSourceId,
    string SourceHost,
    string SourceProvider,
    string RepoName,
    string OwnerOrOrg,
    string ResolvedRef,
    string ResolvedCommitOrContentHash,
    IReadOnlyList<string> Languages,
    int FileCount,
    bool HasTests,
    IReadOnlyList<string> BuildSystemMarkers,
    IReadOnlyList<string> DependencyManifestMarkers,
    string LicenseMetadata,
    string LicenseStatus,
    string AvailabilityState,
    string Summary);

public sealed record BuilderExternalSourceSuggestionRecord(
    string SuggestionId,
    string Title,
    string SuggestedUsage,
    string SuggestedSourceKind,
    string SuggestedIntakeMode,
    bool RequiresManualConfirmation,
    IReadOnlyList<string> ArtifactLinks,
    string Summary);

public sealed record BuilderExternalReconEntryRecord(
    string ActionId,
    string ActionType,
    string ReconMode,
    string SourceUrl,
    string SourceKind,
    string RequestedRef,
    string IntakeMode,
    string OperatorNote,
    string Status,
    string FailureClassification,
    BuilderExternalSourceMetadataRecord Metadata,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalReconRecord(
    string WorkspaceId,
    string SchemaVersion,
    string ReconMode,
    IReadOnlyList<BuilderExternalSourceSuggestionRecord> Suggestions,
    IReadOnlyList<BuilderExternalReconEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalSourceSnapshotRecord(
    string SnapshotId,
    string SourceUrl,
    string SourceKind,
    string RequestedRef,
    string ResolvedSourceId,
    string ResolvedCommit,
    string ResolvedCommitOrContentHash,
    string ContentHash,
    string License,
    string LicenseStatus,
    string SnapshotScope,
    IReadOnlyList<string> IncludedPaths,
    IReadOnlyList<string> ExcludedPaths,
    string SnapshotRoot,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalSourceSnapshotsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderExternalSourceSnapshotRecord> Snapshots,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalCodeEvaluationRecord(
    string EvaluationId,
    string SnapshotId,
    double UsefulnessScore,
    double QualityScore,
    double RiskScore,
    string LicenseStatus,
    string CompatibilityClassification,
    string RecommendedUsage,
    bool RequiresManualReview,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalCodeEvaluationsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderExternalCodeEvaluationRecord> Evaluations,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderVendorCandidateRecord(
    string CandidateId,
    string SnapshotId,
    string CandidateScope,
    IReadOnlyList<string> SelectedPaths,
    string ProvenanceLink,
    string LicenseStatus,
    string RiskSummary,
    bool ReviewRequired,
    string VendorDestinationSuggestion,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderVendorCandidatesRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderVendorCandidateRecord> Candidates,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalProvenanceEntryRecord(
    string ProvenanceId,
    string OriginalUrl,
    string CanonicalSourceId,
    string ResolvedCommitOrContentHash,
    string LicenseMetadata,
    string LicenseStatus,
    string SnapshotHash,
    string SnapshotId,
    string EvaluationId,
    string VendorCandidateId,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalProvenanceIndexRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderExternalProvenanceEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderExternalReconService
{
    public const string ExternalReconFileName = "builder_external_recon.json";
    public const string ExternalSourceSnapshotsFileName = "builder_external_source_snapshots.json";
    public const string ExternalCodeEvaluationsFileName = "builder_external_code_evaluations.json";
    public const string VendorCandidatesFileName = "builder_vendor_candidates.json";
    public const string ExternalProvenanceIndexFileName = "builder_external_provenance_index.json";

    public const string ReconModeOff = "off";
    public const string ReconModeManualOnly = "manual_only";
    public const string ReconModeSuggestOnly = "suggest_only";
    public const string ReconModeEnabled = "enabled";

    public const string SourceKindRepo = "repo";
    public const string SourceKindFile = "file";
    public const string SourceKindArchive = "archive";
    public const string SourceKindPackageSource = "package-source";

    public const string IntakeModeMetadataOnly = "metadata_only";
    public const string IntakeModeSnapshotForReview = "snapshot_for_review";
    public const string IntakeModeVendorCandidate = "vendor_candidate";
    public const string IntakeModeReferenceOnly = "reference_only";

    private const string ReconSchemaVersion = "builder_external_recon.v1";
    private const string SnapshotSchemaVersion = "builder_external_source_snapshots.v1";
    private const string EvaluationSchemaVersion = "builder_external_code_evaluations.v1";
    private const string VendorSchemaVersion = "builder_vendor_candidates.v1";
    private const string ProvenanceSchemaVersion = "builder_external_provenance_index.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient HttpClient = new();
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codex",
        "bin",
        "obj",
        "node_modules",
        ".vs",
        ".idea",
        ".vscode"
    };
    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".xaml"] = "xaml",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".py"] = "python",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".md"] = "markdown",
        [".json"] = "json",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".toml"] = "toml",
        [".xml"] = "xml",
        [".ps1"] = "powershell"
    };

    public static string ExternalRootForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), "external");

    public static string VendorRootForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), "vendor");

    public static string ProvenanceRootForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), "provenance");

    public static string ExternalReconPathForRepo(string repoRoot)
        => Path.Combine(ExternalRootForRepo(repoRoot), ExternalReconFileName);

    public static string ExternalSourceSnapshotsPathForRepo(string repoRoot)
        => Path.Combine(ExternalRootForRepo(repoRoot), ExternalSourceSnapshotsFileName);

    public static string ExternalCodeEvaluationsPathForRepo(string repoRoot)
        => Path.Combine(ExternalRootForRepo(repoRoot), ExternalCodeEvaluationsFileName);

    public static string VendorCandidatesPathForRepo(string repoRoot)
        => Path.Combine(VendorRootForRepo(repoRoot), VendorCandidatesFileName);

    public static string ExternalProvenanceIndexPathForRepo(string repoRoot)
        => Path.Combine(ProvenanceRootForRepo(repoRoot), ExternalProvenanceIndexFileName);

    public static string SnapshotContentRootForRepo(string repoRoot)
        => Path.Combine(ExternalRootForRepo(repoRoot), "snapshots");

    public static string SnapshotContentPathForRepo(string repoRoot, string snapshotId)
        => Path.Combine(SnapshotContentRootForRepo(repoRoot), snapshotId, "content");

    public static BuilderExternalReconRecord? LoadExternalRecon(string repoRoot)
        => Load<BuilderExternalReconRecord>(ExternalReconPathForRepo(repoRoot));

    public static BuilderExternalSourceSnapshotsRecord? LoadExternalSourceSnapshots(string repoRoot)
        => Load<BuilderExternalSourceSnapshotsRecord>(ExternalSourceSnapshotsPathForRepo(repoRoot));

    public static BuilderExternalCodeEvaluationsRecord? LoadExternalCodeEvaluations(string repoRoot)
        => Load<BuilderExternalCodeEvaluationsRecord>(ExternalCodeEvaluationsPathForRepo(repoRoot));

    public static BuilderVendorCandidatesRecord? LoadVendorCandidates(string repoRoot)
        => Load<BuilderVendorCandidatesRecord>(VendorCandidatesPathForRepo(repoRoot));

    public static BuilderExternalProvenanceIndexRecord? LoadExternalProvenanceIndex(string repoRoot)
        => Load<BuilderExternalProvenanceIndexRecord>(ExternalProvenanceIndexPathForRepo(repoRoot));

    public static string NormalizeReconMode(string? reconMode)
        => reconMode?.Trim().ToLowerInvariant() switch
        {
            ReconModeManualOnly => ReconModeManualOnly,
            ReconModeSuggestOnly => ReconModeSuggestOnly,
            ReconModeEnabled => ReconModeEnabled,
            _ => ReconModeOff
        };

    public static bool ModeAllowsManualIntake(string? reconMode)
        => string.Equals(NormalizeReconMode(reconMode), ReconModeManualOnly, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(NormalizeReconMode(reconMode), ReconModeEnabled, StringComparison.OrdinalIgnoreCase);

    public static bool ModeAllowsSuggestions(string? reconMode)
        => string.Equals(NormalizeReconMode(reconMode), ReconModeSuggestOnly, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(NormalizeReconMode(reconMode), ReconModeEnabled, StringComparison.OrdinalIgnoreCase);

    public static string GetReconModeLabel(string? reconMode)
        => NormalizeReconMode(reconMode) switch
        {
            ReconModeManualOnly => "Manual Only",
            ReconModeSuggestOnly => "Suggest Only",
            ReconModeEnabled => "Enabled",
            _ => "Off"
        };

    public static string GetSourceKindLabel(string? sourceKind)
        => NormalizeSourceKind(sourceKind) switch
        {
            SourceKindFile => "Source File",
            SourceKindArchive => "Archive",
            SourceKindPackageSource => "Package Source",
            _ => "Repository"
        };

    public static string GetIntakeModeLabel(string? intakeMode)
        => NormalizeIntakeMode(intakeMode) switch
        {
            IntakeModeSnapshotForReview => "Snapshot For Review",
            IntakeModeVendorCandidate => "Vendor Candidate",
            IntakeModeReferenceOnly => "Reference Only",
            _ => "Metadata Only"
        };

    public static BuilderExternalReconRecord SetReconMode(
        string repoRoot,
        string reconMode,
        DateTimeOffset? observedUtc = null)
    {
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var normalizedMode = NormalizeReconMode(reconMode);
        var existing = LoadExternalRecon(repoRoot);
        var suggestions = BuildSuggestions(repoRoot, normalizedMode);
        var artifact = new BuilderExternalReconRecord(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            ReconSchemaVersion,
            normalizedMode,
            suggestions,
            existing?.Entries ?? Array.Empty<BuilderExternalReconEntryRecord>(),
            true,
            BuildReconSummary(normalizedMode, existing?.Entries ?? Array.Empty<BuilderExternalReconEntryRecord>(), suggestions),
            ExternalReconPathForRepo(repoRoot),
            effectiveObservedUtc);
        EnsureRoots(repoRoot);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    public static BuilderExternalReconRecord RecordMetadataDiscovery(
        string repoRoot,
        BuilderExternalIntakeRequest request,
        DateTimeOffset? observedUtc = null)
    {
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var recon = LoadExternalRecon(repoRoot) ?? CreateDefaultReconRecord(repoRoot, effectiveObservedUtc);
        var modeFailure = ValidateManualMode(recon.ReconMode);
        var entry = modeFailure is null
            ? BuildMetadataEntry(repoRoot, recon.ReconMode, request, effectiveObservedUtc)
            : BuildFailureEntry(repoRoot, recon.ReconMode, request, "discover_metadata", modeFailure.Value.Classification, modeFailure.Value.Summary, effectiveObservedUtc);
        var updated = MergeReconEntry(repoRoot, recon, entry, effectiveObservedUtc);
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);
        return updated;
    }

    public static BuilderExternalSourceSnapshotsRecord CreateSnapshot(
        string repoRoot,
        BuilderExternalIntakeRequest request,
        DateTimeOffset? observedUtc = null)
    {
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var recon = LoadExternalRecon(repoRoot) ?? CreateDefaultReconRecord(repoRoot, effectiveObservedUtc);
        var modeFailure = ValidateManualMode(recon.ReconMode);
        if (modeFailure is not null)
        {
            var failure = BuildFailureEntry(repoRoot, recon.ReconMode, request, "create_snapshot", modeFailure.Value.Classification, modeFailure.Value.Summary, effectiveObservedUtc);
            var updatedRecon = MergeReconEntry(repoRoot, recon, failure, effectiveObservedUtc);
            EnsureRoots(repoRoot);
            Save(updatedRecon.ArtifactPath, updatedRecon);
            return LoadExternalSourceSnapshots(repoRoot) ?? CreateDefaultSnapshotsRecord(repoRoot, effectiveObservedUtc);
        }

        return CreateSnapshotInternal(repoRoot, recon, request, effectiveObservedUtc);
    }

    public static BuilderExternalCodeEvaluationsRecord EvaluateSnapshot(
        string repoRoot,
        string snapshotId,
        DateTimeOffset? observedUtc = null)
    {
        return EvaluateSnapshotInternal(repoRoot, snapshotId, observedUtc ?? DateTimeOffset.UtcNow);
    }

    public static BuilderVendorCandidatesRecord StageVendorCandidate(
        string repoRoot,
        string snapshotId,
        IReadOnlyList<string>? selectedPaths = null,
        DateTimeOffset? observedUtc = null)
    {
        return StageVendorCandidateInternal(repoRoot, snapshotId, selectedPaths, observedUtc ?? DateTimeOffset.UtcNow);
    }

    private static BuilderExternalReconRecord CreateDefaultReconRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            ReconSchemaVersion,
            ReconModeOff,
            Array.Empty<BuilderExternalSourceSuggestionRecord>(),
            Array.Empty<BuilderExternalReconEntryRecord>(),
            true,
            "External recon is off. Normal builder operation remains local and unchanged.",
            ExternalReconPathForRepo(repoRoot),
            observedUtc);

    private static BuilderExternalSourceSnapshotsRecord CreateDefaultSnapshotsRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            SnapshotSchemaVersion,
            Array.Empty<BuilderExternalSourceSnapshotRecord>(),
            true,
            "No external source snapshots recorded.",
            ExternalSourceSnapshotsPathForRepo(repoRoot),
            observedUtc);

    private static BuilderExternalCodeEvaluationsRecord CreateDefaultEvaluationsRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            EvaluationSchemaVersion,
            Array.Empty<BuilderExternalCodeEvaluationRecord>(),
            true,
            "No external code evaluations recorded.",
            ExternalCodeEvaluationsPathForRepo(repoRoot),
            observedUtc);

    private static BuilderVendorCandidatesRecord CreateDefaultVendorCandidatesRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            VendorSchemaVersion,
            Array.Empty<BuilderVendorCandidateRecord>(),
            true,
            "No vendor candidates recorded.",
            VendorCandidatesPathForRepo(repoRoot),
            observedUtc);

    private static BuilderExternalProvenanceIndexRecord CreateDefaultProvenanceIndexRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            ProvenanceSchemaVersion,
            Array.Empty<BuilderExternalProvenanceEntryRecord>(),
            true,
            "No external provenance entries recorded.",
            ExternalProvenanceIndexPathForRepo(repoRoot),
            observedUtc);

    private static (string Classification, string Summary)? ValidateManualMode(string reconMode)
    {
        if (string.Equals(reconMode, ReconModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return ("recon_mode_off", "External recon is off, so the URL intake lane stays inactive.");
        }

        if (string.Equals(reconMode, ReconModeSuggestOnly, StringComparison.OrdinalIgnoreCase))
        {
            return ("suggest_only_mode", "Suggest-only mode surfaces advisory source ideas but does not run manual URL intake actions.");
        }

        return null;
    }

    private static BuilderExternalReconRecord MergeReconEntry(
        string repoRoot,
        BuilderExternalReconRecord recon,
        BuilderExternalReconEntryRecord entry,
        DateTimeOffset observedUtc)
    {
        var mergedEntries = MergeById(recon.Entries, entry, item => item.ActionId);
        var suggestions = BuildSuggestions(repoRoot, recon.ReconMode);
        return recon with
        {
            Entries = mergedEntries,
            Suggestions = suggestions,
            Summary = BuildReconSummary(recon.ReconMode, mergedEntries, suggestions),
            ObservedUtc = observedUtc
        };
    }

    private static BuilderExternalReconEntryRecord BuildFailureEntry(
        string repoRoot,
        string reconMode,
        BuilderExternalIntakeRequest request,
        string actionType,
        string failureClassification,
        string failureSummary,
        DateTimeOffset observedUtc)
    {
        var normalizedKind = NormalizeSourceKind(request.SourceKind);
        var normalizedMode = NormalizeIntakeMode(request.IntakeMode);
        var metadata = new BuilderExternalSourceMetadataRecord(
            ComputeCanonicalSourceId(repoRoot, request.SourceUrl),
            ResolveSourceHost(request.SourceUrl),
            ResolveSourceProvider(request.SourceUrl),
            ResolveRepoName(request.SourceUrl),
            ResolveOwnerOrOrg(request.SourceUrl),
            request.RequestedRef?.Trim() ?? string.Empty,
            string.Empty,
            Array.Empty<string>(),
            0,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Empty,
            "license_unknown",
            "failure",
            failureSummary);
        return new BuilderExternalReconEntryRecord(
            ComputeDeterministicId("recon", actionType, metadata.CanonicalSourceId, failureClassification, request.RequestedRef ?? string.Empty),
            actionType,
            reconMode,
            request.SourceUrl.Trim(),
            normalizedKind,
            request.RequestedRef?.Trim() ?? string.Empty,
            normalizedMode,
            request.OperatorNote?.Trim() ?? string.Empty,
            "failed",
            failureClassification,
            metadata,
            BuildArtifactLinks(
                ExternalReconPathForRepo(repoRoot),
                ExternalProvenanceIndexPathForRepo(repoRoot)),
            failureSummary,
            observedUtc);
    }

    private static string BuildReconSummary(
        string reconMode,
        IReadOnlyList<BuilderExternalReconEntryRecord> entries,
        IReadOnlyList<BuilderExternalSourceSuggestionRecord> suggestions)
    {
        var latest = entries
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest is null)
        {
            return $"External recon mode is {GetReconModeLabel(reconMode)}. Manual intake stays explicit and advisory-only.";
        }

        return $"External recon mode is {GetReconModeLabel(reconMode)}. Recorded {entries.Count} intake action(s) and {suggestions.Count} suggestion(s). Latest action: {latest.ActionType.Replace('_', ' ')} -> {latest.Status.Replace('_', ' ')}.";
    }

    private static string BuildSnapshotSummary(string workspaceId, IReadOnlyList<BuilderExternalSourceSnapshotRecord> snapshots)
        => snapshots.Count == 0
            ? $"No external source snapshots recorded for {workspaceId}."
            : $"Recorded {snapshots.Count} external snapshot(s) for {workspaceId}.";

    private static string BuildEvaluationSummary(string workspaceId, IReadOnlyList<BuilderExternalCodeEvaluationRecord> evaluations)
        => evaluations.Count == 0
            ? $"No external code evaluations recorded for {workspaceId}."
            : $"Recorded {evaluations.Count} external evaluation(s) for {workspaceId}.";

    private static string BuildVendorCandidateSummary(string workspaceId, IReadOnlyList<BuilderVendorCandidateRecord> candidates)
        => candidates.Count == 0
            ? $"No vendor candidates recorded for {workspaceId}."
            : $"Recorded {candidates.Count} vendor candidate(s) for {workspaceId}.";

    private static string BuildProvenanceSummary(string workspaceId, IReadOnlyList<BuilderExternalProvenanceEntryRecord> entries)
        => entries.Count == 0
            ? $"No external provenance entries recorded for {workspaceId}."
            : $"Recorded {entries.Count} provenance entry(ies) for {workspaceId}.";

    private static IReadOnlyList<BuilderExternalSourceSuggestionRecord> BuildSuggestions(string repoRoot, string reconMode)
    {
        if (!ModeAllowsSuggestions(reconMode) || string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return Array.Empty<BuilderExternalSourceSuggestionRecord>();
        }

        var capabilities = BuilderWorkspaceService.LoadCapabilities(repoRoot);
        var routeWarnings = BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(repoRoot);
        var executionState = BuilderCrossRepoOrchestrationService.LoadExecutionState(repoRoot);
        var artifactLinks = BuildArtifactLinks(
            BuilderWorkspaceService.CapabilitiesPathForRepo(repoRoot),
            BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
            BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot),
            BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoRoot));
        var suggestions = new List<BuilderExternalSourceSuggestionRecord>();

        if (capabilities?.LanguagesDetected.Contains("csharp", StringComparer.OrdinalIgnoreCase) == true ||
            capabilities?.BuildSystems.Contains("msbuild", StringComparer.OrdinalIgnoreCase) == true)
        {
            suggestions.Add(new BuilderExternalSourceSuggestionRecord(
                "suggest_dotnet_reference",
                "Pinned .NET Reference",
                "reference_only",
                SourceKindRepo,
                IntakeModeReferenceOnly,
                true,
                artifactLinks,
                "Attach a pinned .NET repo or raw source file when the current workspace needs structure or test-shape reference."));
        }

        if (capabilities?.LanguagesDetected.Contains("typescript", StringComparer.OrdinalIgnoreCase) == true ||
            capabilities?.LanguagesDetected.Contains("javascript", StringComparer.OrdinalIgnoreCase) == true)
        {
            suggestions.Add(new BuilderExternalSourceSuggestionRecord(
                "suggest_typescript_reference",
                "Pinned TS/JS Reference",
                "reference_only",
                SourceKindRepo,
                IntakeModeReferenceOnly,
                true,
                artifactLinks,
                "Attach a pinned TypeScript or JavaScript repo when current work benefits from comparable build and test layout."));
        }

        if ((routeWarnings?.Entries.Count ?? 0) > 0 || (reviewWorkspace?.ReviewCounts.RejectedFiles ?? 0) > 0)
        {
            suggestions.Add(new BuilderExternalSourceSuggestionRecord(
                "suggest_reference_first",
                "Reference-First Intake",
                "reference_only",
                SourceKindFile,
                IntakeModeMetadataOnly,
                true,
                artifactLinks,
                "Current route or review instability suggests starting with metadata-only or reference-only intake before a broader snapshot."));
        }

        if ((executionState?.WorkspaceStatusList.Count ?? 0) > 1)
        {
            suggestions.Add(new BuilderExternalSourceSuggestionRecord(
                "suggest_cross_repo_dependency_reference",
                "Cross-Repo Dependency Reference",
                "reference_only",
                SourceKindRepo,
                IntakeModeReferenceOnly,
                true,
                artifactLinks,
                "Multi-repo orchestration is active, so an upstream dependency repo is best attached as read-only reference evidence first."));
        }

        return suggestions
            .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SuggestionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SourceResolution ResolveSource(string repoRoot, BuilderExternalIntakeRequest request, bool forSnapshot)
    {
        var sourceUrl = NormalizeSourceUrl(repoRoot, request.SourceUrl);
        var sourceKind = NormalizeSourceKind(request.SourceKind, sourceUrl, repoRoot);
        var requestedRef = request.RequestedRef?.Trim() ?? string.Empty;
        if (TryResolveLocalPath(repoRoot, sourceUrl, out var localPath))
        {
            if (Directory.Exists(localPath))
            {
                return CreateLocalResolution(sourceUrl, SourceKindRepo, requestedRef, localPath);
            }

            if (File.Exists(localPath) && IsZipPath(localPath))
            {
                return CreateLocalResolution(sourceUrl, SourceKindArchive, requestedRef, localPath);
            }

            if (File.Exists(localPath))
            {
                return CreateLocalResolution(sourceUrl, SourceKindFile, requestedRef, localPath);
            }

            return CreateFailedResolution(repoRoot, sourceUrl, sourceKind, requestedRef, "local_source_missing", $"Local source path {localPath} was not found.");
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateFailedResolution(repoRoot, sourceUrl, sourceKind, requestedRef, "unsupported_source_url", $"Source URL {sourceUrl} is not a supported local or HTTP(S) target.");
        }

        if (string.Equals(sourceKind, SourceKindRepo, StringComparison.OrdinalIgnoreCase))
        {
            if (TryBuildRemoteRepoArchiveUri(uri, requestedRef, out var archiveUri, out var archiveFailure) && archiveUri is not null)
            {
                var tempArchive = DownloadRemoteBytes(archiveUri, ".zip");
                return CreateRemoteResolution(repoRoot, sourceUrl, SourceKindArchive, requestedRef, uri, tempArchive);
            }

            return forSnapshot
                ? CreateFailedResolution(repoRoot, sourceUrl, sourceKind, requestedRef, archiveFailure.Classification, archiveFailure.Summary)
                : CreateRemoteResolution(repoRoot, sourceUrl, sourceKind, requestedRef, uri, string.Empty);
        }

        var extension = string.Equals(sourceKind, SourceKindArchive, StringComparison.OrdinalIgnoreCase) ? ".zip" : Path.GetExtension(uri.AbsolutePath);
        var tempPath = DownloadRemoteBytes(uri, string.IsNullOrWhiteSpace(extension) ? ".tmp" : extension);
        return CreateRemoteResolution(repoRoot, sourceUrl, sourceKind, requestedRef, uri, tempPath);
    }

    private static BuilderExternalReconEntryRecord BuildMetadataEntry(
        string repoRoot,
        string reconMode,
        BuilderExternalIntakeRequest request,
        DateTimeOffset observedUtc)
    {
        var resolution = ResolveSource(repoRoot, request, forSnapshot: false);
        if (!string.IsNullOrWhiteSpace(resolution.FailureClassification))
        {
            return BuildFailureEntry(repoRoot, reconMode, request, "discover_metadata", resolution.FailureClassification, resolution.FailureSummary, observedUtc);
        }

        var analysis = AnalyzeResolution(resolution);
        var metadata = BuildMetadataRecord(analysis, resolution);
        return new BuilderExternalReconEntryRecord(
            ComputeDeterministicId("recon", "discover_metadata", metadata.CanonicalSourceId, metadata.ResolvedCommitOrContentHash, request.RequestedRef),
            "discover_metadata",
            reconMode,
            resolution.SourceUrl,
            resolution.SourceKind,
            resolution.RequestedRef,
            NormalizeIntakeMode(request.IntakeMode),
            request.OperatorNote?.Trim() ?? string.Empty,
            "metadata_recorded",
            string.Empty,
            metadata,
            BuildArtifactLinks(
                ExternalReconPathForRepo(repoRoot),
                ExternalSourceSnapshotsPathForRepo(repoRoot),
                ExternalProvenanceIndexPathForRepo(repoRoot)),
            metadata.Summary,
            observedUtc);
    }

    private static BuilderExternalSourceMetadataRecord BuildMetadataRecord(SourceAnalysis analysis, SourceResolution resolution)
        => new(
            resolution.CanonicalSourceId,
            resolution.SourceHost,
            resolution.SourceProvider,
            resolution.RepoName,
            resolution.OwnerOrOrg,
            resolution.ResolvedRef,
            string.IsNullOrWhiteSpace(resolution.ResolvedCommitOrContentHash) ? analysis.ContentHash : resolution.ResolvedCommitOrContentHash,
            analysis.Languages,
            analysis.IncludedPaths.Count,
            analysis.HasTests,
            analysis.BuildSystemMarkers,
            analysis.DependencyManifestMarkers,
            analysis.LicenseMetadata,
            analysis.LicenseStatus,
            analysis.AvailabilityState,
            $"{resolution.SourceKind.Replace('-', ' ')} metadata for {resolution.RepoName}: {analysis.IncludedPaths.Count} path(s), languages={DescribeList(analysis.Languages)}, build markers={DescribeList(analysis.BuildSystemMarkers)}, license={analysis.LicenseStatus.Replace('_', ' ')}.");

    private static BuilderExternalSourceSnapshotsRecord CreateSnapshotInternal(
        string repoRoot,
        BuilderExternalReconRecord recon,
        BuilderExternalIntakeRequest request,
        DateTimeOffset observedUtc)
    {
        var resolution = ResolveSource(repoRoot, request, forSnapshot: true);
        if (!string.IsNullOrWhiteSpace(resolution.FailureClassification))
        {
            var failure = BuildFailureEntry(repoRoot, recon.ReconMode, request, "create_snapshot", resolution.FailureClassification, resolution.FailureSummary, observedUtc);
            var updatedRecon = MergeReconEntry(repoRoot, recon, failure, observedUtc);
            EnsureRoots(repoRoot);
            Save(updatedRecon.ArtifactPath, updatedRecon);
            return LoadExternalSourceSnapshots(repoRoot) ?? CreateDefaultSnapshotsRecord(repoRoot, observedUtc);
        }

        var analysis = AnalyzeResolution(resolution);
        var snapshotId = ComputeDeterministicId("snapshot", resolution.CanonicalSourceId, analysis.ContentHash, resolution.RequestedRef, analysis.Scope);
        var snapshotRoot = SnapshotContentPathForRepo(repoRoot, snapshotId);
        EnsureSnapshotContent(snapshotRoot, analysis);
        var snapshot = new BuilderExternalSourceSnapshotRecord(
            snapshotId,
            resolution.SourceUrl,
            resolution.SourceKind,
            resolution.RequestedRef,
            resolution.CanonicalSourceId,
            resolution.ResolvedCommit,
            string.IsNullOrWhiteSpace(resolution.ResolvedCommitOrContentHash) ? analysis.ContentHash : resolution.ResolvedCommitOrContentHash,
            analysis.ContentHash,
            analysis.LicenseMetadata,
            analysis.LicenseStatus,
            analysis.Scope,
            analysis.IncludedPaths,
            analysis.ExcludedPaths,
            snapshotRoot,
            BuildArtifactLinks(
                snapshotRoot,
                ExternalSourceSnapshotsPathForRepo(repoRoot),
                ExternalProvenanceIndexPathForRepo(repoRoot)),
            $"Pinned snapshot {snapshotId} captures {analysis.IncludedPaths.Count} path(s) from {resolution.RepoName}.",
            observedUtc);

        var existing = LoadExternalSourceSnapshots(repoRoot) ?? CreateDefaultSnapshotsRecord(repoRoot, observedUtc);
        var merged = MergeById(existing.Snapshots, snapshot, entry => entry.SnapshotId);
        var updated = existing with
        {
            Snapshots = merged,
            Summary = BuildSnapshotSummary(existing.WorkspaceId, merged),
            ObservedUtc = observedUtc
        };
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);

        UpdateProvenanceIndex(
            repoRoot,
            resolution.SourceUrl,
            resolution.CanonicalSourceId,
            snapshot.ResolvedCommitOrContentHash,
            snapshot.License,
            snapshot.LicenseStatus,
            snapshot.ContentHash,
            snapshot.SnapshotId,
            string.Empty,
            string.Empty,
            snapshot.ArtifactLinks,
            observedUtc);

        var reconEntry = new BuilderExternalReconEntryRecord(
            ComputeDeterministicId("recon", "create_snapshot", resolution.CanonicalSourceId, snapshot.ContentHash),
            "create_snapshot",
            recon.ReconMode,
            resolution.SourceUrl,
            resolution.SourceKind,
            resolution.RequestedRef,
            NormalizeIntakeMode(request.IntakeMode),
            request.OperatorNote?.Trim() ?? string.Empty,
            "snapshot_recorded",
            string.Empty,
            BuildMetadataRecord(analysis, resolution),
            BuildArtifactLinks(updated.ArtifactPath, snapshotRoot, ExternalProvenanceIndexPathForRepo(repoRoot)),
            $"Pinned snapshot {snapshot.SnapshotId} recorded for {resolution.RepoName}.",
            observedUtc);
        var updatedReconArtifact = MergeReconEntry(repoRoot, recon, reconEntry, observedUtc);
        Save(updatedReconArtifact.ArtifactPath, updatedReconArtifact);
        return updated;
    }

    private static BuilderExternalCodeEvaluationsRecord EvaluateSnapshotInternal(string repoRoot, string snapshotId, DateTimeOffset observedUtc)
    {
        var snapshots = LoadExternalSourceSnapshots(repoRoot) ?? CreateDefaultSnapshotsRecord(repoRoot, observedUtc);
        var snapshot = snapshots.Snapshots.FirstOrDefault(entry => string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || !Directory.Exists(snapshot.SnapshotRoot))
        {
            return LoadExternalCodeEvaluations(repoRoot) ?? CreateDefaultEvaluationsRecord(repoRoot, observedUtc);
        }

        var analysis = AnalyzeLocalContent(snapshot.SnapshotRoot, snapshot.SnapshotScope);
        var workspaceCapabilities = BuilderWorkspaceService.LoadCapabilities(repoRoot);
        var workspaceLanguages = new HashSet<string>(workspaceCapabilities?.LanguagesDetected ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var workspaceBuildSystems = new HashSet<string>(workspaceCapabilities?.BuildSystems ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var languageOverlap = analysis.Languages.Count(language => workspaceLanguages.Contains(language));
        var buildOverlap = analysis.BuildSystemMarkers.Count(marker => workspaceBuildSystems.Contains(marker));
        var usefulnessScore = ClampScore(20d + languageOverlap * 20d + Math.Min(buildOverlap, 2) * 10d + (analysis.HasTests ? 15d : 0d) + (analysis.IncludedPaths.Count <= 80 ? 10d : 0d));
        var qualityScore = ClampScore(15d + (analysis.HasTests ? 25d : 0d) + (analysis.BuildSystemMarkers.Count > 0 ? 15d : 0d) + (analysis.DependencyManifestMarkers.Count > 0 ? 10d : 0d) + (string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase) ? 20d : 0d) + (analysis.IncludedPaths.Count is > 0 and <= 250 ? 15d : 5d));
        var riskScore = ClampScore((string.Equals(snapshot.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase) ? 40d : 0d) + (string.Equals(snapshot.LicenseStatus, "license_unknown", StringComparison.OrdinalIgnoreCase) ? 20d : 0d) + (analysis.DependencyManifestMarkers.Count > 1 ? 15d : 5d) + (analysis.BuildSystemMarkers.Count > 1 ? 10d : 0d) + (!analysis.HasTests ? 10d : 0d) + (analysis.IncludedPaths.Count > 200 ? 15d : 5d));
        var compatibility = languageOverlap > 0 || buildOverlap > 0
            ? (riskScore >= 65d ? "manual_review_required" : "compatible")
            : "incompatible";
        var recommendedUsage = string.Equals(snapshot.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase)
            ? "unsafe_or_incompatible"
            : compatibility switch
            {
                "incompatible" => usefulnessScore >= 40d ? "reference_only" : "unsafe_or_incompatible",
                "manual_review_required" => "manual_review_required",
                _ when usefulnessScore >= 70d && qualityScore >= 60d && riskScore < 45d && string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase) => "vendor_candidate",
                _ when usefulnessScore >= 55d && riskScore < 60d => "snippet_candidate",
                _ => "reference_only"
            };
        var evaluation = new BuilderExternalCodeEvaluationRecord(
            ComputeDeterministicId("evaluation", snapshot.SnapshotId, recommendedUsage, snapshot.ContentHash),
            snapshot.SnapshotId,
            usefulnessScore,
            qualityScore,
            riskScore,
            snapshot.LicenseStatus,
            compatibility,
            recommendedUsage,
            !string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase) || riskScore >= 60d || !string.Equals(compatibility, "compatible", StringComparison.OrdinalIgnoreCase),
            BuildArtifactLinks(snapshot.ArtifactLinks, new[] { ExternalCodeEvaluationsPathForRepo(repoRoot) }, new[] { ExternalProvenanceIndexPathForRepo(repoRoot) }),
            $"Evaluation for snapshot {snapshot.SnapshotId}: usage={recommendedUsage.Replace('_', ' ')}, compatibility={compatibility.Replace('_', ' ')}, risk={riskScore:0.##}.",
            observedUtc);

        var existing = LoadExternalCodeEvaluations(repoRoot) ?? CreateDefaultEvaluationsRecord(repoRoot, observedUtc);
        var merged = MergeById(existing.Evaluations, evaluation, entry => entry.EvaluationId);
        var updated = existing with
        {
            Evaluations = merged,
            Summary = BuildEvaluationSummary(existing.WorkspaceId, merged),
            ObservedUtc = observedUtc
        };
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);
        UpdateProvenanceEvaluation(repoRoot, snapshot, evaluation, observedUtc);
        return updated;
    }

    private static BuilderVendorCandidatesRecord StageVendorCandidateInternal(
        string repoRoot,
        string snapshotId,
        IReadOnlyList<string>? selectedPaths,
        DateTimeOffset observedUtc)
    {
        var snapshots = LoadExternalSourceSnapshots(repoRoot);
        var snapshot = snapshots?.Snapshots.FirstOrDefault(entry => string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return LoadVendorCandidates(repoRoot) ?? CreateDefaultVendorCandidatesRecord(repoRoot, observedUtc);
        }

        var evaluations = LoadExternalCodeEvaluations(repoRoot);
        var evaluation = evaluations?.Evaluations.Where(entry => string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(entry => entry.ObservedUtc).ThenBy(entry => entry.EvaluationId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var selected = (selectedPaths ?? snapshot.IncludedPaths).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var repoNameSegment = SanitizePathSegment(Path.GetFileNameWithoutExtension(snapshot.ResolvedSourceId));
        var candidate = new BuilderVendorCandidateRecord(
            ComputeDeterministicId("vendor_candidate", snapshot.SnapshotId, string.Join("|", selected)),
            snapshot.SnapshotId,
            snapshot.SnapshotScope,
            selected,
            ExternalProvenanceIndexPathForRepo(repoRoot),
            snapshot.LicenseStatus,
            BuildVendorRiskSummary(snapshot, evaluation),
            evaluation?.RequiresManualReview ?? !string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase),
            Path.Combine("vendor", string.IsNullOrWhiteSpace(repoNameSegment) ? "external-source" : repoNameSegment).Replace('\\', '/'),
            BuildArtifactLinks(snapshot.ArtifactLinks, evaluation?.ArtifactLinks, new[] { VendorCandidatesPathForRepo(repoRoot) }, new[] { ExternalProvenanceIndexPathForRepo(repoRoot) }),
            $"Vendor candidate for snapshot {snapshot.SnapshotId} stages {selected.Length} path(s) as advisory-only intake.",
            observedUtc);

        var existing = LoadVendorCandidates(repoRoot) ?? CreateDefaultVendorCandidatesRecord(repoRoot, observedUtc);
        var merged = MergeById(existing.Candidates, candidate, entry => entry.CandidateId);
        var updated = existing with
        {
            Candidates = merged,
            Summary = BuildVendorCandidateSummary(existing.WorkspaceId, merged),
            ObservedUtc = observedUtc
        };
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);
        UpdateProvenanceVendorCandidate(repoRoot, snapshot, evaluation, candidate, observedUtc);
        return updated;
    }

    private static SourceAnalysis AnalyzeResolution(SourceResolution resolution)
    {
        if (!string.IsNullOrWhiteSpace(resolution.LocalPath))
        {
            if (Directory.Exists(resolution.LocalPath))
            {
                return AnalyzeLocalContent(resolution.LocalPath, "whole_repo");
            }

            if (File.Exists(resolution.LocalPath) && IsZipPath(resolution.LocalPath))
            {
                using var extraction = ExtractZipToTemp(resolution.LocalPath);
                return AnalyzeLocalContent(extraction.RootPath, "whole_repo");
            }

            if (File.Exists(resolution.LocalPath))
            {
                return AnalyzeLocalFile(resolution.LocalPath);
            }
        }

        return new SourceAnalysis(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            "license_unknown",
            string.Empty,
            string.Empty,
            "url_only",
            "metadata_only")
        {
            RootPath = string.Empty,
            SourceFiles = Array.Empty<string>()
        };
    }

    private static SourceAnalysis AnalyzeLocalContent(string rootPath, string scope)
    {
        var files = EnumerateIncludedFiles(rootPath)
            .OrderBy(path => NormalizeRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var buildMarkers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyMarkers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedPaths = new List<string>();
        var excludedPaths = EnumerateSkippedDirectories(rootPath).ToArray();
        var hasTests = false;
        foreach (var file in files)
        {
            var relativePath = NormalizeRelativePath(rootPath, file);
            includedPaths.Add(relativePath);
            if (LanguageByExtension.TryGetValue(Path.GetExtension(file), out var language))
            {
                languages.Add(language);
            }

            if (relativePath.Contains("/test", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Contains("/tests", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).EndsWith("_test.go", StringComparison.OrdinalIgnoreCase))
            {
                hasTests = true;
            }

            foreach (var marker in ResolveBuildMarkers(Path.GetFileName(file)))
            {
                buildMarkers.Add(marker);
            }

            foreach (var marker in ResolveDependencyMarkers(Path.GetFileName(file)))
            {
                dependencyMarkers.Add(marker);
            }
        }

        var license = DetectLicense(rootPath, files);
        return new SourceAnalysis(
            languages.ToArray(),
            buildMarkers.ToArray(),
            dependencyMarkers.ToArray(),
            includedPaths.ToArray(),
            excludedPaths,
            hasTests,
            license.Status,
            license.Metadata,
            ComputeDeterministicContentHash(rootPath, files),
            "content_scanned",
            scope)
        {
            RootPath = rootPath,
            SourceFiles = files
        };
    }

    private static SourceAnalysis AnalyzeLocalFile(string filePath)
    {
        var rootPath = Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = Path.GetFileName(filePath);
        var languages = LanguageByExtension.TryGetValue(Path.GetExtension(filePath), out var language)
            ? new[] { language }
            : Array.Empty<string>();
        var license = DetectLicense(rootPath, new[] { filePath });
        return new SourceAnalysis(
            languages,
            ResolveBuildMarkers(fileName),
            ResolveDependencyMarkers(fileName),
            new[] { fileName.Replace('\\', '/') },
            Array.Empty<string>(),
            fileName.Contains("test", StringComparison.OrdinalIgnoreCase),
            license.Status,
            license.Metadata,
            ComputeFileHash(filePath),
            "content_scanned",
            "single_file")
        {
            RootPath = rootPath,
            SourceFiles = new[] { filePath }
        };
    }

    private static void EnsureSnapshotContent(string snapshotRoot, SourceAnalysis analysis)
    {
        if (Directory.Exists(snapshotRoot))
        {
            return;
        }

        Directory.CreateDirectory(snapshotRoot);
        foreach (var path in analysis.SourceFiles)
        {
            var relativePath = string.IsNullOrWhiteSpace(analysis.RootPath)
                ? Path.GetFileName(path)
                : NormalizeRelativePath(analysis.RootPath, path);
            var destinationPath = Path.Combine(snapshotRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(path, destinationPath, overwrite: true);
        }
    }

    private static void UpdateProvenanceEvaluation(
        string repoRoot,
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord evaluation,
        DateTimeOffset observedUtc)
    {
        var existing = LoadExternalProvenanceIndex(repoRoot) ?? CreateDefaultProvenanceIndexRecord(repoRoot, observedUtc);
        var prior = existing.Entries.FirstOrDefault(entry => string.Equals(entry.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));
        var record = new BuilderExternalProvenanceEntryRecord(
            ComputeDeterministicId("provenance", snapshot.ResolvedSourceId, snapshot.ResolvedCommitOrContentHash, snapshot.SnapshotId),
            prior?.OriginalUrl ?? snapshot.SourceUrl,
            prior?.CanonicalSourceId ?? snapshot.ResolvedSourceId,
            snapshot.ResolvedCommitOrContentHash,
            snapshot.License,
            snapshot.LicenseStatus,
            snapshot.ContentHash,
            snapshot.SnapshotId,
            evaluation.EvaluationId,
            prior?.VendorCandidateId ?? string.Empty,
            BuildArtifactLinks(snapshot.ArtifactLinks, evaluation.ArtifactLinks, new[] { ExternalProvenanceIndexPathForRepo(repoRoot) }),
            $"Source {snapshot.ResolvedSourceId} is pinned to {snapshot.ResolvedCommitOrContentHash} with evaluation {evaluation.EvaluationId}.",
            observedUtc);
        SaveProvenance(repoRoot, existing, record, observedUtc);
    }

    private static void UpdateProvenanceVendorCandidate(
        string repoRoot,
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord candidate,
        DateTimeOffset observedUtc)
    {
        var existing = LoadExternalProvenanceIndex(repoRoot) ?? CreateDefaultProvenanceIndexRecord(repoRoot, observedUtc);
        var prior = existing.Entries.FirstOrDefault(entry => string.Equals(entry.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));
        var record = new BuilderExternalProvenanceEntryRecord(
            ComputeDeterministicId("provenance", snapshot.ResolvedSourceId, snapshot.ResolvedCommitOrContentHash, snapshot.SnapshotId),
            prior?.OriginalUrl ?? snapshot.SourceUrl,
            prior?.CanonicalSourceId ?? snapshot.ResolvedSourceId,
            snapshot.ResolvedCommitOrContentHash,
            snapshot.License,
            snapshot.LicenseStatus,
            snapshot.ContentHash,
            snapshot.SnapshotId,
            evaluation?.EvaluationId ?? prior?.EvaluationId ?? string.Empty,
            candidate.CandidateId,
            BuildArtifactLinks(snapshot.ArtifactLinks, candidate.ArtifactLinks, new[] { ExternalProvenanceIndexPathForRepo(repoRoot) }),
            $"Source {snapshot.ResolvedSourceId} is pinned to {snapshot.ResolvedCommitOrContentHash} with vendor candidate {candidate.CandidateId}.",
            observedUtc);
        SaveProvenance(repoRoot, existing, record, observedUtc);
    }

    private static void UpdateProvenanceIndex(
        string repoRoot,
        string originalUrl,
        string canonicalSourceId,
        string resolvedCommitOrContentHash,
        string licenseMetadata,
        string licenseStatus,
        string snapshotHash,
        string snapshotId,
        string evaluationId,
        string vendorCandidateId,
        IReadOnlyList<string> artifactLinks,
        DateTimeOffset observedUtc)
    {
        var existing = LoadExternalProvenanceIndex(repoRoot) ?? CreateDefaultProvenanceIndexRecord(repoRoot, observedUtc);
        var record = new BuilderExternalProvenanceEntryRecord(
            ComputeDeterministicId("provenance", canonicalSourceId, resolvedCommitOrContentHash, snapshotId),
            originalUrl,
            canonicalSourceId,
            resolvedCommitOrContentHash,
            licenseMetadata,
            licenseStatus,
            snapshotHash,
            snapshotId,
            evaluationId,
            vendorCandidateId,
            BuildArtifactLinks(artifactLinks, new[] { ExternalProvenanceIndexPathForRepo(repoRoot) }),
            $"Source {canonicalSourceId} is pinned to {resolvedCommitOrContentHash} with license state {licenseStatus.Replace('_', ' ')}.",
            observedUtc);
        SaveProvenance(repoRoot, existing, record, observedUtc);
    }

    private static void SaveProvenance(
        string repoRoot,
        BuilderExternalProvenanceIndexRecord existing,
        BuilderExternalProvenanceEntryRecord record,
        DateTimeOffset observedUtc)
    {
        var merged = MergeById(existing.Entries, record, entry => entry.ProvenanceId);
        var updated = existing with
        {
            Entries = merged,
            Summary = BuildProvenanceSummary(existing.WorkspaceId, merged),
            ObservedUtc = observedUtc
        };
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);
    }

    private static SourceResolution CreateLocalResolution(string sourceUrl, string sourceKind, string requestedRef, string localPath)
    {
        var resolvedCommit = Directory.Exists(localPath) ? TryReadGitHead(localPath) : string.Empty;
        return new SourceResolution(
            sourceUrl,
            sourceKind,
            requestedRef,
            ComputeCanonicalSourceId(string.Empty, localPath),
            "local_file_system",
            "local",
            ResolveOwnerOrOrg(localPath),
            ResolveRepoName(localPath),
            string.IsNullOrWhiteSpace(requestedRef) ? resolvedCommit : requestedRef,
            resolvedCommit,
            resolvedCommit,
            localPath,
            false,
            string.Empty,
            string.Empty);
    }

    private static SourceResolution CreateRemoteResolution(string repoRoot, string sourceUrl, string sourceKind, string requestedRef, Uri uri, string localPath)
        => new(
            sourceUrl,
            sourceKind,
            requestedRef,
            ComputeCanonicalSourceId(repoRoot, sourceUrl),
            uri.Host,
            ResolveSourceProvider(sourceUrl),
            ResolveOwnerOrOrg(sourceUrl),
            ResolveRepoName(sourceUrl),
            requestedRef,
            string.Empty,
            string.Empty,
            localPath,
            !string.IsNullOrWhiteSpace(localPath),
            string.Empty,
            string.Empty);

    private static SourceResolution CreateFailedResolution(string repoRoot, string sourceUrl, string sourceKind, string requestedRef, string failureClassification, string failureSummary)
        => new(
            sourceUrl,
            sourceKind,
            requestedRef,
            ComputeCanonicalSourceId(repoRoot, sourceUrl),
            ResolveSourceHost(sourceUrl),
            ResolveSourceProvider(sourceUrl),
            ResolveOwnerOrOrg(sourceUrl),
            ResolveRepoName(sourceUrl),
            requestedRef,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            failureClassification,
            failureSummary);

    private static IReadOnlyList<string> BuildArtifactLinks(params IEnumerable<string>?[] pathSets)
        => pathSets
            .Where(set => set is not null)
            .SelectMany(set => set!)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BuildArtifactLinks(params string[] paths)
        => BuildArtifactLinks(paths.AsEnumerable());

    private static IReadOnlyList<TRecord> MergeById<TRecord>(
        IReadOnlyList<TRecord> existing,
        TRecord item,
        Func<TRecord, string> idSelector)
        => existing
            .Concat(new[] { item })
            .GroupBy(idSelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(entry => idSelector(entry), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildVendorRiskSummary(BuilderExternalSourceSnapshotRecord snapshot, BuilderExternalCodeEvaluationRecord? evaluation)
    {
        if (evaluation is null)
        {
            return $"License state {snapshot.LicenseStatus.Replace('_', ' ')}; evaluation has not been recorded yet.";
        }

        return $"Risk {evaluation.RiskScore:0.##}; usage {evaluation.RecommendedUsage.Replace('_', ' ')}; license {snapshot.LicenseStatus.Replace('_', ' ')}.";
    }

    private static string NormalizeSourceUrl(string repoRoot, string sourceUrl)
    {
        var trimmed = sourceUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return uri.LocalPath;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return uri.ToString();
            }
        }

        return Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(trimmed, string.IsNullOrWhiteSpace(repoRoot) ? global::System.Environment.CurrentDirectory : repoRoot);
    }

    private static bool TryResolveLocalPath(string repoRoot, string sourceUrl, out string localPath)
    {
        localPath = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                localPath = Path.GetFullPath(uri.LocalPath);
                return true;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var candidate = Path.IsPathRooted(sourceUrl)
            ? Path.GetFullPath(sourceUrl)
            : Path.GetFullPath(sourceUrl, string.IsNullOrWhiteSpace(repoRoot) ? global::System.Environment.CurrentDirectory : repoRoot);
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            localPath = candidate;
            return true;
        }

        return false;
    }

    private static string ComputeCanonicalSourceId(string repoRoot, string value)
    {
        if (TryResolveLocalPath(repoRoot, value, out var localPath))
        {
            return $"local::{localPath.Trim().ToLowerInvariant()}";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant()
            : value.Trim().ToLowerInvariant();
    }

    private static string ResolveSourceHost(string sourceUrl)
        => Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "local_file_system";

    private static string ResolveSourceProvider(string sourceUrl)
    {
        var host = ResolveSourceHost(sourceUrl);
        return host.ToLowerInvariant() switch
        {
            "github.com" => "github",
            "raw.githubusercontent.com" => "github_raw",
            "gitlab.com" => "gitlab",
            "local_file_system" => "local",
            _ => host
        };
    }

    private static string ResolveRepoName(string source)
    {
        if (TryResolveLocalPath(string.Empty, source, out var localPath))
        {
            return Directory.Exists(localPath)
                ? new DirectoryInfo(localPath).Name
                : Path.GetFileNameWithoutExtension(localPath);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                return segments[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return segments.LastOrDefault() ?? uri.Host;
        }

        return "external-source";
    }

    private static string ResolveOwnerOrOrg(string source)
    {
        if (TryResolveLocalPath(string.Empty, source, out var localPath))
        {
            return Directory.GetParent(Directory.Exists(localPath) ? localPath : (Path.GetDirectoryName(localPath) ?? localPath))?.Name ?? "local";
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? uri.Host;
        }

        return "external";
    }

    private static bool IsZipPath(string path)
        => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    private static string DownloadRemoteBytes(Uri uri, string extension)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Shoots", "external-recon");
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{extension}");
        var bytes = HttpClient.GetByteArrayAsync(uri).GetAwaiter().GetResult();
        File.WriteAllBytes(tempPath, bytes);
        return tempPath;
    }

    private static bool TryBuildRemoteRepoArchiveUri(Uri repoUri, string requestedRef, out Uri? archiveUri, out (string Classification, string Summary) failure)
    {
        archiveUri = null;
        if (string.IsNullOrWhiteSpace(requestedRef))
        {
            failure = ("unpinned_remote_repo", "Remote repository snapshotting needs an explicit ref so the source can be frozen into a deterministic archive.");
            return true;
        }

        var segments = repoUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (string.Equals(repoUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
        {
            var owner = segments[0];
            var repo = segments[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            archiveUri = new Uri($"https://codeload.github.com/{owner}/{repo}/zip/{requestedRef}");
            failure = default;
            return true;
        }

        failure = ("unsupported_remote_repo_provider", $"Repository URL {repoUri} does not map to a deterministic archive download yet.");
        return false;
    }

    private static TemporaryExtraction ExtractZipToTemp(string archivePath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Shoots", "external-recon", $"zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        ZipFile.ExtractToDirectory(archivePath, tempRoot);
        var rootPath = Directory.EnumerateDirectories(tempRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? tempRoot;
        return new TemporaryExtraction(tempRoot, rootPath);
    }

    private static string TryReadGitHead(string repoRoot)
    {
        try
        {
            var gitHead = Path.Combine(repoRoot, ".git", "HEAD");
            if (!File.Exists(gitHead))
            {
                return string.Empty;
            }

            var head = File.ReadAllText(gitHead).Trim();
            if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                return IsCommitish(head) ? head : string.Empty;
            }

            var refPath = head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            var fullRefPath = Path.Combine(repoRoot, ".git", refPath);
            if (File.Exists(fullRefPath))
            {
                var commit = File.ReadAllText(fullRefPath).Trim();
                return IsCommitish(commit) ? commit : string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateIncludedFiles(string rootPath)
        => Directory.Exists(rootPath)
            ? Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .Where(path => !IsSkippedPath(rootPath, path))
            : Array.Empty<string>();

    private static string[] EnumerateSkippedDirectories(string rootPath)
        => !Directory.Exists(rootPath)
            ? Array.Empty<string>()
            : Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                .Where(path => NormalizeRelativePath(rootPath, path)
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => SkippedDirectories.Contains(segment)))
                .Select(path => NormalizeRelativePath(rootPath, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static bool IsSkippedPath(string rootPath, string path)
        => NormalizeRelativePath(rootPath, path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => SkippedDirectories.Contains(segment));

    private static string NormalizeRelativePath(string rootPath, string path)
        => Path.GetRelativePath(rootPath, path).Replace('\\', '/');

    private static string[] ResolveBuildMarkers(string fileName)
        => fileName.ToLowerInvariant() switch
        {
            "package.json" => new[] { "npm" },
            "package-lock.json" => new[] { "npm" },
            "pnpm-lock.yaml" => new[] { "pnpm" },
            "yarn.lock" => new[] { "yarn" },
            "pyproject.toml" => new[] { "pyproject" },
            "requirements.txt" => new[] { "pip" },
            "go.mod" => new[] { "go" },
            "cargo.toml" => new[] { "cargo" },
            "pom.xml" => new[] { "maven" },
            "build.gradle" => new[] { "gradle" },
            "build.gradle.kts" => new[] { "gradle" },
            "cmakelists.txt" => new[] { "cmake" },
            _ when fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) => new[] { "msbuild" },
            _ when fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => new[] { "msbuild" },
            _ => Array.Empty<string>()
        };

    private static string[] ResolveDependencyMarkers(string fileName)
        => fileName.ToLowerInvariant() switch
        {
            "package.json" => new[] { "package.json" },
            "package-lock.json" => new[] { "package-lock.json" },
            "pnpm-lock.yaml" => new[] { "pnpm-lock.yaml" },
            "yarn.lock" => new[] { "yarn.lock" },
            "pyproject.toml" => new[] { "pyproject.toml" },
            "requirements.txt" => new[] { "requirements.txt" },
            "go.mod" => new[] { "go.mod" },
            "go.sum" => new[] { "go.sum" },
            "cargo.toml" => new[] { "Cargo.toml" },
            "cargo.lock" => new[] { "Cargo.lock" },
            "pom.xml" => new[] { "pom.xml" },
            "build.gradle" => new[] { "build.gradle" },
            "build.gradle.kts" => new[] { "build.gradle.kts" },
            _ when fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => new[] { "csproj" },
            _ => Array.Empty<string>()
        };

    private static (string Status, string Metadata) DetectLicense(string rootPath, IReadOnlyList<string> files)
    {
        var licenseFile = files
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                       fileName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) ||
                       fileName.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => NormalizeRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(licenseFile) || !File.Exists(licenseFile))
        {
            return ("license_unknown", string.Empty);
        }

        var text = ReadSmallText(licenseFile);
        if (ContainsAny(text, "mit license", "apache license", "bsd license", "isc license", "public domain"))
        {
            return ("license_clear", DetectLicenseName(text, Path.GetFileName(licenseFile)));
        }

        if (ContainsAny(text, "gnu general public license", "agpl", "lgpl", "sspl", "commons clause", "polyform"))
        {
            return ("license_restricted", DetectLicenseName(text, Path.GetFileName(licenseFile)));
        }

        return ("manual_license_review_required", DetectLicenseName(text, Path.GetFileName(licenseFile)));
    }

    private static string DetectLicenseName(string text, string fallback)
    {
        if (ContainsAny(text, "mit license"))
        {
            return "MIT";
        }

        if (ContainsAny(text, "apache license"))
        {
            return "Apache-2.0";
        }

        if (ContainsAny(text, "bsd license"))
        {
            return "BSD";
        }

        if (ContainsAny(text, "gnu general public license"))
        {
            return "GPL";
        }

        if (ContainsAny(text, "lgpl"))
        {
            return "LGPL";
        }

        if (ContainsAny(text, "agpl"))
        {
            return "AGPL";
        }

        return fallback;
    }

    private static string ReadSmallText(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[4096];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string ComputeDeterministicContentHash(string rootPath, IReadOnlyList<string> files)
    {
        using var sha = SHA256.Create();
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append(NormalizeRelativePath(rootPath, file).ToLowerInvariant())
                .Append('|')
                .Append(ComputeFileHash(file))
                .AppendLine();
        }

        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string ComputeDeterministicId(params string[] parts)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()[..16];
    }

    private static bool IsCommitish(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length >= 7 &&
           value.All(ch => char.IsDigit(ch) || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'));

    private static double ClampScore(double value)
        => Math.Max(0d, Math.Min(100d, Math.Round(value, 2)));

    private static string DescribeList(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string SanitizePathSegment(string? value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized.Replace(' ', '-');
    }

    private static string NormalizeSourceKind(string? sourceKind, string sourceUrl, string repoRoot)
    {
        var normalized = NormalizeSourceKind(sourceKind);
        if (sourceKind is not null && normalized != SourceKindRepo)
        {
            return normalized;
        }

        if (TryResolveLocalPath(repoRoot, sourceUrl, out var localPath))
        {
            if (Directory.Exists(localPath))
            {
                return SourceKindRepo;
            }

            return IsZipPath(localPath) ? SourceKindArchive : SourceKindFile;
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var lowerPath = uri.AbsolutePath.ToLowerInvariant();
            if (lowerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return SourceKindArchive;
            }

            if (!string.IsNullOrWhiteSpace(Path.GetExtension(lowerPath)))
            {
                return SourceKindFile;
            }
        }

        return normalized;
    }

    private static string NormalizeSourceKind(string? sourceKind)
        => sourceKind?.Trim().ToLowerInvariant() switch
        {
            SourceKindFile => SourceKindFile,
            SourceKindArchive => SourceKindArchive,
            SourceKindPackageSource => SourceKindPackageSource,
            _ => SourceKindRepo
        };

    private static string NormalizeIntakeMode(string? intakeMode)
        => intakeMode?.Trim().ToLowerInvariant() switch
        {
            IntakeModeSnapshotForReview => IntakeModeSnapshotForReview,
            IntakeModeVendorCandidate => IntakeModeVendorCandidate,
            IntakeModeReferenceOnly => IntakeModeReferenceOnly,
            _ => IntakeModeMetadataOnly
        };

    private static void EnsureRoots(string repoRoot)
    {
        Directory.CreateDirectory(ExternalRootForRepo(repoRoot));
        Directory.CreateDirectory(VendorRootForRepo(repoRoot));
        Directory.CreateDirectory(ProvenanceRootForRepo(repoRoot));
        Directory.CreateDirectory(SnapshotContentRootForRepo(repoRoot));
    }

    private static void Save<TRecord>(string path, TRecord record)
    {
        var saveLock = SaveLocks.GetOrAdd(path, _ => new object());
        lock (saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(record, SerializerOptions));
        }
    }

    private static TRecord? Load<TRecord>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<TRecord>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return default;
        }
    }

    private sealed record SourceResolution(
        string SourceUrl,
        string SourceKind,
        string RequestedRef,
        string CanonicalSourceId,
        string SourceHost,
        string SourceProvider,
        string OwnerOrOrg,
        string RepoName,
        string ResolvedRef,
        string ResolvedCommit,
        string ResolvedCommitOrContentHash,
        string LocalPath,
        bool WasFetched,
        string FailureClassification,
        string FailureSummary);

    private sealed record SourceAnalysis(
        IReadOnlyList<string> Languages,
        IReadOnlyList<string> BuildSystemMarkers,
        IReadOnlyList<string> DependencyManifestMarkers,
        IReadOnlyList<string> IncludedPaths,
        IReadOnlyList<string> ExcludedPaths,
        bool HasTests,
        string LicenseStatus,
        string LicenseMetadata,
        string ContentHash,
        string AvailabilityState,
        string Scope)
    {
        public string RootPath { get; init; } = string.Empty;
        public IReadOnlyList<string> SourceFiles { get; init; } = Array.Empty<string>();
    }

    private sealed record TemporaryExtraction(string TempRoot, string RootPath) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
