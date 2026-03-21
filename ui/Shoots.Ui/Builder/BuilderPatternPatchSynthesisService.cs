using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shoots.UI.Builder;

public sealed record BuilderPatternPatchCandidateRecord(
    string PatchCandidateId,
    string WorkspaceId,
    string PatternEntryId,
    string PatternMatchId,
    string SourceSnapshotId,
    string TargetRepoId,
    IReadOnlyList<string> TargetPaths,
    string SynthesisType,
    string SynthesisEligibility,
    string EligibilityReason,
    bool BlockedByLicense,
    bool BlockedByReviewState,
    bool BlockedByUsageClass,
    string DiffText,
    string ConfidenceClass,
    string RiskLevel,
    string LicenseStatus,
    string ApprovedUsageClass,
    string TargetAnchorType,
    string TargetAnchorValue,
    string TargetResolutionReason,
    string AnchorConfidence,
    string StagedReviewSessionId,
    bool AdvisoryOnly,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchesRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternPatchCandidateRecord> Candidates,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchExplanationRecord(
    string ExplanationId,
    string PatchCandidateId,
    string Summary,
    IReadOnlyList<string> TransformationSteps,
    IReadOnlyList<string> SourceElements,
    IReadOnlyList<string> TargetElements,
    IReadOnlyList<string> MappingRules,
    string WhyThisPatternApplies,
    string WhyThisTargetWasChosen,
    IReadOnlyList<string> ArtifactLinks,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchExplanationsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternPatchExplanationRecord> Explanations,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchProvenanceEntryRecord(
    string PatchProvenanceId,
    string PatchCandidateId,
    string PatternEntryId,
    string PatternMatchId,
    string SourceSnapshotId,
    string OriginalUrl,
    string CanonicalSourceId,
    string ResolvedCommitOrContentHash,
    string LicenseStatus,
    string ApprovedUsageClass,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchProvenanceRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternPatchProvenanceEntryRecord> Entries,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchMatchRecord(
    string PatchCandidateId,
    string PatternEntryId,
    string PatternMatchId,
    string WorkspaceId,
    string TargetPath,
    string SynthesisType,
    string SynthesisEligibility,
    string TargetAnchorType,
    string TargetAnchorValue,
    string TargetResolutionReason,
    string AnchorConfidence,
    string MatchClassification,
    IReadOnlyList<string> ArtifactLinks,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchMatchesRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPatternPatchMatchRecord> Matches,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatternPatchSynthesisContext(
    BuilderPatternPatchesRecord Patches,
    BuilderPatternPatchExplanationsRecord Explanations,
    BuilderPatternPatchProvenanceRecord Provenance,
    BuilderPatternPatchMatchesRecord Matches);

public static class BuilderPatternPatchSynthesisService
{
    public const string PatternPatchesFileName = "builder_pattern_patches.json";
    public const string PatternPatchExplanationsFileName = "builder_pattern_patch_explanations.json";
    public const string PatternPatchProvenanceFileName = "builder_pattern_patch_provenance.json";
    public const string PatternPatchMatchesFileName = "builder_pattern_patch_matches.json";

    public const string SynthesisTypeFileCreate = "file_create";
    public const string SynthesisTypeFileModify = "file_modify";
    public const string SynthesisTypeStructuralInsert = "structural_insert";
    public const string SynthesisTypeConfigUpdate = "config_update";
    public const string SynthesisTypeServiceRegistration = "service_registration";
    public const string SynthesisTypeViewModelWiring = "view_model_wiring";
    public const string SynthesisTypeArtifactWriterAddition = "artifact_writer_addition";
    public const string SynthesisTypeTestScaffoldAddition = "test_scaffold_addition";

    private const string PatchesSchemaVersion = "builder_pattern_patches.v1";
    private const string ExplanationsSchemaVersion = "builder_pattern_patch_explanations.v1";
    private const string ProvenanceSchemaVersion = "builder_pattern_patch_provenance.v1";
    private const string MatchesSchemaVersion = "builder_pattern_patch_matches.v1";
    private const string LicenseClear = "license_clear";
    private const string EligibilityReady = "ready_for_synthesis";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
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

    public static string PatternPatchRootForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), "pattern-patches");

    public static string PatternPatchesPathForRepo(string repoRoot)
        => Path.Combine(PatternPatchRootForRepo(repoRoot), PatternPatchesFileName);

    public static string PatternPatchExplanationsPathForRepo(string repoRoot)
        => Path.Combine(PatternPatchRootForRepo(repoRoot), PatternPatchExplanationsFileName);

    public static string PatternPatchProvenancePathForRepo(string repoRoot)
        => Path.Combine(PatternPatchRootForRepo(repoRoot), PatternPatchProvenanceFileName);

    public static string PatternPatchMatchesPathForRepo(string repoRoot)
        => Path.Combine(PatternPatchRootForRepo(repoRoot), PatternPatchMatchesFileName);

    public static BuilderPatternPatchesRecord? LoadPatternPatches(string repoRoot)
        => Load<BuilderPatternPatchesRecord>(PatternPatchesPathForRepo(repoRoot));

    public static BuilderPatternPatchExplanationsRecord? LoadPatternPatchExplanations(string repoRoot)
        => Load<BuilderPatternPatchExplanationsRecord>(PatternPatchExplanationsPathForRepo(repoRoot));

    public static BuilderPatternPatchProvenanceRecord? LoadPatternPatchProvenance(string repoRoot)
        => Load<BuilderPatternPatchProvenanceRecord>(PatternPatchProvenancePathForRepo(repoRoot));

    public static BuilderPatternPatchMatchesRecord? LoadPatternPatchMatches(string repoRoot)
        => Load<BuilderPatternPatchMatchesRecord>(PatternPatchMatchesPathForRepo(repoRoot));

    public static BuilderPatternPatchSynthesisContext? LoadPatternPatchContext(string repoRoot)
    {
        var patches = LoadPatternPatches(repoRoot);
        var explanations = LoadPatternPatchExplanations(repoRoot);
        var provenance = LoadPatternPatchProvenance(repoRoot);
        var matches = LoadPatternPatchMatches(repoRoot);
        return patches is null || explanations is null || provenance is null || matches is null
            ? null
            : new BuilderPatternPatchSynthesisContext(patches, explanations, provenance, matches);
    }

    public static BuilderPatternPatchSynthesisContext? RefreshPatternPatchArtifacts(
        string repoRoot,
        BuilderPatternLibraryEntriesRecord? entriesArtifact = null,
        BuilderPatternLibraryMatchesRecord? matchesArtifact = null,
        BuilderPatternLibraryProvenanceRecord? provenanceArtifact = null,
        BuilderExternalSourceSnapshotsRecord? snapshotsArtifact = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        entriesArtifact ??= BuilderPatternLibraryService.LoadPatternLibraryEntries(repoRoot);
        if (entriesArtifact is null || entriesArtifact.Entries.Count == 0)
        {
            return null;
        }

        matchesArtifact ??= BuilderPatternLibraryService.LoadPatternLibraryMatches(repoRoot)
                          ?? BuilderPatternLibraryService.RefreshPatternLibraryMatches(repoRoot, entriesArtifact);
        provenanceArtifact ??= BuilderPatternLibraryService.LoadPatternLibraryProvenance(repoRoot);
        snapshotsArtifact ??= BuilderExternalReconService.LoadExternalSourceSnapshots(repoRoot);
        if (matchesArtifact is null || provenanceArtifact is null || snapshotsArtifact is null)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var provenanceByPatternId = provenanceArtifact.Entries
            .GroupBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.ObservedUtc).ThenBy(entry => entry.ProvenanceId, StringComparer.OrdinalIgnoreCase).First(),
                StringComparer.OrdinalIgnoreCase);
        var snapshotById = snapshotsArtifact.Snapshots
            .GroupBy(entry => entry.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.ObservedUtc).ThenBy(entry => entry.SnapshotId, StringComparer.OrdinalIgnoreCase).First(),
                StringComparer.OrdinalIgnoreCase);
        var existingPatches = LoadPatternPatches(repoRoot);
        var stagedSessionByCandidate = (existingPatches?.Candidates ?? Array.Empty<BuilderPatternPatchCandidateRecord>())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.StagedReviewSessionId))
            .ToDictionary(candidate => candidate.PatchCandidateId, candidate => candidate.StagedReviewSessionId, StringComparer.OrdinalIgnoreCase);

        var materialized = new List<MaterializedPatternPatchCandidate>();
        foreach (var match in matchesArtifact.Matches
                     .OrderByDescending(entry => entry.MatchScore)
                     .ThenBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.MatchId, StringComparer.OrdinalIgnoreCase))
        {
            var entry = entriesArtifact.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.PatternEntryId, match.PatternEntryId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            provenanceByPatternId.TryGetValue(entry.PatternEntryId, out var provenance);
            snapshotById.TryGetValue(entry.SourceSnapshotId, out var snapshot);
            var allSnapshotFiles = snapshot is null ? Array.Empty<SnapshotFileRecord>() : EnumerateSnapshotFiles(snapshot).ToArray();
            var entrySnapshotFiles = ResolveSnapshotFilesForEntry(allSnapshotFiles, entry);

            foreach (var synthesisType in ResolvePlannedSynthesisTypes(entry))
            {
                materialized.Add(MaterializeCandidate(
                    repoRoot,
                    workspaceId,
                    entry,
                    match,
                    provenance,
                    snapshot,
                    entrySnapshotFiles,
                    synthesisType,
                    stagedSessionByCandidate,
                    effectiveObservedUtc));
            }
        }

        materialized = materialized
            .OrderBy(entry => EligibilityRank(entry.Candidate.SynthesisEligibility))
            .ThenBy(entry => SynthesisTypeRank(entry.Candidate.SynthesisType))
            .ThenBy(entry => entry.Candidate.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Candidate.TargetPaths.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Candidate.PatchCandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patches = new BuilderPatternPatchesRecord(
            workspaceId,
            PatchesSchemaVersion,
            materialized.Select(entry => entry.Candidate).ToArray(),
            true,
            BuildPatchSummary(workspaceId, materialized.Select(entry => entry.Candidate).ToArray()),
            PatternPatchesPathForRepo(repoRoot),
            effectiveObservedUtc);
        var explanations = new BuilderPatternPatchExplanationsRecord(
            workspaceId,
            ExplanationsSchemaVersion,
            materialized.Select(entry => entry.Explanation).ToArray(),
            true,
            BuildExplanationSummary(workspaceId, materialized.Count),
            PatternPatchExplanationsPathForRepo(repoRoot),
            effectiveObservedUtc);
        var provenanceArtifactRecord = new BuilderPatternPatchProvenanceRecord(
            workspaceId,
            ProvenanceSchemaVersion,
            materialized.Select(entry => entry.Provenance).ToArray(),
            true,
            BuildProvenanceSummary(workspaceId, materialized.Count),
            PatternPatchProvenancePathForRepo(repoRoot),
            effectiveObservedUtc);
        var matches = new BuilderPatternPatchMatchesRecord(
            workspaceId,
            MatchesSchemaVersion,
            materialized.Select(entry => entry.Match).ToArray(),
            true,
            BuildMatchSummary(workspaceId, materialized.Select(entry => entry.Match).ToArray()),
            PatternPatchMatchesPathForRepo(repoRoot),
            effectiveObservedUtc);

        Directory.CreateDirectory(PatternPatchRootForRepo(repoRoot));
        Save(patches.ArtifactPath, patches);
        Save(explanations.ArtifactPath, explanations);
        Save(provenanceArtifactRecord.ArtifactPath, provenanceArtifactRecord);
        Save(matches.ArtifactPath, matches);

        return new BuilderPatternPatchSynthesisContext(patches, explanations, provenanceArtifactRecord, matches);
    }

    public static BuilderReviewWorkspaceContext? StagePatchCandidateForReview(
        string repoRoot,
        string patchCandidateId,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchCandidateId);

        var context = LoadPatternPatchContext(repoRoot);
        if (context is null)
        {
            return null;
        }

        var candidate = context.Patches.Candidates.FirstOrDefault(entry =>
            string.Equals(entry.PatchCandidateId, patchCandidateId, StringComparison.OrdinalIgnoreCase));
        if (candidate is null ||
            !string.Equals(candidate.SynthesisEligibility, EligibilityReady, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(candidate.DiffText) ||
            candidate.TargetPaths.Count == 0)
        {
            return null;
        }

        var explanation = context.Explanations.Explanations.FirstOrDefault(entry =>
            string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase));
        var provenance = context.Provenance.Entries.FirstOrDefault(entry =>
            string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase));
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var reviewContext = WriteStagedReviewArtifacts(repoRoot, candidate, explanation, provenance, effectiveObservedUtc);
        if (reviewContext is null)
        {
            return null;
        }

        var updatedCandidates = context.Patches.Candidates
            .Select(entry => string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase)
                ? entry with { StagedReviewSessionId = $"pattern-patch-{candidate.PatchCandidateId}" }
                : entry)
            .ToArray();
        Save(
            context.Patches.ArtifactPath,
            context.Patches with
            {
                Candidates = updatedCandidates,
                Summary = BuildPatchSummary(context.Patches.WorkspaceId, updatedCandidates),
                ObservedUtc = effectiveObservedUtc
            });
        return reviewContext;
    }

    private static MaterializedPatternPatchCandidate MaterializeCandidate(
        string repoRoot,
        string workspaceId,
        BuilderPatternLibraryEntryRecord entry,
        BuilderPatternLibraryMatchRecord match,
        BuilderPatternLibraryProvenanceEntryRecord? provenance,
        BuilderExternalSourceSnapshotRecord? snapshot,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles,
        string synthesisType,
        IReadOnlyDictionary<string, string> stagedSessionByCandidate,
        DateTimeOffset observedUtc)
    {
        var eligibility = EvaluateSynthesisEligibility(entry, provenance, snapshot);
        var candidateId = ComputeDeterministicId("pattern_patch", workspaceId, entry.PatternEntryId, match.MatchId, synthesisType);
        var artifactLinks = BuildArtifactLinks(
            entry.ArtifactLinks,
            match.ArtifactLinks,
            provenance?.ArtifactLinks ?? Array.Empty<string>(),
            new[]
            {
                PatternPatchesPathForRepo(repoRoot),
                PatternPatchExplanationsPathForRepo(repoRoot),
                PatternPatchProvenancePathForRepo(repoRoot),
                PatternPatchMatchesPathForRepo(repoRoot)
            });

        if (!eligibility.IsEligible || snapshot is null || !Directory.Exists(snapshot.SnapshotRoot))
        {
            return BuildBlockedCandidate(
                workspaceId,
                entry,
                match,
                provenance,
                candidateId,
                synthesisType,
                eligibility,
                Array.Empty<string>(),
                "unresolved",
                string.Empty,
                string.IsNullOrWhiteSpace(eligibility.EligibilityReason)
                    ? "Pattern synthesis is blocked before target resolution."
                    : eligibility.EligibilityReason,
                "blocked",
                artifactLinks,
                stagedSessionByCandidate,
                observedUtc);
        }

        var resolution = ResolveSynthesis(repoRoot, entry, snapshotFiles, synthesisType);
        if (!resolution.IsSuccessful)
        {
            return BuildBlockedCandidate(
                workspaceId,
                entry,
                match,
                provenance,
                candidateId,
                synthesisType,
                eligibility,
                resolution.TargetPaths,
                resolution.TargetAnchorType,
                resolution.TargetAnchorValue,
                resolution.BlockedReason,
                resolution.AnchorConfidence,
                artifactLinks,
                stagedSessionByCandidate,
                observedUtc);
        }

        var diffText = resolution.ChangeKind switch
        {
            "modified" => BuildModifyFileDiff(resolution.TargetRelativePath, resolution.OldText, resolution.NewText),
            _ => BuildCreateFileDiff(resolution.TargetRelativePath, resolution.NewText)
        };
        var stagedSessionId = stagedSessionByCandidate.TryGetValue(candidateId, out var existingSessionId)
            ? existingSessionId
            : string.Empty;
        var candidate = new BuilderPatternPatchCandidateRecord(
            candidateId,
            workspaceId,
            entry.PatternEntryId,
            match.MatchId,
            entry.SourceSnapshotId,
            workspaceId,
            new[] { resolution.TargetRelativePath },
            synthesisType,
            EligibilityReady,
            $"Approved pattern {entry.PatternEntryId} cleared deterministic synthesis checks for {synthesisType.Replace('_', ' ')}.",
            false,
            false,
            false,
            diffText,
            ResolveConfidenceClass(match, resolution.AnchorConfidence, blocked: false),
            ResolveRiskLevel(entry, synthesisType, blocked: false),
            entry.LicenseStatus,
            entry.ApprovedUsageClass,
            resolution.TargetAnchorType,
            resolution.TargetAnchorValue,
            resolution.TargetResolutionReason,
            resolution.AnchorConfidence,
            stagedSessionId,
            true,
            artifactLinks,
            $"Synthesized {synthesisType.Replace('_', ' ')} candidate from {entry.PatternName} into {resolution.TargetRelativePath}.",
            observedUtc);
        var explanation = new BuilderPatternPatchExplanationRecord(
            ComputeDeterministicId("patch_explanation", candidateId),
            candidateId,
            $"Pattern {entry.PatternName} mapped to {resolution.TargetRelativePath} using deterministic {synthesisType.Replace('_', ' ')} rules.",
            new[]
            {
                $"Eligibility gate passed with usage class {entry.ApprovedUsageClass.Replace('_', ' ')} and license state {entry.LicenseStatus.Replace('_', ' ')}.",
                $"Pattern match {match.MatchId} scored {match.MatchScore:0.##} as {match.FitClassification.Replace('_', ' ')}.",
                $"Source element {resolution.SourceRelativePath} was selected from snapshot {entry.SourceSnapshotId}.",
                $"Target {resolution.TargetRelativePath} was resolved via {resolution.TargetResolutionReason}.",
                $"Deterministic diff output was generated for {resolution.ChangeKind} using anchor {resolution.TargetAnchorType}."
            },
            new[] { resolution.SourceRelativePath },
            new[] { resolution.TargetRelativePath },
            resolution.MappingRules,
            DescribeReasons(match.MatchReasons),
            resolution.TargetSelectionSummary,
            artifactLinks,
            observedUtc);
        var patchProvenance = new BuilderPatternPatchProvenanceEntryRecord(
            ComputeDeterministicId("patch_provenance", candidateId),
            candidateId,
            entry.PatternEntryId,
            match.MatchId,
            entry.SourceSnapshotId,
            provenance?.OriginalUrl ?? entry.SourceOrigin,
            provenance?.CanonicalSourceId ?? entry.SourceOrigin,
            provenance?.ResolvedCommitOrContentHash ?? snapshot.ResolvedCommitOrContentHash,
            entry.LicenseStatus,
            entry.ApprovedUsageClass,
            artifactLinks,
            $"Patch candidate {candidateId} remains pinned to snapshot {entry.SourceSnapshotId} with {entry.LicenseStatus.Replace('_', ' ')} licensing.",
            observedUtc);
        var patchMatch = new BuilderPatternPatchMatchRecord(
            candidateId,
            entry.PatternEntryId,
            match.MatchId,
            workspaceId,
            resolution.TargetRelativePath,
            synthesisType,
            EligibilityReady,
            resolution.TargetAnchorType,
            resolution.TargetAnchorValue,
            resolution.TargetResolutionReason,
            resolution.AnchorConfidence,
            match.FitClassification,
            artifactLinks,
            $"Pattern match {match.MatchId} resolved {synthesisType.Replace('_', ' ')} toward {resolution.TargetRelativePath}.",
            observedUtc);
        return new MaterializedPatternPatchCandidate(candidate, explanation, patchProvenance, patchMatch);
    }

    private static MaterializedPatternPatchCandidate BuildBlockedCandidate(
        string workspaceId,
        BuilderPatternLibraryEntryRecord entry,
        BuilderPatternLibraryMatchRecord match,
        BuilderPatternLibraryProvenanceEntryRecord? provenance,
        string candidateId,
        string synthesisType,
        SynthesisEligibilityResult eligibility,
        IReadOnlyList<string> targetPaths,
        string anchorType,
        string anchorValue,
        string blockedReason,
        string anchorConfidence,
        IReadOnlyList<string> artifactLinks,
        IReadOnlyDictionary<string, string> stagedSessionByCandidate,
        DateTimeOffset observedUtc)
    {
        var stagedSessionId = stagedSessionByCandidate.TryGetValue(candidateId, out var existingSessionId)
            ? existingSessionId
            : string.Empty;
        var candidate = new BuilderPatternPatchCandidateRecord(
            candidateId,
            workspaceId,
            entry.PatternEntryId,
            match.MatchId,
            entry.SourceSnapshotId,
            workspaceId,
            targetPaths,
            synthesisType,
            eligibility.EligibilityState,
            blockedReason,
            eligibility.BlockedByLicense,
            eligibility.BlockedByReviewState,
            eligibility.BlockedByUsageClass,
            string.Empty,
            "blocked",
            ResolveRiskLevel(entry, synthesisType, blocked: true),
            entry.LicenseStatus,
            entry.ApprovedUsageClass,
            anchorType,
            anchorValue,
            blockedReason,
            anchorConfidence,
            stagedSessionId,
            true,
            artifactLinks,
            $"Synthesis for {entry.PatternName} is blocked for {synthesisType.Replace('_', ' ')}: {blockedReason}",
            observedUtc);
        var explanation = new BuilderPatternPatchExplanationRecord(
            ComputeDeterministicId("patch_explanation", candidateId),
            candidateId,
            $"Deterministic synthesis is blocked for {synthesisType.Replace('_', ' ')}.",
            new[]
            {
                $"Eligibility state: {eligibility.EligibilityState.Replace('_', ' ')}.",
                blockedReason,
                $"Pattern match {match.MatchId} remains advisory at {match.MatchScore:0.##} but cannot synthesize under the current approval, license, or mapping rules."
            },
            Array.Empty<string>(),
            targetPaths,
            new[] { "blocked_candidate_recorded", "no_repo_mutation", "manual_review_required" },
            DescribeReasons(match.MatchReasons),
            "Target resolution stopped before a deterministic diff could be produced.",
            artifactLinks,
            observedUtc);
        var patchProvenance = new BuilderPatternPatchProvenanceEntryRecord(
            ComputeDeterministicId("patch_provenance", candidateId),
            candidateId,
            entry.PatternEntryId,
            match.MatchId,
            entry.SourceSnapshotId,
            provenance?.OriginalUrl ?? entry.SourceOrigin,
            provenance?.CanonicalSourceId ?? entry.SourceOrigin,
            provenance?.ResolvedCommitOrContentHash ?? entry.SourceSnapshotId,
            entry.LicenseStatus,
            entry.ApprovedUsageClass,
            artifactLinks,
            $"Blocked candidate {candidateId} still retains full provenance to pattern {entry.PatternEntryId}.",
            observedUtc);
        var patchMatch = new BuilderPatternPatchMatchRecord(
            candidateId,
            entry.PatternEntryId,
            match.MatchId,
            workspaceId,
            targetPaths.FirstOrDefault() ?? string.Empty,
            synthesisType,
            eligibility.EligibilityState,
            anchorType,
            anchorValue,
            blockedReason,
            anchorConfidence,
            match.FitClassification,
            artifactLinks,
            $"Pattern match {match.MatchId} is blocked for {synthesisType.Replace('_', ' ')} synthesis.",
            observedUtc);
        return new MaterializedPatternPatchCandidate(candidate, explanation, patchProvenance, patchMatch);
    }

    private static IReadOnlyList<string> ResolvePlannedSynthesisTypes(BuilderPatternLibraryEntryRecord entry)
        => entry.PatternType switch
        {
            BuilderPatternLibraryService.PatternTypeBuildTestPipeline => new[] { SynthesisTypeConfigUpdate, SynthesisTypeTestScaffoldAddition },
            BuilderPatternLibraryService.PatternTypeServiceWiring => new[] { SynthesisTypeServiceRegistration },
            BuilderPatternLibraryService.PatternTypeUiViewModel => new[] { SynthesisTypeViewModelWiring },
            BuilderPatternLibraryService.PatternTypeArtifactGeneration => new[] { SynthesisTypeArtifactWriterAddition },
            BuilderPatternLibraryService.PatternTypeDeterministicSerialization => new[] { SynthesisTypeFileModify },
            BuilderPatternLibraryService.PatternTypeReviewApprovalWorkflow => new[] { SynthesisTypeStructuralInsert },
            BuilderPatternLibraryService.PatternTypeHelperUtilitySnippet => new[] { SynthesisTypeFileCreate },
            BuilderPatternLibraryService.PatternTypeProjectStructure => new[] { SynthesisTypeFileCreate },
            _ => new[] { SynthesisTypeFileCreate }
        };

    private static SynthesisEligibilityResult EvaluateSynthesisEligibility(
        BuilderPatternLibraryEntryRecord entry,
        BuilderPatternLibraryProvenanceEntryRecord? provenance,
        BuilderExternalSourceSnapshotRecord? snapshot)
    {
        if (snapshot is null)
        {
            return new SynthesisEligibilityResult(false, "blocked_missing_snapshot", "Pattern snapshot is not available for deterministic synthesis.", true, false, false);
        }

        if (!string.Equals(entry.Eligibility.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Eligibility.ReviewStatus, "operator_reviewed", StringComparison.OrdinalIgnoreCase))
        {
            return new SynthesisEligibilityResult(false, "blocked_review_state", "Pattern entry has not cleared explicit review and approval requirements for synthesis.", true, false, false);
        }

        if (string.IsNullOrWhiteSpace(snapshot.ResolvedCommitOrContentHash) ||
            string.IsNullOrWhiteSpace(snapshot.ContentHash) ||
            provenance is null ||
            string.IsNullOrWhiteSpace(provenance.ResolvedCommitOrContentHash))
        {
            return new SynthesisEligibilityResult(false, "blocked_missing_provenance", "Pattern entry is missing pinned provenance required for deterministic synthesis.", true, false, false);
        }

        if (!string.Equals(entry.LicenseStatus, LicenseClear, StringComparison.OrdinalIgnoreCase))
        {
            return new SynthesisEligibilityResult(false, "blocked_by_license", $"License state {entry.LicenseStatus.Replace('_', ' ')} does not permit synthesized patch proposals.", false, true, false);
        }

        if (!string.Equals(entry.ApprovedUsageClass, BuilderPatternLibraryService.UsageClassSnippetCandidate, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.ApprovedUsageClass, BuilderPatternLibraryService.UsageClassVendorableWithReview, StringComparison.OrdinalIgnoreCase))
        {
            return new SynthesisEligibilityResult(false, "blocked_by_usage_class", $"Usage class {entry.ApprovedUsageClass.Replace('_', ' ')} is limited to advisory reference use.", false, false, true);
        }

        return new SynthesisEligibilityResult(true, EligibilityReady, "Approved pattern entry cleared pinned provenance, review approval, usage class, and license requirements.", false, false, false);
    }

    private static SynthesisResolution ResolveSynthesis(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles,
        string synthesisType)
        => synthesisType switch
        {
            SynthesisTypeConfigUpdate => ResolveConfigUpdate(repoRoot, snapshotFiles),
            SynthesisTypeServiceRegistration => ResolveGeneratedSourceFile(repoRoot, entry, snapshotFiles, synthesisType, "Generated", "Service registration helper", ".cs"),
            SynthesisTypeViewModelWiring => ResolveViewModelFile(repoRoot, entry, snapshotFiles),
            SynthesisTypeArtifactWriterAddition => ResolveGeneratedSourceFile(repoRoot, entry, snapshotFiles, synthesisType, "Generated", "Artifact writer helper", ".cs"),
            SynthesisTypeTestScaffoldAddition => ResolveGeneratedTestFile(repoRoot, entry, snapshotFiles),
            SynthesisTypeFileModify => ResolveModifiedSourceFile(repoRoot, entry, snapshotFiles, synthesisType, "deterministic serialization"),
            SynthesisTypeStructuralInsert => ResolveModifiedSourceFile(repoRoot, entry, snapshotFiles, synthesisType, "review workflow"),
            _ => ResolveGeneratedSourceFile(repoRoot, entry, snapshotFiles, synthesisType, "Generated", "Approved pattern helper", ".cs")
        };

    private static SynthesisResolution ResolveGeneratedSourceFile(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles,
        string synthesisType,
        string targetFolderName,
        string summaryLabel,
        string extension)
    {
        var sourceFile = SelectPreferredSnapshotFile(snapshotFiles, file => file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase));
        if (sourceFile is null)
        {
            return SynthesisResolution.Blocked($"No approved C# source file was available for {synthesisType.Replace('_', ' ')} synthesis.");
        }

        var sourceProject = FindPrimarySourceProjectFile(repoRoot);
        if (string.IsNullOrWhiteSpace(sourceProject))
        {
            return SynthesisResolution.Blocked("No source project file exists in the target workspace.");
        }

        var projectDirectory = Path.GetDirectoryName(sourceProject)!;
        var relativeProjectDirectory = NormalizeRelativePath(Path.GetRelativePath(repoRoot, projectDirectory));
        var fileName = $"{BuildPatternFileStem(entry)}{ResolveSynthesisFileSuffix(synthesisType)}{extension}";
        var targetDirectory = NormalizeRelativePath(Path.Combine(relativeProjectDirectory, targetFolderName));
        var targetRelativePath = NormalizeRelativePath(Path.Combine(targetDirectory, fileName));
        var targetNamespace = BuildNamespaceForRelativePath(sourceProject, targetRelativePath);
        var newText = BuildGeneratedFileContent(sourceFile, targetNamespace, entry, synthesisType);
        return SynthesisResolution.Success(
            targetRelativePath,
            "exact_project_root",
            NormalizeRelativePath(Path.GetRelativePath(repoRoot, sourceProject)),
            $"Primary source project {NormalizeRelativePath(Path.GetRelativePath(repoRoot, sourceProject))} anchors the generated file.",
            "high",
            "created",
            string.Empty,
            newText,
            sourceFile.RelativePath,
            $"{summaryLabel} path resolved under {targetDirectory}.",
            new[] { "source_project_anchor", "generated_file_path", "namespace_rewrite" });
    }

    private static SynthesisResolution ResolveViewModelFile(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles)
    {
        var sourceFile = SelectPreferredSnapshotFile(
            snapshotFiles,
            file => file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    (file.RelativePath.Contains("ViewModel", StringComparison.OrdinalIgnoreCase) ||
                     file.RelativePath.Contains("ViewModels", StringComparison.OrdinalIgnoreCase)));
        if (sourceFile is null)
        {
            return SynthesisResolution.Blocked("No approved view-model source file was available for view model wiring synthesis.");
        }

        var sourceProject = FindPrimarySourceProjectFile(repoRoot);
        if (string.IsNullOrWhiteSpace(sourceProject))
        {
            return SynthesisResolution.Blocked("No source project file exists in the target workspace.");
        }

        var projectDirectory = Path.GetDirectoryName(sourceProject)!;
        var existingViewModelsDirectory = Directory.EnumerateDirectories(projectDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "ViewModels", StringComparison.OrdinalIgnoreCase));
        var targetDirectory = existingViewModelsDirectory is null
            ? Path.Combine(projectDirectory, "Generated", "ViewModels")
            : Path.Combine(existingViewModelsDirectory, "Generated");
        var targetRelativePath = NormalizeRelativePath(Path.Combine(
            Path.GetRelativePath(repoRoot, targetDirectory),
            $"{BuildPatternFileStem(entry)}PatternViewModel.cs"));
        var targetNamespace = BuildNamespaceForRelativePath(sourceProject, targetRelativePath);
        var newText = BuildGeneratedFileContent(sourceFile, targetNamespace, entry, SynthesisTypeViewModelWiring);
        return SynthesisResolution.Success(
            targetRelativePath,
            existingViewModelsDirectory is null ? "generated_viewmodels_directory" : "existing_viewmodels_directory",
            existingViewModelsDirectory is null
                ? NormalizeRelativePath(Path.Combine(Path.GetRelativePath(repoRoot, projectDirectory), "Generated", "ViewModels"))
                : NormalizeRelativePath(Path.GetRelativePath(repoRoot, existingViewModelsDirectory)),
            existingViewModelsDirectory is null
                ? "No ViewModels directory existed, so the generated candidate was placed under Generated\\ViewModels."
                : $"Existing ViewModels directory {NormalizeRelativePath(Path.GetRelativePath(repoRoot, existingViewModelsDirectory))} anchors the target path.",
            "high",
            "created",
            string.Empty,
            newText,
            sourceFile.RelativePath,
            "View model candidate resolved into the workspace ViewModels surface.",
            new[] { "viewmodels_anchor", "generated_viewmodel_file", "namespace_rewrite" });
    }

    private static SynthesisResolution ResolveGeneratedTestFile(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles)
    {
        var sourceFile = SelectPreferredSnapshotFile(snapshotFiles, file =>
            file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
            (file.RelativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
             file.RelativePath.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase)));
        if (sourceFile is null)
        {
            return SynthesisResolution.Blocked("No approved test scaffold source file was available for test scaffold synthesis.");
        }

        var testProject = FindPrimaryTestProjectFile(repoRoot);
        if (string.IsNullOrWhiteSpace(testProject))
        {
            return SynthesisResolution.Blocked("No test project file exists in the target workspace.");
        }

        var testDirectory = Path.GetDirectoryName(testProject)!;
        var targetRelativePath = NormalizeRelativePath(Path.Combine(
            Path.GetRelativePath(repoRoot, testDirectory),
            $"{BuildPatternFileStem(entry)}PatternTests.cs"));
        var targetNamespace = BuildNamespaceForRelativePath(testProject, targetRelativePath);
        var newText = BuildGeneratedFileContent(sourceFile, targetNamespace, entry, SynthesisTypeTestScaffoldAddition);
        return SynthesisResolution.Success(
            targetRelativePath,
            "test_project_root",
            NormalizeRelativePath(Path.GetRelativePath(repoRoot, testProject)),
            $"Primary test project {NormalizeRelativePath(Path.GetRelativePath(repoRoot, testProject))} anchors the scaffold target.",
            "high",
            "created",
            string.Empty,
            newText,
            sourceFile.RelativePath,
            "Test scaffold candidate resolved into the workspace test project.",
            new[] { "test_project_anchor", "generated_test_scaffold", "namespace_rewrite" });
    }

    private static SynthesisResolution ResolveModifiedSourceFile(
        string repoRoot,
        BuilderPatternLibraryEntryRecord entry,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles,
        string synthesisType,
        string summaryLabel)
    {
        var sourceFile = SelectPreferredSnapshotFile(snapshotFiles, file => file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase));
        if (sourceFile is null)
        {
            return SynthesisResolution.Blocked($"No approved C# source file was available for {synthesisType.Replace('_', ' ')} synthesis.");
        }

        var targetFile = FindPrimarySourceCodeFile(repoRoot);
        if (string.IsNullOrWhiteSpace(targetFile) || !File.Exists(targetFile))
        {
            return SynthesisResolution.Blocked("No existing source file exists in the target workspace for structural synthesis.");
        }

        var projectFile = FindPrimarySourceProjectFile(repoRoot);
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            return SynthesisResolution.Blocked("No source project file exists in the target workspace.");
        }

        var targetRelativePath = NormalizeRelativePath(Path.GetRelativePath(repoRoot, targetFile));
        var targetNamespace = BuildNamespaceForRelativePath(projectFile, targetRelativePath);
        var oldText = NormalizeLineEndings(File.ReadAllText(targetFile));
        var sourceBody = BuildGeneratedBody(sourceFile, targetNamespace, entry, synthesisType);
        var insertion = $"// Approved pattern synthesis: {entry.PatternEntryId} ({synthesisType}){System.Environment.NewLine}{sourceBody}";
        var newText = oldText.TrimEnd() + System.Environment.NewLine + System.Environment.NewLine + insertion + System.Environment.NewLine;
        return SynthesisResolution.Success(
            targetRelativePath,
            "existing_source_file",
            targetRelativePath,
            $"Existing source file {targetRelativePath} anchors the {summaryLabel} insertion.",
            "medium",
            "modified",
            oldText,
            newText,
            sourceFile.RelativePath,
            $"{summaryLabel} candidate appends deterministic approved source content to {targetRelativePath}.",
            new[] { "existing_source_anchor", "append_at_end_of_file", "namespace_rewrite" });
    }

    private static SynthesisResolution ResolveConfigUpdate(
        string repoRoot,
        IReadOnlyList<SnapshotFileRecord> snapshotFiles)
    {
        var sourceProjectFile = SelectPreferredSnapshotFile(snapshotFiles, file => file.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase));
        if (sourceProjectFile is null)
        {
            return SynthesisResolution.Blocked("No approved project file was available for deterministic config update synthesis.");
        }

        var targetProjectFile = FindPrimarySourceProjectFile(repoRoot);
        if (string.IsNullOrWhiteSpace(targetProjectFile) || !File.Exists(targetProjectFile))
        {
            return SynthesisResolution.Blocked("No target project file exists in the workspace for config update synthesis.");
        }

        var sourceText = NormalizeLineEndings(File.ReadAllText(sourceProjectFile.FullPath));
        var targetText = NormalizeLineEndings(File.ReadAllText(targetProjectFile));
        var sourcePackageRefs = ExtractPackageReferences(sourceText);
        if (sourcePackageRefs.Count == 0)
        {
            return SynthesisResolution.Blocked("The approved source project did not contain deterministic package references to map.");
        }

        var targetPackages = ExtractPackageReferences(targetText)
            .Select(reference => reference.Include)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingPackages = sourcePackageRefs
            .Where(reference => !targetPackages.Contains(reference.Include))
            .OrderBy(reference => reference.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPackages.Length == 0)
        {
            return SynthesisResolution.Blocked("The target project already contains the deterministic package references from the approved pattern.");
        }

        var insertion = new StringBuilder();
        insertion.AppendLine("  <ItemGroup>");
        foreach (var reference in missingPackages)
        {
            insertion.Append("    <PackageReference Include=\"");
            insertion.Append(reference.Include);
            insertion.Append("\" Version=\"");
            insertion.Append(reference.Version);
            insertion.AppendLine("\" />");
        }
        insertion.Append("  </ItemGroup>");

        if (!targetText.Contains("</Project>", StringComparison.OrdinalIgnoreCase))
        {
            return SynthesisResolution.Blocked("The target project file did not contain a stable </Project> anchor.");
        }

        var newText = targetText.Replace("</Project>", insertion + System.Environment.NewLine + "</Project>", StringComparison.Ordinal);
        return SynthesisResolution.Success(
            NormalizeRelativePath(Path.GetRelativePath(repoRoot, targetProjectFile)),
            "project_file_end",
            "</Project>",
            $"Target project file {NormalizeRelativePath(Path.GetRelativePath(repoRoot, targetProjectFile))} was selected because it is the primary source project.",
            "high",
            "modified",
            targetText,
            newText,
            sourceProjectFile.RelativePath,
            $"Config update maps {missingPackages.Length} package reference(s) from the approved pattern.",
            new[] { "primary_project_file", "package_reference_merge", "project_end_anchor" });
    }

    private static BuilderReviewWorkspaceContext? WriteStagedReviewArtifacts(
        string repoRoot,
        BuilderPatternPatchCandidateRecord candidate,
        BuilderPatternPatchExplanationRecord? explanation,
        BuilderPatternPatchProvenanceEntryRecord? provenance,
        DateTimeOffset observedUtc)
    {
        var sessionId = $"pattern-patch-{candidate.PatchCandidateId}";
        var patchReviewId = $"patch-review-{sessionId}";
        var patchDiffReviewId = $"patch-diff-review-{sessionId}";
        var executionSessionPath = BuilderReviewWorkspaceService.ConversationExecutionSessionPathForRepo(repoRoot);
        var patchReviewPath = BuilderReviewWorkspaceService.PatchReviewPathForRepo(repoRoot);
        var patchDiffReviewPath = BuilderReviewWorkspaceService.PatchDiffReviewPathForRepo(repoRoot);
        var fileDecisionPath = BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot);
        var patchOutcomePath = BuilderReviewWorkspaceService.PatchReviewOutcomePathForRepo(repoRoot);
        var patchApplyPath = BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot);
        var patchBundlePath = BuilderReviewWorkspaceService.PatchBundlePathForRepo(repoRoot);
        var linkedArtifacts = BuildArtifactLinks(
            candidate.ArtifactLinks,
            explanation?.ArtifactLinks ?? Array.Empty<string>(),
            provenance?.ArtifactLinks ?? Array.Empty<string>(),
            new[] { patchReviewPath, patchDiffReviewPath, fileDecisionPath, patchOutcomePath, patchApplyPath });
        var changeKind = candidate.SynthesisType switch
        {
            SynthesisTypeConfigUpdate or SynthesisTypeFileModify or SynthesisTypeStructuralInsert => "modified",
            _ => "created"
        };
        var changedFiles = candidate.TargetPaths
            .Select(path => new BuilderConversationChangedFileRecord(NormalizeRelativePath(path), InferFileCategory(path), changeKind, candidate.Summary, true))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var patchReviewFiles = candidate.TargetPaths
            .Select(path => new BuilderPatchReviewChangedFileRecord(NormalizeRelativePath(path), InferFileCategory(path), changeKind, candidate.Summary, true))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var diffEntries = candidate.TargetPaths
            .Select(path => new BuilderPatchDiffReviewFileEntryRecord(NormalizeRelativePath(path), InferFileCategory(path), changeKind, candidate.Summary, candidate.DiffText, "pending_review", string.Empty, observedUtc))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var decisionEntries = candidate.TargetPaths
            .Select(path => new BuilderFileReviewDecisionEntryRecord(NormalizeRelativePath(path), "pending_review", "pattern_patch_stage", string.Empty, linkedArtifacts, observedUtc))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blockReasons = candidate.TargetPaths
            .Select(path => $"Pending review file {NormalizeRelativePath(path)} must be approved before finalize.")
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        WriteJson(executionSessionPath, new BuilderConversationExecutionSessionRecord(
            sessionId,
            candidate.PatternEntryId,
            provenance?.PatchProvenanceId ?? string.Empty,
            $"Review synthesized patch candidate {candidate.PatchCandidateId} derived from approved pattern {candidate.PatternEntryId}.",
            "pattern_patch_synthesis",
            "pattern_patch_stage",
            candidate.TargetRepoId,
            "Pattern Patch Candidate",
            $"Synthesized from {candidate.PatternEntryId} with {candidate.SynthesisType.Replace('_', ' ')} mapping.",
            "awaiting_review",
            "patch_review",
            "Patch review",
            "pending_review",
            candidate.Summary,
            string.Empty,
            string.Empty,
            string.Empty,
            patchReviewPath,
            patchOutcomePath,
            changedFiles,
            Array.Empty<BuilderConversationStageRecord>(),
            linkedArtifacts,
            $"Pattern patch candidate {candidate.PatchCandidateId} is staged for operator review.",
            executionSessionPath,
            observedUtc));

        WriteJson(patchReviewPath, new BuilderPatchReviewRecord(
            sessionId,
            candidate.PatternEntryId,
            provenance?.PatchProvenanceId ?? string.Empty,
            "pattern_patch_stage",
            candidate.TargetRepoId,
            "Pattern Patch Candidate",
            candidate.Summary,
            "ready",
            patchReviewFiles,
            linkedArtifacts,
            $"Pattern patch review captured {patchReviewFiles.Length} changed file(s).",
            patchReviewPath,
            observedUtc));

        WriteJson(patchDiffReviewPath, new BuilderPatchDiffReviewRecord(
            sessionId,
            patchReviewId,
            patchReviewPath,
            "pending_review",
            "ready",
            diffEntries,
            linkedArtifacts,
            $"Diff review is ready for synthesized patch candidate {candidate.PatchCandidateId}.",
            patchDiffReviewPath,
            observedUtc));

        WriteJson(fileDecisionPath, new BuilderFileReviewDecisionRecord(
            sessionId,
            patchDiffReviewId,
            "pending_review",
            decisionEntries,
            linkedArtifacts,
            "Synthesized patch file review decisions are pending operator approval.",
            fileDecisionPath,
            observedUtc));

        WriteJson(patchOutcomePath, new BuilderPatchReviewOutcomeRecord(
            sessionId,
            "pending_review",
            "awaiting_review",
            "pending_review",
            "Synthesized patch candidate is staged and awaiting operator file review.",
            string.Empty,
            linkedArtifacts,
            "Patch review outcome is pending operator review for the synthesized candidate.",
            patchOutcomePath,
            observedUtc));

        WriteJson(patchApplyPath, new BuilderPatchApplyDecisionRecord(
            sessionId,
            "pending_review",
            "pending_review",
            blockReasons,
            "pending_review",
            linkedArtifacts,
            "Finalize remains blocked until the synthesized patch candidate is fully reviewed and approved.",
            patchApplyPath,
            observedUtc));

        Directory.CreateDirectory(Path.GetDirectoryName(patchBundlePath)!);
        File.WriteAllText(patchBundlePath, NormalizeLineEndings(candidate.DiffText), Encoding.UTF8);
        return BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
            repoRoot,
            new BuilderReviewWorkspacePreferences("all", "directory", NormalizeRelativePath(candidate.TargetPaths.First())),
            observedUtc: observedUtc);
    }

    private static SnapshotFileRecord? SelectPreferredSnapshotFile(
        IReadOnlyList<SnapshotFileRecord> snapshotFiles,
        Func<SnapshotFileRecord, bool> predicate)
        => snapshotFiles
            .Where(predicate)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static IReadOnlyList<SnapshotFileRecord> ResolveSnapshotFilesForEntry(
        IReadOnlyList<SnapshotFileRecord> allFiles,
        BuilderPatternLibraryEntryRecord entry)
    {
        var matchingFiles = allFiles
            .Where(file => entry.KeyPaths.Any(keyPath => MatchesKeyPath(file.RelativePath, keyPath)))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matchingFiles.Length == 0
            ? allFiles.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray()
            : matchingFiles;
    }

    private static IEnumerable<SnapshotFileRecord> EnumerateSnapshotFiles(BuilderExternalSourceSnapshotRecord snapshot)
    {
        if (!Directory.Exists(snapshot.SnapshotRoot))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(snapshot.SnapshotRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsInSkippedDirectory(snapshot.SnapshotRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SnapshotFileRecord(
                NormalizeRelativePath(Path.GetRelativePath(snapshot.SnapshotRoot, path)),
                path,
                Path.GetExtension(path));
        }
    }

    private static bool MatchesKeyPath(string fileRelativePath, string keyPath)
    {
        var normalizedFile = fileRelativePath.Replace('\\', '/');
        var normalizedKey = NormalizeRelativePath(keyPath).Replace('\\', '/').Trim('/');
        return string.Equals(normalizedFile, normalizedKey, StringComparison.OrdinalIgnoreCase) ||
               normalizedFile.StartsWith(normalizedKey + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGeneratedFileContent(
        SnapshotFileRecord sourceFile,
        string targetNamespace,
        BuilderPatternLibraryEntryRecord entry,
        string synthesisType)
    {
        var body = BuildGeneratedBody(sourceFile, targetNamespace, entry, synthesisType);
        var builder = new StringBuilder();
        builder.AppendLine("// Synthesized from approved pattern library entry.");
        builder.Append("// Pattern entry: ");
        builder.Append(entry.PatternEntryId);
        builder.Append(". Usage: ");
        builder.Append(entry.ApprovedUsageClass);
        builder.Append(". License: ");
        builder.Append(entry.LicenseStatus);
        builder.AppendLine(".");
        builder.Append(body);
        if (!body.EndsWith(System.Environment.NewLine, StringComparison.Ordinal))
        {
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildGeneratedBody(
        SnapshotFileRecord sourceFile,
        string targetNamespace,
        BuilderPatternLibraryEntryRecord entry,
        string synthesisType)
    {
        var sourceText = NormalizeLineEndings(File.ReadAllText(sourceFile.FullPath));
        var rewritten = RewriteNamespace(sourceText, targetNamespace);
        if (!rewritten.Contains("namespace ", StringComparison.Ordinal))
        {
            rewritten = $"namespace {targetNamespace};{System.Environment.NewLine}{System.Environment.NewLine}{rewritten.TrimStart()}";
        }

        return rewritten.TrimEnd();
    }

    private static string RewriteNamespace(string sourceText, string targetNamespace)
    {
        var fileScoped = Regex.Replace(
            sourceText,
            @"^\s*namespace\s+[^;]+;\s*$",
            $"namespace {targetNamespace};",
            RegexOptions.Multiline);
        if (!string.Equals(fileScoped, sourceText, StringComparison.Ordinal))
        {
            return fileScoped;
        }

        return Regex.Replace(
            sourceText,
            @"namespace\s+[^{]+\{",
            $"namespace {targetNamespace}{System.Environment.NewLine}{{",
            RegexOptions.Multiline);
    }

    private static string BuildNamespaceForRelativePath(string projectFile, string targetRelativePath)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectFile);
        var relativeDirectory = NormalizeRelativePath(Path.GetDirectoryName(targetRelativePath) ?? string.Empty);
        var directorySegments = relativeDirectory
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(SanitizeNamespaceSegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        return directorySegments.Length == 0
            ? SanitizeNamespaceSegment(projectName)
            : $"{SanitizeNamespaceSegment(projectName)}.{string.Join(".", directorySegments)}";
    }

    private static string FindPrimarySourceProjectFile(string repoRoot)
        => EnumerateRepoFiles(repoRoot, "*.csproj")
            .Where(path => !path.Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Contains(Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;

    private static string FindPrimaryTestProjectFile(string repoRoot)
        => EnumerateRepoFiles(repoRoot, "*.csproj")
            .Where(path => path.Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;

    private static string FindPrimarySourceCodeFile(string repoRoot)
        => EnumerateRepoFiles(repoRoot, "*.cs")
            .Where(path => !path.Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;

    private static IEnumerable<string> EnumerateRepoFiles(string repoRoot, string pattern)
    {
        if (!Directory.Exists(repoRoot))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(repoRoot, pattern, SearchOption.AllDirectories)
                     .Where(path => !IsInSkippedDirectory(repoRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private static bool IsInSkippedDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));
        return relative.Any(segment => SkippedDirectories.Contains(segment));
    }

    private static IReadOnlyList<PackageReferenceRecord> ExtractPackageReferences(string projectText)
    {
        var matches = Regex.Matches(
            projectText,
            "<PackageReference[^>]*Include=\"(?<include>[^\"]+)\"[^>]*Version=\"(?<version>[^\"]+)\"[^>]*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return matches
            .Select(match => new PackageReferenceRecord(
                match.Groups["include"].Value.Trim(),
                match.Groups["version"].Value.Trim()))
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Include) && !string.IsNullOrWhiteSpace(reference.Version))
            .Distinct()
            .OrderBy(reference => reference.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildCreateFileDiff(string relativePath, string newText)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var lines = SplitLines(newText);
        var builder = new StringBuilder();
        builder.AppendLine("--- /dev/null");
        builder.Append("+++ b/");
        builder.AppendLine(normalizedPath);
        builder.Append("@@ -0,0 +1,");
        builder.Append(lines.Length);
        builder.AppendLine(" @@");
        foreach (var line in lines)
        {
            builder.Append('+');
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildModifyFileDiff(string relativePath, string oldText, string newText)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var builder = new StringBuilder();
        builder.Append("--- a/");
        builder.AppendLine(normalizedPath);
        builder.Append("+++ b/");
        builder.AppendLine(normalizedPath);
        builder.Append("@@ -1,");
        builder.Append(oldLines.Length);
        builder.Append(" +1,");
        builder.Append(newLines.Length);
        builder.AppendLine(" @@");
        foreach (var line in oldLines)
        {
            builder.Append('-');
            builder.AppendLine(line);
        }

        foreach (var line in newLines)
        {
            builder.Append('+');
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
        => NormalizeLineEndings(text).TrimEnd('\n').Split('\n');

    private static string NormalizeLineEndings(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string BuildPatchSummary(string workspaceId, IReadOnlyList<BuilderPatternPatchCandidateRecord> candidates)
    {
        if (candidates.Count == 0)
        {
            return $"No synthesized patch candidates are recorded for {workspaceId}.";
        }

        var ready = candidates.Count(candidate => string.Equals(candidate.SynthesisEligibility, EligibilityReady, StringComparison.OrdinalIgnoreCase));
        var blocked = candidates.Count - ready;
        var staged = candidates.Count(candidate => !string.IsNullOrWhiteSpace(candidate.StagedReviewSessionId));
        return $"Recorded {candidates.Count} synthesized patch candidate(s) for {workspaceId}. Ready: {ready}. Blocked: {blocked}. Staged for review: {staged}.";
    }

    private static string BuildExplanationSummary(string workspaceId, int count)
        => count == 0
            ? $"No patch synthesis explanations are recorded for {workspaceId}."
            : $"Recorded {count} deterministic patch synthesis explanation(s) for {workspaceId}.";

    private static string BuildProvenanceSummary(string workspaceId, int count)
        => count == 0
            ? $"No patch synthesis provenance entries are recorded for {workspaceId}."
            : $"Recorded {count} patch synthesis provenance entr{(count == 1 ? "y" : "ies")} for {workspaceId}.";

    private static string BuildMatchSummary(string workspaceId, IReadOnlyList<BuilderPatternPatchMatchRecord> matches)
        => matches.Count == 0
            ? $"No pattern patch target mappings are recorded for {workspaceId}."
            : $"Recorded {matches.Count} deterministic pattern patch target mapping(s) for {workspaceId}.";

    private static int EligibilityRank(string value)
        => string.Equals(value, EligibilityReady, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int SynthesisTypeRank(string synthesisType)
        => synthesisType switch
        {
            SynthesisTypeConfigUpdate => 0,
            SynthesisTypeServiceRegistration => 1,
            SynthesisTypeViewModelWiring => 2,
            SynthesisTypeArtifactWriterAddition => 3,
            SynthesisTypeFileModify => 4,
            SynthesisTypeStructuralInsert => 5,
            SynthesisTypeTestScaffoldAddition => 6,
            _ => 7
        };

    private static string ResolveRiskLevel(BuilderPatternLibraryEntryRecord entry, string synthesisType, bool blocked)
    {
        if (blocked)
        {
            return "high";
        }

        if (string.Equals(synthesisType, SynthesisTypeConfigUpdate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(synthesisType, SynthesisTypeFileModify, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(synthesisType, SynthesisTypeStructuralInsert, StringComparison.OrdinalIgnoreCase))
        {
            return entry.RiskScore >= 55d ? "high" : "moderate";
        }

        return entry.RiskScore >= 65d ? "moderate" : "low";
    }

    private static string ResolveConfidenceClass(BuilderPatternLibraryMatchRecord match, string anchorConfidence, bool blocked)
    {
        if (blocked)
        {
            return "blocked";
        }

        if (string.Equals(anchorConfidence, "high", StringComparison.OrdinalIgnoreCase) && match.MatchScore >= 70d)
        {
            return "high";
        }

        return match.MatchScore >= 55d ? "medium" : "low";
    }

    private static string ResolveSynthesisFileSuffix(string synthesisType)
        => synthesisType switch
        {
            SynthesisTypeServiceRegistration => "ServiceRegistration",
            SynthesisTypeViewModelWiring => "PatternViewModel",
            SynthesisTypeArtifactWriterAddition => "ArtifactWriter",
            SynthesisTypeTestScaffoldAddition => "PatternTests",
            _ => "Pattern"
        };

    private static string BuildPatternFileStem(BuilderPatternLibraryEntryRecord entry)
        => $"{SanitizeToken(entry.PatternName)}-{entry.PatternEntryId[..Math.Min(8, entry.PatternEntryId.Length)]}";

    private static string SanitizeToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return string.IsNullOrWhiteSpace(builder.ToString()) ? "ApprovedPattern" : builder.ToString();
    }

    private static string SanitizeNamespaceSegment(string value)
    {
        var sanitized = SanitizeToken(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "Generated" : sanitized;
    }

    private static string DescribeReasons(IReadOnlyList<string> reasons)
        => reasons.Count == 0 ? "The current workspace context matched the approved pattern." : string.Join(" ", reasons);

    private static string InferFileCategory(string path)
        => path switch
        {
            _ when path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => "build_config",
            _ when path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) => "ui_markup",
            _ when path.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase) => "view_model",
            _ when path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) => "test_code",
            _ => "source_code"
        };

    private static IReadOnlyList<string> BuildArtifactLinks(params IEnumerable<string>[] groups)
        => groups.SelectMany(group => group)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeRelativePath(string? path)
        => (path ?? string.Empty).Trim().Replace('/', '\\').TrimStart('\\');

    private static string ComputeDeterministicId(string prefix, params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"{prefix}-{hash[..10]}";
    }

    private static T? Load<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            lock (GetSaveLock(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (GetSaveLock(path))
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, SerializerOptions);
        }
    }

    private static void WriteJson<T>(string path, T value)
        => Save(path, value);

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    private sealed record SnapshotFileRecord(string RelativePath, string FullPath, string Extension);

    private sealed record PackageReferenceRecord(string Include, string Version);

    private sealed record SynthesisEligibilityResult(
        bool IsEligible,
        string EligibilityState,
        string EligibilityReason,
        bool BlockedByReviewState,
        bool BlockedByLicense,
        bool BlockedByUsageClass);

    private sealed record SynthesisResolution(
        bool IsSuccessful,
        string TargetRelativePath,
        string TargetAnchorType,
        string TargetAnchorValue,
        string TargetResolutionReason,
        string AnchorConfidence,
        string ChangeKind,
        string OldText,
        string NewText,
        string SourceRelativePath,
        string TargetSelectionSummary,
        IReadOnlyList<string> MappingRules,
        string BlockedReason)
    {
        public IReadOnlyList<string> TargetPaths => string.IsNullOrWhiteSpace(TargetRelativePath) ? Array.Empty<string>() : new[] { TargetRelativePath };

        public static SynthesisResolution Success(
            string targetRelativePath,
            string targetAnchorType,
            string targetAnchorValue,
            string targetResolutionReason,
            string anchorConfidence,
            string changeKind,
            string oldText,
            string newText,
            string sourceRelativePath,
            string targetSelectionSummary,
            IReadOnlyList<string> mappingRules)
            => new(
                true,
                targetRelativePath,
                targetAnchorType,
                targetAnchorValue,
                targetResolutionReason,
                anchorConfidence,
                changeKind,
                oldText,
                newText,
                sourceRelativePath,
                targetSelectionSummary,
                mappingRules,
                string.Empty);

        public static SynthesisResolution Blocked(string blockedReason)
            => new(
                false,
                string.Empty,
                "unresolved",
                string.Empty,
                blockedReason,
                "blocked",
                "blocked",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                blockedReason);
    }

    private sealed record MaterializedPatternPatchCandidate(
        BuilderPatternPatchCandidateRecord Candidate,
        BuilderPatternPatchExplanationRecord Explanation,
        BuilderPatternPatchProvenanceEntryRecord Provenance,
        BuilderPatternPatchMatchRecord Match);
}
