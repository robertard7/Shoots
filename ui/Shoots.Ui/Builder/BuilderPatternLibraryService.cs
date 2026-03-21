using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderPatternLibraryEligibilityRecord(
    string EntryCandidateId,
    string SourceSnapshotId,
    string VendorCandidateId,
    string LicenseStatus,
    string ReviewStatus,
    string ApprovalStatus,
    string EntryEligibility,
    string EntryEligibilityReason);

public sealed record BuilderPatternLibraryEntryRecord(
    string PatternEntryId,
    BuilderPatternLibraryEligibilityRecord Eligibility,
    string PatternName,
    string PatternType,
    IReadOnlyList<string> LanguageSet,
    string SourceOrigin,
    string SourceSnapshotId,
    string VendorCandidateId,
    string ApprovedScope,
    IReadOnlyList<string> KeyPaths,
    string PatternSummary,
    IReadOnlyList<string> StructuralMarkers,
    IReadOnlyList<string> DependencyMarkers,
    IReadOnlyList<string> BuildMarkers,
    double QualityScore,
    double RiskScore,
    string LicenseStatus,
    string ApprovedUsageClass,
    IReadOnlyList<string> ArtifactLinks,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryEntriesRecord(
    string LibraryId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternLibraryEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryIndexEntryRecord(
    string PatternEntryId,
    string PatternName,
    string PatternType,
    IReadOnlyList<string> LanguageSet,
    string SourceOrigin,
    string SourceSnapshotId,
    string ApprovedUsageClass,
    string LicenseStatus,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryIndexRecord(
    string LibraryId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternLibraryIndexEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryProvenanceEntryRecord(
    string ProvenanceId,
    string PatternEntryId,
    string OriginalUrl,
    string CanonicalSourceId,
    string ResolvedCommitOrContentHash,
    string SourceSnapshotId,
    string EvaluationId,
    string VendorCandidateId,
    string LicenseMetadata,
    string LicenseStatus,
    string ReviewStatus,
    string ApprovalStatus,
    string ApprovedUsageClass,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryProvenanceRecord(
    string LibraryId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternLibraryProvenanceEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternLibraryContextSnapshotRecord(
    string WorkspaceId,
    IReadOnlyList<string> LanguagesDetected,
    IReadOnlyList<string> BuildSystems,
    IReadOnlyList<string> FailureClasses,
    string Intent,
    string ConstraintProfileId,
    IReadOnlyList<string> ActiveConstraintTypes,
    string Summary);

public sealed record BuilderPatternLibraryMatchRecord(
    string MatchId,
    string WorkspaceId,
    string PatternEntryId,
    double MatchScore,
    IReadOnlyList<string> MatchReasons,
    string FitClassification,
    string RecommendedReferenceUsage,
    string LicenseCompatibility,
    IReadOnlyList<string> ArtifactLinks,
    string Summary);

public sealed record BuilderPatternLibraryMatchesRecord(
    string WorkspaceId,
    string SchemaVersion,
    BuilderPatternLibraryContextSnapshotRecord ContextSnapshot,
    IReadOnlyList<BuilderPatternLibraryMatchRecord> Matches,
    string AttachedPatternEntryId,
    string AttachedPatternMatchId,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderPatternLibraryService
{
    public const string PatternLibraryIndexFileName = "builder_pattern_library_index.json";
    public const string PatternLibraryEntriesFileName = "builder_pattern_library_entries.json";
    public const string PatternLibraryMatchesFileName = "builder_pattern_library_matches.json";
    public const string PatternLibraryProvenanceFileName = "builder_pattern_library_provenance.json";

    public const string PatternTypeProjectStructure = "project_structure_pattern";
    public const string PatternTypeBuildTestPipeline = "build_test_pipeline_pattern";
    public const string PatternTypeServiceWiring = "service_wiring_pattern";
    public const string PatternTypeUiViewModel = "ui_view_model_pattern";
    public const string PatternTypeArtifactGeneration = "artifact_generation_pattern";
    public const string PatternTypeDeterministicSerialization = "deterministic_serialization_pattern";
    public const string PatternTypeReviewApprovalWorkflow = "review_approval_workflow_pattern";
    public const string PatternTypeHelperUtilitySnippet = "helper_utility_snippet_pattern";

    public const string UsageClassReferenceOnly = "reference_only";
    public const string UsageClassStructuralInspirationOnly = "structural_inspiration_only";
    public const string UsageClassSnippetCandidate = "snippet_candidate";
    public const string UsageClassVendorableWithReview = "vendorable_with_review";
    public const string UsageClassRestrictedDoNotReuse = "restricted_do_not_reuse";

    private const string EntriesSchemaVersion = "builder_pattern_library_entries.v1";
    private const string IndexSchemaVersion = "builder_pattern_library_index.v1";
    private const string MatchesSchemaVersion = "builder_pattern_library_matches.v1";
    private const string ProvenanceSchemaVersion = "builder_pattern_library_provenance.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
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
        [".json"] = "json",
        [".xml"] = "xml",
        [".md"] = "markdown",
        [".ps1"] = "powershell"
    };

    public static string PatternLibraryRootForRepo(string repoRoot)
        => Path.Combine(Path.GetFullPath(repoRoot), ".codex", "pattern-library");

    public static string PatternLibraryMatchesRootForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), "pattern-library");

    public static string PatternLibraryEntriesPathForRepo(string repoRoot)
        => Path.Combine(PatternLibraryRootForRepo(repoRoot), PatternLibraryEntriesFileName);

    public static string PatternLibraryIndexPathForRepo(string repoRoot)
        => Path.Combine(PatternLibraryRootForRepo(repoRoot), PatternLibraryIndexFileName);

    public static string PatternLibraryProvenancePathForRepo(string repoRoot)
        => Path.Combine(PatternLibraryRootForRepo(repoRoot), PatternLibraryProvenanceFileName);

    public static string PatternLibraryMatchesPathForRepo(string repoRoot)
        => Path.Combine(PatternLibraryMatchesRootForRepo(repoRoot), PatternLibraryMatchesFileName);

    public static BuilderPatternLibraryEntriesRecord? LoadPatternLibraryEntries(string repoRoot)
        => Load<BuilderPatternLibraryEntriesRecord>(PatternLibraryEntriesPathForRepo(repoRoot));

    public static BuilderPatternLibraryIndexRecord? LoadPatternLibraryIndex(string repoRoot)
        => Load<BuilderPatternLibraryIndexRecord>(PatternLibraryIndexPathForRepo(repoRoot));

    public static BuilderPatternLibraryProvenanceRecord? LoadPatternLibraryProvenance(string repoRoot)
        => Load<BuilderPatternLibraryProvenanceRecord>(PatternLibraryProvenancePathForRepo(repoRoot));

    public static BuilderPatternLibraryMatchesRecord? LoadPatternLibraryMatches(string repoRoot)
        => Load<BuilderPatternLibraryMatchesRecord>(PatternLibraryMatchesPathForRepo(repoRoot));

    public static string GetPatternTypeLabel(string? patternType)
        => NormalizeToken(patternType) switch
        {
            PatternTypeProjectStructure => "Project Structure",
            PatternTypeBuildTestPipeline => "Build and Test Pipeline",
            PatternTypeServiceWiring => "Service Wiring",
            PatternTypeUiViewModel => "UI and ViewModel",
            PatternTypeArtifactGeneration => "Artifact Generation",
            PatternTypeDeterministicSerialization => "Deterministic Serialization",
            PatternTypeReviewApprovalWorkflow => "Review and Approval Workflow",
            PatternTypeHelperUtilitySnippet => "Helper Utility or Snippet",
            _ => "Approved Pattern"
        };

    public static string GetUsageClassLabel(string? usageClass)
        => NormalizeToken(usageClass) switch
        {
            UsageClassStructuralInspirationOnly => "Structural Inspiration Only",
            UsageClassSnippetCandidate => "Snippet Candidate",
            UsageClassVendorableWithReview => "Vendorable With Review",
            UsageClassRestrictedDoNotReuse => "Restricted - Do Not Reuse",
            _ => "Reference Only"
        };

    public static BuilderPatternLibraryEntriesRecord ApproveSnapshotAsPatternEntry(
        string repoRoot,
        string snapshotId,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var snapshotArtifact = BuilderExternalReconService.LoadExternalSourceSnapshots(repoRoot);
        var snapshot = snapshotArtifact?.Snapshots.FirstOrDefault(entry =>
            string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, effectiveObservedUtc);
        }

        var evaluation = LoadLatestEvaluationForSnapshot(repoRoot, snapshotId);
        var provenance = LoadLatestProvenanceForSnapshot(repoRoot, snapshotId);
        var eligibility = EvaluateEligibility(snapshot, evaluation, vendorCandidate: null, explicitApprovalSource: "approved_snapshot");
        if (!eligibility.IsEligible)
        {
            return LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, effectiveObservedUtc);
        }

        return SaveApprovedEntries(
            repoRoot,
            snapshot,
            evaluation,
            vendorCandidate: null,
            provenance,
            eligibility,
            effectiveObservedUtc);
    }

    public static BuilderPatternLibraryEntriesRecord ApproveVendorCandidateAsPatternEntry(
        string repoRoot,
        string candidateId,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var vendorArtifact = BuilderExternalReconService.LoadVendorCandidates(repoRoot);
        var vendorCandidate = vendorArtifact?.Candidates.FirstOrDefault(entry =>
            string.Equals(entry.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
        if (vendorCandidate is null)
        {
            return LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, effectiveObservedUtc);
        }

        var snapshotArtifact = BuilderExternalReconService.LoadExternalSourceSnapshots(repoRoot);
        var snapshot = snapshotArtifact?.Snapshots.FirstOrDefault(entry =>
            string.Equals(entry.SnapshotId, vendorCandidate.SnapshotId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, effectiveObservedUtc);
        }

        var evaluation = LoadLatestEvaluationForSnapshot(repoRoot, snapshot.SnapshotId);
        var provenance = LoadLatestProvenanceForSnapshot(repoRoot, snapshot.SnapshotId);
        var eligibility = EvaluateEligibility(snapshot, evaluation, vendorCandidate, "approved_vendor_candidate");
        if (!eligibility.IsEligible)
        {
            return LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, effectiveObservedUtc);
        }

        return SaveApprovedEntries(
            repoRoot,
            snapshot,
            evaluation,
            vendorCandidate,
            provenance,
            eligibility,
            effectiveObservedUtc);
    }

    public static BuilderPatternLibraryMatchesRecord? RefreshPatternLibraryMatches(
        string repoRoot,
        BuilderPatternLibraryEntriesRecord? entriesArtifact = null,
        BuilderWorkspaceCapabilitiesRecord? capabilities = null,
        BuilderRecoveryPlaybooksRecord? recovery = null,
        BuilderOperatorIntentRecord? intent = null,
        BuilderOperatorConstraintsRecord? constraints = null,
        string? attachedPatternEntryId = null,
        string? attachedPatternMatchId = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        entriesArtifact ??= LoadPatternLibraryEntries(repoRoot);
        if (entriesArtifact is null || entriesArtifact.Entries.Count == 0)
        {
            return null;
        }

        capabilities ??= BuilderWorkspaceService.LoadCapabilities(repoRoot);
        recovery ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        intent ??= BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        constraints ??= BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var existing = LoadPatternLibraryMatches(repoRoot);
        var resolvedAttachedEntryId = string.IsNullOrWhiteSpace(attachedPatternEntryId)
            ? existing?.AttachedPatternEntryId ?? string.Empty
            : attachedPatternEntryId.Trim();
        var resolvedAttachedMatchId = string.IsNullOrWhiteSpace(attachedPatternMatchId)
            ? existing?.AttachedPatternMatchId ?? string.Empty
            : attachedPatternMatchId.Trim();

        var contextSnapshot = BuildContextSnapshot(workspaceId, capabilities, recovery, intent, constraints);
        var matches = entriesArtifact.Entries
            .Select(entry => BuildMatch(repoRoot, workspaceId, entry, contextSnapshot))
            .OrderByDescending(entry => entry.MatchScore)
            .ThenBy(entry => FitClassificationRank(entry.FitClassification))
            .ThenBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!matches.Any(match => string.Equals(match.PatternEntryId, resolvedAttachedEntryId, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedAttachedEntryId = string.Empty;
            resolvedAttachedMatchId = string.Empty;
        }
        else if (!matches.Any(match => string.Equals(match.MatchId, resolvedAttachedMatchId, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedAttachedMatchId = matches.FirstOrDefault(match =>
                string.Equals(match.PatternEntryId, resolvedAttachedEntryId, StringComparison.OrdinalIgnoreCase))?.MatchId ?? string.Empty;
        }

        var artifact = new BuilderPatternLibraryMatchesRecord(
            workspaceId,
            MatchesSchemaVersion,
            contextSnapshot,
            matches,
            resolvedAttachedEntryId,
            resolvedAttachedMatchId,
            true,
            BuildMatchesSummary(workspaceId, matches, resolvedAttachedEntryId),
            PatternLibraryMatchesPathForRepo(repoRoot),
            effectiveObservedUtc);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    public static BuilderPatternLibraryMatchesRecord? AttachPatternReference(
        string repoRoot,
        string patternEntryId,
        string patternMatchId = "",
        DateTimeOffset? observedUtc = null)
    {
        var entries = LoadPatternLibraryEntries(repoRoot);
        if (entries is null || entries.Entries.Count == 0)
        {
            return null;
        }

        return RefreshPatternLibraryMatches(
            repoRoot,
            entries,
            attachedPatternEntryId: patternEntryId,
            attachedPatternMatchId: patternMatchId,
            observedUtc: observedUtc);
    }

    private static BuilderPatternLibraryEntriesRecord SaveApprovedEntries(
        string repoRoot,
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord? vendorCandidate,
        BuilderExternalProvenanceEntryRecord? externalProvenance,
        EligibilityEvaluation eligibility,
        DateTimeOffset observedUtc)
    {
        var existing = LoadPatternLibraryEntries(repoRoot) ?? CreateDefaultEntriesRecord(repoRoot, observedUtc);
        var extractedEntries = ExtractPatternEntries(repoRoot, snapshot, evaluation, vendorCandidate, externalProvenance, eligibility, observedUtc);
        if (extractedEntries.Count == 0)
        {
            return existing;
        }

        var merged = MergeById(existing.Entries, extractedEntries);
        var updated = existing with
        {
            Entries = merged,
            Summary = BuildEntriesSummary(existing.LibraryId, merged),
            ObservedUtc = observedUtc
        };
        EnsureRoots(repoRoot);
        Save(updated.ArtifactPath, updated);
        SaveDerivedArtifacts(repoRoot, updated, observedUtc);
        RefreshPatternLibraryMatches(repoRoot, updated, observedUtc: observedUtc);
        return updated;
    }

    private static void SaveDerivedArtifacts(
        string repoRoot,
        BuilderPatternLibraryEntriesRecord entries,
        DateTimeOffset observedUtc)
    {
        var index = new BuilderPatternLibraryIndexRecord(
            entries.LibraryId,
            IndexSchemaVersion,
            entries.Entries
                .Select(entry => new BuilderPatternLibraryIndexEntryRecord(
                    entry.PatternEntryId,
                    entry.PatternName,
                    entry.PatternType,
                    entry.LanguageSet,
                    entry.SourceOrigin,
                    entry.SourceSnapshotId,
                    entry.ApprovedUsageClass,
                    entry.LicenseStatus,
                    entry.ArtifactLinks,
                    entry.PatternSummary,
                    entry.ObservedUtc))
                .OrderBy(entry => PatternTypeRank(entry.PatternType))
                .ThenBy(entry => entry.PatternName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            true,
            BuildIndexSummary(entries.LibraryId, entries.Entries),
            PatternLibraryIndexPathForRepo(repoRoot),
            observedUtc);
        Save(index.ArtifactPath, index);

        var provenance = new BuilderPatternLibraryProvenanceRecord(
            entries.LibraryId,
            ProvenanceSchemaVersion,
            entries.Entries
                .Select(entry => BuildProvenanceEntry(repoRoot, entry, observedUtc))
                .OrderBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ProvenanceId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            true,
            BuildProvenanceSummary(entries.LibraryId, entries.Entries),
            PatternLibraryProvenancePathForRepo(repoRoot),
            observedUtc);
        Save(provenance.ArtifactPath, provenance);
    }

    private static BuilderPatternLibraryProvenanceEntryRecord BuildProvenanceEntry(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        DateTimeOffset observedUtc)
    {
        var external = BuilderExternalReconService.LoadExternalProvenanceIndex(repoRoot)?.Entries.FirstOrDefault(item =>
            string.Equals(item.SnapshotId, entry.SourceSnapshotId, StringComparison.OrdinalIgnoreCase));
        return new BuilderPatternLibraryProvenanceEntryRecord(
            ComputeDeterministicId("pattern_provenance", entry.PatternEntryId, entry.SourceSnapshotId),
            entry.PatternEntryId,
            external?.OriginalUrl ?? entry.SourceOrigin,
            external?.CanonicalSourceId ?? entry.SourceOrigin,
            external?.ResolvedCommitOrContentHash ?? entry.SourceSnapshotId,
            entry.SourceSnapshotId,
            external?.EvaluationId ?? string.Empty,
            entry.VendorCandidateId,
            external?.LicenseMetadata ?? string.Empty,
            entry.LicenseStatus,
            entry.Eligibility.ReviewStatus,
            entry.Eligibility.ApprovalStatus,
            entry.ApprovedUsageClass,
            entry.ArtifactLinks,
            $"Pattern {entry.PatternEntryId} stays pinned to snapshot {entry.SourceSnapshotId} with usage class {entry.ApprovedUsageClass.Replace('_', ' ')}.",
            observedUtc);
    }

    private static List<BuilderPatternLibraryEntryRecord> ExtractPatternEntries(
        string repoRoot,
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord? vendorCandidate,
        BuilderExternalProvenanceEntryRecord? externalProvenance,
        EligibilityEvaluation eligibility,
        DateTimeOffset observedUtc)
    {
        var selectedPaths = (vendorCandidate?.SelectedPaths ?? snapshot.IncludedPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0 || !Directory.Exists(snapshot.SnapshotRoot))
        {
            return new List<BuilderPatternLibraryEntryRecord>();
        }

        var sourceFiles = selectedPaths
            .Select(path => new
            {
                RelativePath = path,
                FullPath = Path.Combine(snapshot.SnapshotRoot, path.Replace('/', Path.DirectorySeparatorChar))
            })
            .Where(item => File.Exists(item.FullPath))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            return new List<BuilderPatternLibraryEntryRecord>();
        }

        var languages = sourceFiles
            .Select(item => Path.GetExtension(item.RelativePath))
            .Where(LanguageByExtension.ContainsKey)
            .Select(extension => LanguageByExtension[extension])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var buildMarkers = sourceFiles
            .SelectMany(item => ResolveBuildMarkers(Path.GetFileName(item.RelativePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(marker => marker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dependencyMarkers = sourceFiles
            .SelectMany(item => ResolveDependencyMarkers(Path.GetFileName(item.RelativePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(marker => marker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rootDirectories = sourceFiles
            .Select(item => item.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? item.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var previewTexts = sourceFiles
            .Take(20)
            .Select(item => new KeyValuePair<string, string>(item.RelativePath, ReadSmallText(item.FullPath)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var hasTests = sourceFiles.Any(item => IsTestPath(item.RelativePath));
        var hasXaml = sourceFiles.Any(item => item.RelativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
        var hasViewModel = sourceFiles.Any(item =>
            item.RelativePath.Contains("viewmodel", StringComparison.OrdinalIgnoreCase) ||
            item.RelativePath.Contains("viewmodels/", StringComparison.OrdinalIgnoreCase));
        var hasServiceWiring = sourceFiles.Any(item =>
                                    item.RelativePath.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                                    item.RelativePath.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                                    item.RelativePath.Contains("dependencyinjection", StringComparison.OrdinalIgnoreCase)) ||
                               previewTexts.Values.Any(text => ContainsAny(text, "IServiceCollection", "AddSingleton", "AddScoped", "AddTransient", "builder.Services"));
        var hasArtifactGeneration = sourceFiles.Any(item =>
                                        item.RelativePath.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
                                        item.RelativePath.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
                                        item.RelativePath.Contains("report", StringComparison.OrdinalIgnoreCase)) ||
                                    previewTexts.Values.Any(text => ContainsAny(text, "WriteIndented", "ArtifactPath", "JsonSerializer.Serialize"));
        var hasDeterministicSerialization = previewTexts.Values.Any(text =>
            ContainsAny(text, "JsonSerializer", "JsonSerializerOptions", "WriteIndented", "PropertyNamingPolicy", "OrderBy(", "StringComparer.OrdinalIgnoreCase"));
        var hasReviewWorkflow = sourceFiles.Any(item =>
                                     item.RelativePath.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                                     item.RelativePath.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
                                     item.RelativePath.Contains("rejection", StringComparison.OrdinalIgnoreCase) ||
                                     item.RelativePath.Contains("finalize", StringComparison.OrdinalIgnoreCase) ||
                                     item.RelativePath.Contains("queue", StringComparison.OrdinalIgnoreCase)) ||
                                 previewTexts.Values.Any(text =>
                                     ContainsAny(text, "approve", "rejected", "revision", "finalize", "queue", "review"));
        var usageClass = DetermineApprovedUsageClass(snapshot, evaluation, vendorCandidate);
        var commonArtifactLinks = BuildArtifactLinks(
            snapshot.ArtifactLinks,
            evaluation?.ArtifactLinks,
            vendorCandidate?.ArtifactLinks,
            new[] { PatternLibraryEntriesPathForRepo(repoRoot), PatternLibraryIndexPathForRepo(repoRoot), PatternLibraryProvenancePathForRepo(repoRoot) });

        var candidates = new List<PatternExtractionCandidate>();
        if (rootDirectories.Length > 1 || sourceFiles.Length >= 4)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeProjectStructure,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path => true, 6),
                new[] { "multi_directory_layout", "approved_snapshot" }));
        }

        if (buildMarkers.Length > 0 || hasTests)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeBuildTestPipeline,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), IsBuildOrTestPath, 6),
                buildMarkers.Concat(hasTests ? new[] { "has_tests" } : Array.Empty<string>()).ToArray()));
        }

        if (hasXaml || hasViewModel)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeUiViewModel,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path =>
                    path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("viewmodel", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("viewmodels/", StringComparison.OrdinalIgnoreCase), 6),
                new[]
                {
                    hasXaml ? "xaml_surface" : string.Empty,
                    hasViewModel ? "view_model_surface" : string.Empty
                }.Where(marker => !string.IsNullOrWhiteSpace(marker)).ToArray()));
        }

        if (hasServiceWiring)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeServiceWiring,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path =>
                    path.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("dependencyinjection", StringComparison.OrdinalIgnoreCase), 6),
                new[] { "service_registration", "entry_point_wiring" }));
        }

        if (hasArtifactGeneration)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeArtifactGeneration,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path =>
                    path.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("report", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase), 6),
                new[] { "artifact_output", "deterministic_reports" }));
        }

        if (hasDeterministicSerialization)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeDeterministicSerialization,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path =>
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase), 6),
                new[] { "json_serializer", "stable_ordering" }));
        }

        if (hasReviewWorkflow)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeReviewApprovalWorkflow,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path =>
                    path.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("finalize", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("queue", StringComparison.OrdinalIgnoreCase), 6),
                new[] { "review_workflow", "approval_gate" }));
        }

        if (sourceFiles.Length <= 4)
        {
            candidates.Add(new PatternExtractionCandidate(
                PatternTypeHelperUtilitySnippet,
                SelectKeyPaths(sourceFiles.Select(item => item.RelativePath), path => true, 4),
                new[] { "focused_scope", "small_surface" }));
        }

        return candidates
            .OrderBy(candidate => PatternTypeRank(candidate.PatternType))
            .ThenBy(candidate => string.Join("|", candidate.KeyPaths), StringComparer.OrdinalIgnoreCase)
            .Select(candidate => BuildEntry(
                snapshot,
                evaluation,
                vendorCandidate,
                externalProvenance,
                eligibility,
                languages,
                buildMarkers,
                dependencyMarkers,
                usageClass,
                commonArtifactLinks,
                candidate,
                observedUtc))
            .ToList();
    }

    private static BuilderPatternLibraryEntryRecord BuildEntry(
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord? vendorCandidate,
        BuilderExternalProvenanceEntryRecord? externalProvenance,
        EligibilityEvaluation eligibility,
        IReadOnlyList<string> languages,
        IReadOnlyList<string> buildMarkers,
        IReadOnlyList<string> dependencyMarkers,
        string usageClass,
        IReadOnlyList<string> commonArtifactLinks,
        PatternExtractionCandidate candidate,
        DateTimeOffset observedUtc)
    {
        var entryId = ComputeDeterministicId(
            "pattern_entry",
            snapshot.SnapshotId,
            vendorCandidate?.CandidateId ?? string.Empty,
            candidate.PatternType,
            usageClass,
            string.Join("|", candidate.KeyPaths));
        var patternName = $"{ResolvePatternSourceName(snapshot, vendorCandidate)} {GetPatternTypeLabel(candidate.PatternType)}";
        var eligibilityRecord = new BuilderPatternLibraryEligibilityRecord(
            ComputeDeterministicId("entry_candidate", snapshot.SnapshotId, vendorCandidate?.CandidateId ?? string.Empty, candidate.PatternType),
            snapshot.SnapshotId,
            vendorCandidate?.CandidateId ?? string.Empty,
            snapshot.LicenseStatus,
            eligibility.ReviewStatus,
            eligibility.ApprovalStatus,
            eligibility.EntryEligibility,
            eligibility.EntryEligibilityReason);
        var structuralMarkers = candidate.StructuralMarkers
            .Concat(new[] { snapshot.SnapshotScope, snapshot.SourceKind, eligibility.EntryEligibility })
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(marker => marker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var qualityScore = evaluation?.QualityScore ?? 50d;
        var riskScore = evaluation?.RiskScore ?? 50d;

        return new BuilderPatternLibraryEntryRecord(
            entryId,
            eligibilityRecord,
            patternName,
            candidate.PatternType,
            languages,
            externalProvenance?.CanonicalSourceId ?? snapshot.ResolvedSourceId,
            snapshot.SnapshotId,
            vendorCandidate?.CandidateId ?? string.Empty,
            vendorCandidate?.CandidateScope ?? snapshot.SnapshotScope,
            candidate.KeyPaths,
            $"Approved {GetPatternTypeLabel(candidate.PatternType)} entry from snapshot {snapshot.SnapshotId} with {candidate.KeyPaths.Count} key path(s) and usage class {GetUsageClassLabel(usageClass)}.",
            structuralMarkers,
            dependencyMarkers,
            buildMarkers,
            Math.Round(qualityScore, 2),
            Math.Round(riskScore, 2),
            snapshot.LicenseStatus,
            usageClass,
            commonArtifactLinks,
            observedUtc);
    }

    private static BuilderPatternLibraryContextSnapshotRecord BuildContextSnapshot(
        string workspaceId,
        BuilderWorkspaceCapabilitiesRecord? capabilities,
        BuilderRecoveryPlaybooksRecord? recovery,
        BuilderOperatorIntentRecord? intent,
        BuilderOperatorConstraintsRecord? constraints)
    {
        var languages = (capabilities?.LanguagesDetected ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var buildSystems = (capabilities?.BuildSystems ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(system => system, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failureClasses = (recovery?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
            .Select(entry => entry.FailureClass)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var constraintTypes = (constraints?.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, constraints.ActiveProfileId, StringComparison.OrdinalIgnoreCase))?.Constraints ?? Array.Empty<BuilderOperatorConstraintRecord>())
            .Select(entry => entry.ConstraintType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderPatternLibraryContextSnapshotRecord(
            workspaceId,
            languages,
            buildSystems,
            failureClasses,
            intent?.Intent ?? string.Empty,
            constraints?.ActiveProfileId ?? string.Empty,
            constraintTypes,
            $"Workspace {workspaceId} is matching approved patterns across {DescribeList(languages)} with build systems {DescribeList(buildSystems)} and recovery context {DescribeList(failureClasses)}.");
    }

    private static BuilderPatternLibraryMatchRecord BuildMatch(
        string repoRoot,
        string workspaceId,
        BuilderPatternLibraryEntryRecord entry,
        BuilderPatternLibraryContextSnapshotRecord context)
    {
        var reasons = new List<string>();
        var score = 0d;

        var languageOverlap = entry.LanguageSet
            .Intersect(context.LanguagesDetected, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (languageOverlap.Length > 0)
        {
            score += 38d;
            reasons.Add($"Language overlap: {string.Join(", ", languageOverlap)}.");
        }
        else if (entry.LanguageSet.Count == 0)
        {
            score += 8d;
            reasons.Add("Pattern is structural and does not depend on a dominant language match.");
        }

        var buildOverlap = entry.BuildMarkers
            .Intersect(context.BuildSystems, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (buildOverlap.Length > 0)
        {
            score += 18d;
            reasons.Add($"Build overlap: {string.Join(", ", buildOverlap)}.");
        }

        if (IsFailureContextRelevant(entry.PatternType, context.FailureClasses))
        {
            score += 16d;
            reasons.Add($"Current recovery context lines up with {GetPatternTypeLabel(entry.PatternType)}.");
        }

        if (IsIntentRelevant(entry.PatternType, context.Intent))
        {
            score += 10d;
            reasons.Add($"Intent {BuilderOperatorIntentService.GetIntentLabel(context.Intent)} favors this pattern shape.");
        }

        var usageWeight = entry.ApprovedUsageClass switch
        {
            UsageClassVendorableWithReview => 14d,
            UsageClassSnippetCandidate => 12d,
            UsageClassStructuralInspirationOnly => 8d,
            UsageClassReferenceOnly => 6d,
            _ => 0d
        };
        score += usageWeight;
        if (usageWeight > 0d)
        {
            reasons.Add($"Approved usage class: {GetUsageClassLabel(entry.ApprovedUsageClass)}.");
        }

        score += Math.Max(0d, Math.Min(12d, entry.QualityScore / 10d));
        score -= Math.Max(0d, Math.Min(22d, entry.RiskScore / 5d));
        if (!string.Equals(entry.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase))
        {
            score -= string.Equals(entry.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase) ? 35d : 12d;
            reasons.Add($"License state: {entry.LicenseStatus.Replace('_', ' ')}.");
        }
        else
        {
            score += 8d;
            reasons.Add("License state is clear.");
        }

        var fitClassification = ResolveFitClassification(entry, score);
        return new BuilderPatternLibraryMatchRecord(
            ComputeDeterministicId("pattern_match", workspaceId, entry.PatternEntryId, fitClassification),
            workspaceId,
            entry.PatternEntryId,
            ClampScore(score),
            reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            fitClassification,
            entry.ApprovedUsageClass,
            entry.LicenseStatus,
            BuildArtifactLinks(entry.ArtifactLinks, new[] { PatternLibraryMatchesPathForRepo(repoRoot) }),
            $"Pattern {entry.PatternName} scored {ClampScore(score):0.##} for workspace {workspaceId} as {fitClassification.Replace('_', ' ')}.");
    }

    private static string ResolveFitClassification(BuilderPatternLibraryEntryRecord entry, double score)
    {
        if (string.Equals(entry.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.ApprovedUsageClass, UsageClassRestrictedDoNotReuse, StringComparison.OrdinalIgnoreCase))
        {
            return "license_blocked";
        }

        if (!string.Equals(entry.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.ApprovedUsageClass, UsageClassReferenceOnly, StringComparison.OrdinalIgnoreCase))
        {
            return "manual_review_required";
        }

        if (score >= 75d && string.Equals(entry.ApprovedUsageClass, UsageClassVendorableWithReview, StringComparison.OrdinalIgnoreCase))
        {
            return "high_fit_vendor_candidate";
        }

        if (score >= 65d)
        {
            return "high_fit_reference";
        }

        if (score >= 45d)
        {
            return "structure_only";
        }

        return "manual_review_required";
    }

    private static EligibilityEvaluation EvaluateEligibility(
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord? vendorCandidate,
        string explicitApprovalSource)
    {
        var isPinned = !string.IsNullOrWhiteSpace(snapshot.ResolvedCommitOrContentHash) &&
                       !string.IsNullOrWhiteSpace(snapshot.ContentHash);
        if (!isPinned)
        {
            return new EligibilityEvaluation(false, "ineligible_unpinned_source", "Source is not pinned to a deterministic commit or content hash.", "not_reviewed", "not_approved");
        }

        if (evaluation is null)
        {
            return new EligibilityEvaluation(false, "ineligible_unevaluated_source", "Source needs an external code evaluation before it can enter the approved pattern library.", "not_reviewed", "not_approved");
        }

        if (vendorCandidate is null &&
            !string.Equals(evaluation.RecommendedUsage, "reference_only", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evaluation.RecommendedUsage, "snippet_candidate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evaluation.RecommendedUsage, "vendor_candidate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evaluation.RecommendedUsage, "manual_review_required", StringComparison.OrdinalIgnoreCase))
        {
            return new EligibilityEvaluation(false, "ineligible_usage_class", $"Evaluation usage {evaluation.RecommendedUsage.Replace('_', ' ')} does not support approved pattern indexing.", "not_reviewed", "not_approved");
        }

        if (string.Equals(snapshot.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase) &&
            vendorCandidate is not null)
        {
            return new EligibilityEvaluation(false, "ineligible_restricted_vendor_candidate", "Restricted license sources stay outside vendor-backed reusable pattern entries.", "not_reviewed", "not_approved");
        }

        if (string.Equals(snapshot.LicenseStatus, "license_unknown", StringComparison.OrdinalIgnoreCase) &&
            vendorCandidate is not null)
        {
            return new EligibilityEvaluation(false, "ineligible_unknown_vendor_candidate", "Vendor-backed pattern entries need a known or clear license state.", "not_reviewed", "not_approved");
        }

        var eligibility = vendorCandidate is not null
            ? "eligible_vendor_candidate"
            : string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase)
                ? "eligible_snapshot_reference"
                : "eligible_reference_only";
        var reason = vendorCandidate is not null
            ? $"Vendor candidate approval via {explicitApprovalSource.Replace('_', ' ')} keeps the source pinned to snapshot {snapshot.SnapshotId}."
            : $"Snapshot approval via {explicitApprovalSource.Replace('_', ' ')} indexes the source as advisory reference evidence.";
        return new EligibilityEvaluation(true, eligibility, reason, "operator_reviewed", "approved");
    }

    private static BuilderExternalCodeEvaluationRecord? LoadLatestEvaluationForSnapshot(string repoRoot, string snapshotId)
        => (BuilderExternalReconService.LoadExternalCodeEvaluations(repoRoot)?.Evaluations ?? Array.Empty<BuilderExternalCodeEvaluationRecord>())
            .Where(entry => string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.EvaluationId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static BuilderExternalProvenanceEntryRecord? LoadLatestProvenanceForSnapshot(string repoRoot, string snapshotId)
        => (BuilderExternalReconService.LoadExternalProvenanceIndex(repoRoot)?.Entries ?? Array.Empty<BuilderExternalProvenanceEntryRecord>())
            .Where(entry => string.Equals(entry.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.ProvenanceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string DetermineApprovedUsageClass(
        BuilderExternalSourceSnapshotRecord snapshot,
        BuilderExternalCodeEvaluationRecord? evaluation,
        BuilderVendorCandidateRecord? vendorCandidate)
    {
        if (string.Equals(snapshot.LicenseStatus, "license_restricted", StringComparison.OrdinalIgnoreCase))
        {
            return UsageClassRestrictedDoNotReuse;
        }

        if (vendorCandidate is not null && !vendorCandidate.ReviewRequired && string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase))
        {
            return UsageClassVendorableWithReview;
        }

        if (evaluation is null)
        {
            return UsageClassReferenceOnly;
        }

        if (string.Equals(evaluation.RecommendedUsage, "vendor_candidate", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase))
        {
            return UsageClassVendorableWithReview;
        }

        if (string.Equals(evaluation.RecommendedUsage, "snippet_candidate", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.LicenseStatus, "license_clear", StringComparison.OrdinalIgnoreCase))
        {
            return UsageClassSnippetCandidate;
        }

        if (string.Equals(snapshot.LicenseStatus, "license_unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.LicenseStatus, "manual_license_review_required", StringComparison.OrdinalIgnoreCase))
        {
            return UsageClassReferenceOnly;
        }

        return string.Equals(evaluation.RecommendedUsage, "manual_review_required", StringComparison.OrdinalIgnoreCase)
            ? UsageClassStructuralInspirationOnly
            : UsageClassReferenceOnly;
    }

    private static BuilderPatternLibraryEntriesRecord CreateDefaultEntriesRecord(string repoRoot, DateTimeOffset observedUtc)
        => new(
            ResolveLibraryId(repoRoot),
            EntriesSchemaVersion,
            Array.Empty<BuilderPatternLibraryEntryRecord>(),
            true,
            $"No approved pattern library entries recorded for {ResolveLibraryId(repoRoot)}.",
            PatternLibraryEntriesPathForRepo(repoRoot),
            observedUtc);

    private static string ResolveLibraryId(string repoRoot)
        => $"pattern-library::{BuilderWorkspaceService.ResolveWorkspaceId(repoRoot)}";

    private static string ResolvePatternSourceName(BuilderExternalSourceSnapshotRecord snapshot, BuilderVendorCandidateRecord? vendorCandidate)
    {
        var sourceName = Path.GetFileNameWithoutExtension(snapshot.ResolvedSourceId);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = Path.GetFileNameWithoutExtension(snapshot.SourceUrl);
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "approved-source";
        }

        return vendorCandidate is null ? sourceName : $"{sourceName} Vendor";
    }

    private static string[] SelectKeyPaths(IEnumerable<string> paths, Func<string, bool> predicate, int limit)
    {
        var selected = paths
            .Where(path => predicate(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        return selected.Length == 0
            ? paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : selected;
    }

    private static bool IsBuildOrTestPath(string path)
        => IsTestPath(path) ||
           ResolveBuildMarkers(Path.GetFileName(path)).Length > 0 ||
           ResolveDependencyMarkers(Path.GetFileName(path)).Length > 0;

    private static bool IsTestPath(string path)
        => path.Contains("/test", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("/tests", StringComparison.OrdinalIgnoreCase) ||
           path.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailureContextRelevant(string patternType, IReadOnlyList<string> failureClasses)
        => NormalizeToken(patternType) switch
        {
            PatternTypeReviewApprovalWorkflow => failureClasses.Any(value => ContainsAny(value, "patch_rejected", "review_blocked", "finalize_blocked")),
            PatternTypeBuildTestPipeline => failureClasses.Any(value => ContainsAny(value, "route_failed", "repeated_failure_pattern")),
            PatternTypeProjectStructure => failureClasses.Count > 0,
            PatternTypeArtifactGeneration => failureClasses.Any(value => ContainsAny(value, "route_failed", "orchestration_blocked", "repeated_failure_pattern")),
            PatternTypeDeterministicSerialization => failureClasses.Any(value => ContainsAny(value, "route_failed", "finalize_blocked")),
            _ => false
        };

    private static bool IsIntentRelevant(string patternType, string intent)
        => NormalizeToken(intent) switch
        {
            BuilderOperatorIntentService.SafeRecoveryIntent => patternType is PatternTypeReviewApprovalWorkflow or PatternTypeBuildTestPipeline,
            BuilderOperatorIntentService.FastRecoveryIntent => patternType is PatternTypeHelperUtilitySnippet or PatternTypeServiceWiring,
            BuilderOperatorIntentService.MinimalChangeIntent => patternType is PatternTypeHelperUtilitySnippet or PatternTypeProjectStructure,
            BuilderOperatorIntentService.FullResolutionIntent => patternType is PatternTypeServiceWiring or PatternTypeArtifactGeneration or PatternTypeDeterministicSerialization,
            BuilderOperatorIntentService.UnblockOrchestrationIntent => patternType is PatternTypeProjectStructure or PatternTypeBuildTestPipeline,
            _ => false
        };

    private static string BuildEntriesSummary(string libraryId, IReadOnlyList<BuilderPatternLibraryEntryRecord> entries)
        => entries.Count == 0
            ? $"No approved pattern library entries recorded for {libraryId}."
            : $"Recorded {entries.Count} approved pattern entr{(entries.Count == 1 ? "y" : "ies")} for {libraryId}.";

    private static string BuildIndexSummary(string libraryId, IReadOnlyList<BuilderPatternLibraryEntryRecord> entries)
        => entries.Count == 0
            ? $"No approved pattern catalog entries recorded for {libraryId}."
            : $"Indexed {entries.Count} approved pattern entr{(entries.Count == 1 ? "y" : "ies")} for {libraryId}.";

    private static string BuildProvenanceSummary(string libraryId, IReadOnlyList<BuilderPatternLibraryEntryRecord> entries)
        => entries.Count == 0
            ? $"No approved pattern provenance entries recorded for {libraryId}."
            : $"Tracked provenance for {entries.Count} approved pattern entr{(entries.Count == 1 ? "y" : "ies")} in {libraryId}.";

    private static string BuildMatchesSummary(string workspaceId, IReadOnlyList<BuilderPatternLibraryMatchRecord> matches, string attachedPatternEntryId)
    {
        if (matches.Count == 0)
        {
            return $"No approved pattern matches are currently recorded for {workspaceId}.";
        }

        var topMatch = matches[0];
        var attachment = string.IsNullOrWhiteSpace(attachedPatternEntryId)
            ? "No pattern reference is currently attached."
            : $"Attached pattern reference: {attachedPatternEntryId}.";
        return $"Generated {matches.Count} approved pattern match(es) for {workspaceId}. Top fit: {topMatch.FitClassification.Replace('_', ' ')} at {topMatch.MatchScore:0.##}. {attachment}";
    }

    private static IReadOnlyList<BuilderPatternLibraryEntryRecord> MergeById(
        IReadOnlyList<BuilderPatternLibraryEntryRecord> existing,
        IReadOnlyList<BuilderPatternLibraryEntryRecord> additions)
        => existing
            .Concat(additions)
            .GroupBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.ObservedUtc).ThenBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase).Last())
            .OrderBy(entry => PatternTypeRank(entry.PatternType))
            .ThenBy(entry => entry.PatternName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int PatternTypeRank(string patternType)
        => NormalizeToken(patternType) switch
        {
            PatternTypeProjectStructure => 0,
            PatternTypeBuildTestPipeline => 1,
            PatternTypeServiceWiring => 2,
            PatternTypeUiViewModel => 3,
            PatternTypeArtifactGeneration => 4,
            PatternTypeDeterministicSerialization => 5,
            PatternTypeReviewApprovalWorkflow => 6,
            PatternTypeHelperUtilitySnippet => 7,
            _ => 8
        };

    private static int FitClassificationRank(string fitClassification)
        => NormalizeToken(fitClassification) switch
        {
            "high_fit_vendor_candidate" => 0,
            "high_fit_reference" => 1,
            "structure_only" => 2,
            "manual_review_required" => 3,
            "license_blocked" => 4,
            _ => 5
        };

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

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

    private static double ClampScore(double value)
        => Math.Max(0d, Math.Min(100d, Math.Round(value, 2)));

    private static string DescribeList(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    private static IReadOnlyList<string> BuildArtifactLinks(params IEnumerable<string>?[] groups)
        => groups
            .Where(group => group is not null)
            .SelectMany(group => group!)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ComputeDeterministicId(params string[] parts)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()[..16];
    }

    private static void EnsureRoots(string repoRoot)
    {
        Directory.CreateDirectory(PatternLibraryRootForRepo(repoRoot));
        Directory.CreateDirectory(PatternLibraryMatchesRootForRepo(repoRoot));
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

    private sealed record EligibilityEvaluation(
        bool IsEligible,
        string EntryEligibility,
        string EntryEligibilityReason,
        string ReviewStatus,
        string ApprovalStatus);

    private sealed record PatternExtractionCandidate(
        string PatternType,
        IReadOnlyList<string> KeyPaths,
        IReadOnlyList<string> StructuralMarkers);
}
