using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Settings;

namespace Shoots.UI.Services;

public sealed record SemanticReuseArtifactLink(string Label, string Path);

public sealed record SemanticReuseMetadataField(string Name, string Value);

public sealed record SemanticReuseIndexedCase(
    string DocumentId,
    string CaseType,
    string Title,
    string Summary,
    string Outcome,
    string SourceRunId,
    string PrimaryArtifactPath,
    IReadOnlyList<SemanticReuseArtifactLink> ArtifactLinks,
    IReadOnlyList<SemanticReuseMetadataField> Metadata,
    string SearchText,
    string SourceFingerprint,
    DateTimeOffset RecordedUtc);

public sealed record SemanticReuseIndexLedger(
    int RetentionCount,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SemanticReuseIndexedCase> Entries);

public sealed record SemanticReuseIndexLinkageEntry(
    string DocumentId,
    string CaseType,
    string SourceRunId,
    string PrimaryArtifactPath,
    string SourceFingerprint,
    IReadOnlyList<string> ArtifactPaths,
    DateTimeOffset IndexedUtc);

public sealed record SemanticReuseIndexLinkageLedger(
    int RetentionCount,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SemanticReuseIndexLinkageEntry> Entries);

public sealed record SemanticReuseQuery(
    string ContextId,
    string ContextLabel,
    IReadOnlyList<string> ApprovedCaseTypes,
    string QueryText,
    string Outcome,
    IReadOnlyList<SemanticReuseMetadataField> Signals,
    IReadOnlyList<string> ArtifactPaths,
    string ContextKind = "general",
    IReadOnlyList<string>? PreferredSourceRunIds = null);

public sealed record SemanticReuseSuggestedCase(
    string ContextId,
    string ContextLabel,
    string DocumentId,
    string CaseType,
    string Title,
    string Summary,
    string Outcome,
    double Score,
    string RankingLabel,
    string MatchExplanation,
    string PrimaryArtifactPath,
    IReadOnlyList<SemanticReuseArtifactLink> ArtifactLinks,
    string SourceRunId,
    string ContextKind = "general",
    IReadOnlyList<SemanticReuseMetadataField>? Metadata = null,
    string UsefulnessSummary = "");

public sealed record SemanticReuseSuggestionSet(
    string Status,
    string Summary,
    string DesignNotePath,
    string IndexPath,
    string LinkagePath,
    IReadOnlyList<SemanticReuseSuggestedCase> Suggestions);

public sealed record SemanticReuseVectorPoint(
    string DocumentId,
    IReadOnlyList<float> Vector);

public sealed record SemanticReuseVectorMatch(
    string DocumentId,
    double Score);

public interface ISemanticReuseVectorStore
{
    Task UpsertAsync(string repoKey, IReadOnlyList<SemanticReuseVectorPoint> points, CancellationToken cancellationToken);

    Task<IReadOnlyList<SemanticReuseVectorMatch>> SearchAsync(
        string repoKey,
        IReadOnlyList<float> vector,
        int limit,
        CancellationToken cancellationToken);
}

public interface ISemanticReuseService
{
    string RepoRoot { get; }

    string DesignNotePath { get; }

    string IndexPath { get; }

    string LinkagePath { get; }

    SemanticReuseIndexLedger RefreshLocalIndex(ValidationSettings settings);

    Task<SemanticReuseSuggestionSet> FindSimilarCasesAsync(
        IReadOnlyList<SemanticReuseQuery> queries,
        ValidationSettings settings,
        CancellationToken cancellationToken = default);
}

internal sealed record SemanticReuseProviderDiagnosticArtifact(
    string Provider,
    string State,
    string Classification,
    string ErrorCode,
    string Summary,
    DateTimeOffset ObservedAtUtc,
    string Endpoint);

internal sealed record SemanticReuseProjectDescriptorArtifact(
    string Name,
    string Description,
    string ProjectRootPath);

public sealed record SemanticReuseUsefulnessEvidence(
    string DocumentId,
    string SourceRunId,
    string ValidationRunId,
    string RepairId,
    string OutcomeClassification,
    string EvidenceSummary,
    string EvidenceArtifactPath,
    DateTimeOffset RecordedUtc,
    string ContextKind = "repair_bundle_reference",
    string SurfaceContextKind = "repair_attempt",
    string SuggestionType = "",
    string CaseReference = "",
    IReadOnlyList<string>? LinkedArtifactPaths = null,
    string OutcomeArtifactKind = "validation");

public sealed record SemanticReuseUsefulnessLedger(
    int RetentionCount,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SemanticReuseUsefulnessEvidence> Entries);

public sealed record SemanticReuseEffectivenessContextSummary(
    string ContextKind,
    int EvidenceCount,
    int CleanValidationPassCount,
    int PassedOnRetryCount,
    int ImprovedRepairResultCount,
    int UnchangedOutcomeCount,
    int RegressedOutcomeCount,
    int FailedOutcomeCount);

public sealed record SemanticReuseEffectivenessSummary(
    int RetentionCount,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SemanticReuseEffectivenessContextSummary> Contexts,
    IReadOnlyList<SemanticReuseUsefulnessEvidence> RecentEvidence);

internal sealed record SemanticReusePlaybookCandidate(
    SemanticReuseUsefulnessEvidence Evidence,
    SemanticReuseIndexedCase IndexedCase);

public sealed record SemanticReusePlaybook(
    string PlaybookId,
    string ContextKind,
    string PlaybookClass,
    string Title,
    string Summary,
    string Explanation,
    string Confidence,
    int EvidenceCount,
    IReadOnlyList<SemanticReuseMetadataField> MatchMetadata,
    IReadOnlyList<string> SourceDocumentIds,
    IReadOnlyList<string> LinkedArtifactPaths,
    IReadOnlyList<string> EvidenceArtifactPaths,
    IReadOnlyList<string> OutcomeClassifications,
    DateTimeOffset GeneratedUtc);

public sealed record SemanticReusePlaybookCatalog(
    int MinimumEvidenceCount,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SemanticReusePlaybook> Entries);

public sealed class SemanticReuseService : ISemanticReuseService
{
    private const string DesignNoteFileName = "semantic_reuse_design_note.md";
    private const string IndexFileName = "semantic_reuse_index.json";
    private const string LinkageFileName = "semantic_reuse_index_linkage.json";
    private const string UsefulnessFileName = "semantic_reuse_usefulness.json";
    private const string EffectivenessFileName = "semantic_reuse_effectiveness.json";
    private const string PlaybookFileName = "semantic_reuse_playbooks.json";
    private const int EmbeddingDimensions = 64;
    private static readonly Regex TokenPattern = new(@"[a-z0-9_./:-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ISemanticReuseVectorStore? _vectorStore;

    public SemanticReuseService(string? repoRoot = null, ISemanticReuseVectorStore? vectorStore = null)
    {
        RepoRoot = ResolveRepoRoot(repoRoot);
        _vectorStore = vectorStore;
    }

    public string RepoRoot { get; }

    public string DesignNotePath => DesignNotePathForRepo(RepoRoot);

    public string IndexPath => IndexPathForRepo(RepoRoot);

    public string LinkagePath => LinkagePathForRepo(RepoRoot);

    public static string ArtifactsRootForRepo(string repoRoot)
        => Path.Combine(ResolveRepoRoot(repoRoot), ".codex", "validation-ui");

    public static string DesignNotePathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), DesignNoteFileName);

    public static string IndexPathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), IndexFileName);

    public static string LinkagePathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), LinkageFileName);

    public static string UsefulnessPathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), UsefulnessFileName);

    public static string EffectivenessPathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), EffectivenessFileName);

    public static string PlaybookPathForRepo(string repoRoot)
        => Path.Combine(ArtifactsRootForRepo(repoRoot), PlaybookFileName);

    public static SemanticReuseIndexLedger LoadIndexLedger(string repoRoot)
        => TryLoadArtifact(IndexPathForRepo(repoRoot), new SemanticReuseIndexLedger(0, DateTimeOffset.MinValue, Array.Empty<SemanticReuseIndexedCase>()));

    public static SemanticReuseIndexLinkageLedger LoadLinkageLedger(string repoRoot)
        => TryLoadArtifact(LinkagePathForRepo(repoRoot), new SemanticReuseIndexLinkageLedger(0, DateTimeOffset.MinValue, Array.Empty<SemanticReuseIndexLinkageEntry>()));

    public static SemanticReuseUsefulnessLedger LoadUsefulnessLedger(string repoRoot)
        => TryLoadArtifact(UsefulnessPathForRepo(repoRoot), new SemanticReuseUsefulnessLedger(0, DateTimeOffset.MinValue, Array.Empty<SemanticReuseUsefulnessEvidence>()));

    public static SemanticReuseEffectivenessSummary LoadEffectivenessSummary(string repoRoot)
        => TryLoadArtifact(EffectivenessPathForRepo(repoRoot), new SemanticReuseEffectivenessSummary(0, DateTimeOffset.MinValue, Array.Empty<SemanticReuseEffectivenessContextSummary>(), Array.Empty<SemanticReuseUsefulnessEvidence>()));

    public static SemanticReusePlaybookCatalog LoadPlaybookCatalog(string repoRoot)
        => TryLoadArtifact(PlaybookPathForRepo(repoRoot), new SemanticReusePlaybookCatalog(2, DateTimeOffset.MinValue, Array.Empty<SemanticReusePlaybook>()));

    public static SemanticReuseUsefulnessLedger RecordRepairReferenceOutcome(
        string repoRoot,
        RepairBundle bundle,
        ValidationRunResult validationResult,
        string outcomeClassification,
        ValidationSettings settings)
    {
        var references = (bundle.ReferenceCases ?? Array.Empty<RepairReferenceCase>())
            .Where(reference => !string.IsNullOrWhiteSpace(reference.DocumentId))
            .ToArray();
        if (references.Length == 0)
            return LoadUsefulnessLedger(repoRoot);

        var evidenceArtifactPath = Path.Combine(validationResult.OutputFolder, "validation_result.json");
        var normalizedOutcome = string.IsNullOrWhiteSpace(outcomeClassification)
            ? (validationResult.Success ? "passed" : "failed")
            : outcomeClassification;
        return RecordSuggestionOutcome(
            repoRoot,
            references,
            "repair_bundle_reference",
            bundle.SourceRunId,
            validationResult.RunId,
            bundle.RepairId,
            normalizedOutcome,
            string.IsNullOrWhiteSpace(validationResult.Summary)
                ? $"Repair reference outcome {normalizedOutcome}."
                : validationResult.Summary,
            evidenceArtifactPath,
            bundle.RelatedArtifactPaths
                .Append(validationResult.OutputFolder)
                .Append(validationResult.StabilityArtifactPath ?? string.Empty),
            "validation",
            validationResult.CompletedUtc == default ? DateTimeOffset.UtcNow : validationResult.CompletedUtc,
            settings);
    }

    public static SemanticReuseUsefulnessLedger RecordSuggestionOutcome(
        string repoRoot,
        IEnumerable<RepairReferenceCase> references,
        string contextKind,
        string sourceRunId,
        string validationRunId,
        string repairId,
        string outcomeClassification,
        string evidenceSummary,
        string evidenceArtifactPath,
        IEnumerable<string>? linkedArtifactPaths,
        string outcomeArtifactKind,
        DateTimeOffset recordedUtc,
        ValidationSettings settings)
    {
        var normalizedReferences = (references ?? Array.Empty<RepairReferenceCase>())
            .Where(reference => !string.IsNullOrWhiteSpace(reference.DocumentId))
            .ToArray();
        if (normalizedReferences.Length == 0)
            return LoadUsefulnessLedger(repoRoot);

        var normalizedOutcome = string.IsNullOrWhiteSpace(outcomeClassification)
            ? "failed"
            : outcomeClassification.Trim();
        var normalizedSummary = string.IsNullOrWhiteSpace(evidenceSummary)
            ? $"Suggestion outcome {normalizedOutcome}."
            : evidenceSummary.Trim();
        var normalizedEvidenceArtifactPath = string.IsNullOrWhiteSpace(evidenceArtifactPath)
            ? string.Empty
            : evidenceArtifactPath.Trim();
        var sharedLinkedPaths = NormalizePaths(linkedArtifactPaths);
        var entries = normalizedReferences.Select(reference => new SemanticReuseUsefulnessEvidence(
            reference.DocumentId,
            string.IsNullOrWhiteSpace(sourceRunId) ? reference.SourceRunId : sourceRunId,
            validationRunId ?? string.Empty,
            repairId ?? string.Empty,
            normalizedOutcome,
            normalizedSummary,
            normalizedEvidenceArtifactPath,
            recordedUtc == default ? DateTimeOffset.UtcNow : recordedUtc,
            string.IsNullOrWhiteSpace(contextKind) ? reference.ContextKind : contextKind.Trim(),
            string.IsNullOrWhiteSpace(reference.ContextKind) ? "general" : reference.ContextKind,
            reference.CaseType ?? string.Empty,
            string.IsNullOrWhiteSpace(reference.Title) ? reference.DocumentId : reference.Title,
            NormalizePaths((reference.LinkedArtifactPaths ?? Array.Empty<string>())
                .Append(reference.PrimaryArtifactPath)
                .Append(normalizedEvidenceArtifactPath)
                .Concat(sharedLinkedPaths)),
            string.IsNullOrWhiteSpace(outcomeArtifactKind) ? "validation" : outcomeArtifactKind.Trim()));

        return AppendUsefulnessEvidence(repoRoot, entries, settings);
    }

    private static SemanticReuseUsefulnessLedger AppendUsefulnessEvidence(
        string repoRoot,
        IEnumerable<SemanticReuseUsefulnessEvidence> entries,
        ValidationSettings settings)
    {
        var normalizedSettings = settings.Normalize();
        var normalizedRetention = Math.Clamp(normalizedSettings.SemanticReuseRetentionCount, 20, 500);
        var existing = LoadUsefulnessLedger(repoRoot);
        var nextEntries = existing.Entries
            .Concat(entries ?? Array.Empty<SemanticReuseUsefulnessEvidence>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DocumentId))
            .GroupBy(entry => $"{entry.ContextKind}|{entry.DocumentId}|{entry.RepairId}|{entry.ValidationRunId}|{entry.OutcomeClassification}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entry => entry.RecordedUtc)
                .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(entry => entry.RecordedUtc)
            .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
            .Take(normalizedRetention)
            .ToArray();

        var ledger = new SemanticReuseUsefulnessLedger(normalizedRetention, DateTimeOffset.UtcNow, nextEntries);
        var path = UsefulnessPathForRepo(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(ledger, JsonOptions()));
        WriteDerivedOutcomeArtifacts(repoRoot, LoadIndexLedger(repoRoot), ledger, normalizedSettings);
        return ledger;
    }

    public SemanticReuseIndexLedger RefreshLocalIndex(ValidationSettings settings)
    {
        var normalized = settings.Normalize();
        EnsureArtifactsRoot();
        File.WriteAllText(DesignNotePath, BuildDesignNote());

        var entries = BuildDocuments(normalized)
            .GroupBy(entry => entry.DocumentId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entry => entry.RecordedUtc)
                .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(entry => entry.RecordedUtc)
            .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
            .Take(normalized.SemanticReuseRetentionCount)
            .ToArray();

        var generatedUtc = DateTimeOffset.UtcNow;
        var index = new SemanticReuseIndexLedger(normalized.SemanticReuseRetentionCount, generatedUtc, entries);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, JsonOptions()));

        var linkage = new SemanticReuseIndexLinkageLedger(
            normalized.SemanticReuseRetentionCount,
            generatedUtc,
            entries.Select(entry => new SemanticReuseIndexLinkageEntry(
                    entry.DocumentId,
                    entry.CaseType,
                    entry.SourceRunId,
                    entry.PrimaryArtifactPath,
                    entry.SourceFingerprint,
                    entry.ArtifactLinks
                        .Select(link => link.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray(),
                    entry.RecordedUtc))
                .OrderByDescending(entry => entry.IndexedUtc)
                .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
                .ToArray());
        File.WriteAllText(LinkagePath, JsonSerializer.Serialize(linkage, JsonOptions()));
        WriteDerivedOutcomeArtifacts(RepoRoot, index, LoadUsefulnessLedger(RepoRoot), normalized);

        return index;
    }

    private static void WriteDerivedOutcomeArtifacts(
        string repoRoot,
        SemanticReuseIndexLedger index,
        SemanticReuseUsefulnessLedger usefulnessLedger,
        ValidationSettings settings)
    {
        var effectivenessPath = EffectivenessPathForRepo(repoRoot);
        var playbookPath = PlaybookPathForRepo(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(effectivenessPath)!);
        File.WriteAllText(effectivenessPath, JsonSerializer.Serialize(BuildEffectivenessSummary(usefulnessLedger), JsonOptions()));
        File.WriteAllText(playbookPath, JsonSerializer.Serialize(BuildPlaybookCatalog(index, usefulnessLedger, settings), JsonOptions()));
    }

    private static SemanticReuseEffectivenessSummary BuildEffectivenessSummary(SemanticReuseUsefulnessLedger ledger)
    {
        var normalizedEntries = (ledger.Entries ?? Array.Empty<SemanticReuseUsefulnessEvidence>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DocumentId))
            .OrderByDescending(entry => entry.RecordedUtc)
            .ThenBy(entry => entry.DocumentId, StringComparer.Ordinal)
            .ToArray();
        var contexts = normalizedEntries
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.ContextKind) ? "general" : entry.ContextKind, StringComparer.Ordinal)
            .Select(group => new SemanticReuseEffectivenessContextSummary(
                group.Key,
                group.Count(),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "passed", StringComparison.Ordinal)),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "passed_on_retry", StringComparison.Ordinal)),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "improved", StringComparison.Ordinal)),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "unchanged", StringComparison.Ordinal)),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "regressed", StringComparison.Ordinal)),
                group.Count(entry => string.Equals(entry.OutcomeClassification, "failed", StringComparison.Ordinal))))
            .OrderBy(summary => summary.ContextKind, StringComparer.Ordinal)
            .ToArray();

        return new SemanticReuseEffectivenessSummary(
            ledger.RetentionCount,
            DateTimeOffset.UtcNow,
            contexts,
            normalizedEntries);
    }

    private static SemanticReusePlaybookCatalog BuildPlaybookCatalog(
        SemanticReuseIndexLedger index,
        SemanticReuseUsefulnessLedger ledger,
        ValidationSettings settings)
    {
        var normalizedSettings = settings.Normalize();
        var minimumEvidenceCount = Math.Clamp(normalizedSettings.MinimumPlaybookEvidenceCount, 2, 10);
        var indexedCases = index.Entries.ToDictionary(entry => entry.DocumentId, entry => entry, StringComparer.Ordinal);
        var candidates = (ledger.Entries ?? Array.Empty<SemanticReuseUsefulnessEvidence>())
            .Where(entry => IsPositivePlaybookOutcome(entry.OutcomeClassification))
            .Where(entry => indexedCases.ContainsKey(entry.DocumentId))
            .Select(entry => new SemanticReusePlaybookCandidate(entry, indexedCases[entry.DocumentId]))
            .ToArray();

        var playbooks = candidates
            .GroupBy(item => BuildPlaybookKey(item.IndexedCase, item.Evidence), StringComparer.Ordinal)
            .Select(group => BuildPlaybook(group.Key, group, minimumEvidenceCount))
            .Where(playbook => playbook is not null)
            .Cast<SemanticReusePlaybook>()
            .OrderBy(playbook => playbook.ContextKind, StringComparer.Ordinal)
            .ThenByDescending(playbook => MapPlaybookConfidence(playbook.Confidence))
            .ThenByDescending(playbook => playbook.EvidenceCount)
            .ThenBy(playbook => playbook.Title, StringComparer.Ordinal)
            .ThenBy(playbook => playbook.PlaybookId, StringComparer.Ordinal)
            .ToArray();

        return new SemanticReusePlaybookCatalog(minimumEvidenceCount, DateTimeOffset.UtcNow, playbooks);
    }

    public async Task<SemanticReuseSuggestionSet> FindSimilarCasesAsync(
        IReadOnlyList<SemanticReuseQuery> queries,
        ValidationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var index = RefreshLocalIndex(normalized);
        var usefulnessLedger = LoadUsefulnessLedger(RepoRoot);
        var filteredQueries = (queries ?? Array.Empty<SemanticReuseQuery>())
            .Where(query => query is not null && !string.IsNullOrWhiteSpace(query.QueryText))
            .ToArray();

        if (!normalized.EnableSemanticReuseSuggestions)
        {
            return new SemanticReuseSuggestionSet(
                "disabled",
                "Semantic reuse suggestions are disabled in Validation Options. Deterministic run artifacts remain the source of truth.",
                DesignNotePath,
                IndexPath,
                LinkagePath,
                Array.Empty<SemanticReuseSuggestedCase>());
        }

        if (filteredQueries.Length == 0)
        {
            return new SemanticReuseSuggestionSet(
                "no_context",
                "No current planning context, validation failure, repair result, or provider issue is available for semantic comparison.",
                DesignNotePath,
                IndexPath,
                LinkagePath,
                Array.Empty<SemanticReuseSuggestedCase>());
        }

        var storeStatus = "local_only";
        var storeSummary = "Qdrant suggestions were unavailable, so deterministic local ranking was used.";
        var repoKey = RepoRoot;
        var points = index.Entries
            .Select(entry => new SemanticReuseVectorPoint(entry.DocumentId, CreateVector(entry.SearchText)))
            .ToArray();

        if (_vectorStore is not null && points.Length > 0)
        {
            try
            {
                await _vectorStore.UpsertAsync(repoKey, points, cancellationToken).ConfigureAwait(false);
                storeStatus = "qdrant";
                storeSummary = "Qdrant ranked similar cases, but deterministic local artifacts still control execution and reporting.";
            }
            catch
            {
                storeStatus = "local_only";
                storeSummary = "Qdrant was unavailable, so deterministic local ranking was used.";
            }
        }

        var suggestions = new List<SemanticReuseSuggestedCase>();
        foreach (var query in filteredQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queryVector = CreateVector(query.QueryText);
            var storeScores = new Dictionary<string, double>(StringComparer.Ordinal);
            if (storeStatus == "qdrant" && _vectorStore is not null)
            {
                try
                {
                    var hits = await _vectorStore
                        .SearchAsync(repoKey, queryVector, Math.Max(normalized.MaxSemanticReuseCases * 4, normalized.MaxSemanticReuseCases), cancellationToken)
                        .ConfigureAwait(false);
                    storeScores = hits
                        .GroupBy(hit => hit.DocumentId, StringComparer.Ordinal)
                        .Select(group => group
                            .OrderByDescending(hit => hit.Score)
                            .ThenBy(hit => hit.DocumentId, StringComparer.Ordinal)
                            .First())
                        .ToDictionary(hit => hit.DocumentId, hit => hit.Score, StringComparer.Ordinal);
                }
                catch
                {
                    storeStatus = "local_only";
                    storeSummary = "Qdrant was unavailable, so deterministic local ranking was used.";
                    storeScores = new Dictionary<string, double>(StringComparer.Ordinal);
                }
            }

            suggestions.AddRange(
                index.Entries
                    .Where(entry => IsCandidateForQuery(entry, query, normalized))
                    .Select(entry => BuildSuggestion(query, entry, storeScores, queryVector, usefulnessLedger))
                    .Where(match => match.Score >= 0.16d)
                    .OrderByDescending(match => match.Score)
                    .ThenBy(match => match.ContextLabel, StringComparer.Ordinal)
                    .ThenBy(match => match.DocumentId, StringComparer.Ordinal)
                    .Take(normalized.MaxSemanticReuseCases));
        }

        var deduped = suggestions
            .GroupBy(match => $"{match.ContextId}|{match.DocumentId}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.DocumentId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.ContextLabel, StringComparer.Ordinal)
            .ThenBy(match => match.DocumentId, StringComparer.Ordinal)
            .ToArray();

        if (deduped.Length == 0)
        {
            return new SemanticReuseSuggestionSet(
                storeStatus,
                $"No similar past cases were found. {storeSummary}",
                DesignNotePath,
                IndexPath,
                LinkagePath,
                deduped);
        }

        var contextCount = deduped.Select(match => match.ContextId).Distinct(StringComparer.Ordinal).Count();
        return new SemanticReuseSuggestionSet(
            storeStatus,
            $"Loaded {deduped.Length} similar past case{(deduped.Length == 1 ? string.Empty : "s")} across {contextCount} active context{(contextCount == 1 ? string.Empty : "s")}. {storeSummary}",
            DesignNotePath,
            IndexPath,
            LinkagePath,
            deduped);
    }

    private void EnsureArtifactsRoot()
        => Directory.CreateDirectory(ArtifactsRootForRepo(RepoRoot));

    private IEnumerable<SemanticReuseIndexedCase> BuildDocuments(ValidationSettings settings)
    {
        foreach (var entry in BuildGeneratedOutputDocuments())
            yield return entry;

        foreach (var entry in BuildValidationFailureDocuments())
            yield return entry;

        foreach (var entry in BuildRepairComparisonDocuments())
            yield return entry;

        foreach (var entry in BuildRepairPromotionDocuments())
            yield return entry;

        if (settings.IndexProviderDiagnosticsEpisodes)
        {
            foreach (var entry in BuildProviderDiagnosticDocuments())
                yield return entry;
        }

        foreach (var entry in BuildReplayDiffDocuments())
            yield return entry;

        foreach (var entry in BuildBaselineDriftDocuments())
            yield return entry;
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildGeneratedOutputDocuments()
    {
        foreach (var linkPath in EnumerateRepoArtifactFiles(GeneratedOutputValidationLinkService.FileName))
        {
            var link = TryLoadArtifact<GeneratedOutputValidationLink?>(linkPath, null);
            if (link is null || string.Equals(link.ValidationStatus, "not_validated", StringComparison.Ordinal))
                continue;

            var runPath = Path.GetDirectoryName(linkPath) ?? string.Empty;
            var project = TryLoadProjectDescriptor(runPath);
            var validationResultPath = !string.IsNullOrWhiteSpace(link.ValidationOutputFolder)
                ? Path.Combine(link.ValidationOutputFolder!, "validation_result.json")
                : string.Empty;
            var validationResult = !string.IsNullOrWhiteSpace(validationResultPath)
                ? TryLoadArtifact<ValidationRunResult?>(validationResultPath, null)
                : null;
            var promotion = RepairReviewArtifactsService.LoadPromotion(runPath);
            var failingStage = validationResult?.FirstFailure?.StageLabel
                ?? validationResult?.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))?.StageLabel
                ?? string.Empty;
            var searchText = string.Join(
                ' ',
                new[]
                {
                    project?.Name,
                    project?.Description,
                    link.SourcePath,
                    link.ValidationActionLabel,
                    link.ValidationSummary,
                    link.FirstFailureText,
                    promotion?.ConfidenceText,
                    promotion?.Reason,
                    promotion?.AdoptionReason,
                    promotion?.OperatorNote
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"generated-output|{link.SourceRunId}|{linkPath}"),
                "generated_output_pattern",
                string.IsNullOrWhiteSpace(project?.Name)
                    ? $"Generated output {link.SourceRunId}"
                    : $"{project!.Name} generated output",
                string.IsNullOrWhiteSpace(promotion?.ConfidenceText)
                    ? link.ValidationSummary
                    : $"{link.ValidationSummary} {promotion!.ConfidenceText}",
                link.ValidationStatus,
                link.SourceRunId,
                linkPath,
                NormalizeArtifactLinks(new[]
                {
                    new SemanticReuseArtifactLink("Generated output validation link", linkPath),
                    new SemanticReuseArtifactLink("Source run folder", link.SourceRunPath),
                    new SemanticReuseArtifactLink("Validation result", validationResultPath),
                    new SemanticReuseArtifactLink("Repair promotion", RepairReviewArtifactsService.PromotionPathForRun(runPath))
                }),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("validation_status", link.ValidationStatus),
                    new SemanticReuseMetadataField("source_path", link.SourcePath),
                    new SemanticReuseMetadataField("project_name", project?.Name ?? string.Empty),
                    new SemanticReuseMetadataField("project_description", project?.Description ?? string.Empty),
                    new SemanticReuseMetadataField("promotion_status", promotion?.Status ?? string.Empty),
                    new SemanticReuseMetadataField("adoption_state", promotion?.AdoptionState ?? string.Empty),
                    new SemanticReuseMetadataField("failing_stage", failingStage),
                    new SemanticReuseMetadataField("first_failure_excerpt", link.FirstFailureText ?? string.Empty)
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                link.RecordedUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildValidationFailureDocuments()
    {
        var runsRoot = Path.Combine(ArtifactsRootForRepo(RepoRoot), "runs");
        if (!Directory.Exists(runsRoot))
            yield break;

        foreach (var resultPath in Directory.EnumerateFiles(runsRoot, "validation_result.json", SearchOption.AllDirectories)
                     .Where(path => !ShouldIgnorePath(path))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var result = TryLoadArtifact<ValidationRunResult?>(resultPath, null);
            if (result is null)
                continue;

            var classification = NormalizeValidationClassification(result);
            if (result.Success && string.Equals(classification, "passed", StringComparison.Ordinal))
                continue;

            var firstFailure = result.FirstFailure;
            var failingStage = firstFailure?.StageLabel
                ?? result.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))?.StageLabel
                ?? string.Empty;
            var artifactLinks = new[]
            {
                new SemanticReuseArtifactLink("Validation result", resultPath),
                new SemanticReuseArtifactLink("Validation stability", ResolveStabilityPath(result)),
                new SemanticReuseArtifactLink("Validation output folder", result.OutputFolder)
            };
            var recordedUtc = result.CompletedUtc == default ? File.GetLastWriteTimeUtc(resultPath) : result.CompletedUtc;
            var searchText = string.Join(
                ' ',
                new[]
                {
                    result.ActionLabel,
                    result.Summary,
                    result.FirstFailureText,
                    firstFailure?.FailingTestName,
                    failingStage,
                    string.Join(" ", result.Stages.Select(stage => $"{stage.StageLabel} {stage.Status} {stage.Summary}"))
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"validation|{result.RunId}|{resultPath}"),
                "validation_failure_record",
                result.ActionLabel,
                string.IsNullOrWhiteSpace(result.FirstFailureText) ? result.Summary : $"{result.Summary} First failure: {result.FirstFailureText}",
                classification,
                result.RunId,
                resultPath,
                NormalizeArtifactLinks(artifactLinks),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("failing_stage", failingStage),
                    new SemanticReuseMetadataField("failing_test_name", firstFailure?.FailingTestName ?? string.Empty),
                    new SemanticReuseMetadataField("first_failure_excerpt", result.FirstFailureText ?? string.Empty),
                    new SemanticReuseMetadataField("stability_classification", classification),
                    new SemanticReuseMetadataField("action_label", result.ActionLabel)
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                recordedUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildRepairComparisonDocuments()
    {
        var repairsRoot = Path.Combine(ArtifactsRootForRepo(RepoRoot), "repairs");
        if (!Directory.Exists(repairsRoot))
            yield break;

        foreach (var comparisonPath in Directory.EnumerateFiles(repairsRoot, RepairReviewArtifactsService.ComparisonFileName, SearchOption.AllDirectories)
                     .Where(path => !ShouldIgnorePath(path))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var comparison = TryLoadArtifact<RepairComparisonRecord?>(comparisonPath, null);
            if (comparison is null)
                continue;

            var bundle = TryLoadArtifact<RepairBundle?>(comparison.RepairBundlePath, null);
            var promotion = bundle is null || string.IsNullOrWhiteSpace(bundle.SourceRunPath)
                ? null
                : RepairReviewArtifactsService.LoadPromotion(bundle.SourceRunPath);

            var changedFileNames = comparison.ChangedFiles
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var searchText = string.Join(
                ' ',
                new[]
                {
                    comparison.SourceValidationSummary,
                    comparison.SourceFailedStage,
                    comparison.SourceFirstFailureExcerpt,
                    comparison.RepairedValidationSummary,
                    comparison.RepairedFailedStage,
                    comparison.RepairedFirstFailureExcerpt,
                    comparison.ImprovementState,
                    comparison.RepairedValidationStatus,
                    promotion?.Status,
                    promotion?.AdoptionState,
                    string.Join(" ", changedFileNames)
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"repair|{comparison.RepairId}|{comparisonPath}"),
                "repair_bundle_summary",
                $"Repair {comparison.RepairId}",
                $"{comparison.SourceFailedStage}: {comparison.SourceFirstFailureExcerpt} Repair outcome {comparison.ImprovementState}.",
                comparison.ImprovementState,
                comparison.SourceValidationRunId,
                comparisonPath,
                NormalizeArtifactLinks(new[]
                {
                    new SemanticReuseArtifactLink("Repair comparison", comparisonPath),
                    new SemanticReuseArtifactLink("Repair bundle", comparison.RepairBundlePath),
                    new SemanticReuseArtifactLink("Repair result folder", comparison.RepairResultFolder),
                    new SemanticReuseArtifactLink("Linked validation run", comparison.LinkedValidationRunFolder)
                }),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("failing_stage", comparison.SourceFailedStage),
                    new SemanticReuseMetadataField("first_failure_excerpt", comparison.SourceFirstFailureExcerpt),
                    new SemanticReuseMetadataField("repaired_stage", comparison.RepairedFailedStage),
                    new SemanticReuseMetadataField("improvement_state", comparison.ImprovementState),
                    new SemanticReuseMetadataField("repaired_validation_status", comparison.RepairedValidationStatus),
                    new SemanticReuseMetadataField("promotion_status", promotion?.Status ?? string.Empty),
                    new SemanticReuseMetadataField("adoption_state", promotion?.AdoptionState ?? string.Empty),
                    new SemanticReuseMetadataField("changed_file_names", string.Join("|", changedFileNames))
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                comparison.RecordedUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildRepairPromotionDocuments()
    {
        foreach (var promotionPath in EnumerateRepoArtifactFiles(RepairReviewArtifactsService.PromotionFileName))
        {
            var promotion = TryLoadArtifact<RepairPromotionRecord?>(promotionPath, null);
            if (promotion is null)
                continue;

            var comparison = RepairReviewArtifactsService.LoadComparison(
                RepairReviewArtifactsService.ComparisonPathForRepair(promotion.RepairResultFolder));
            var changedFileNames = (comparison?.ChangedFiles ?? Array.Empty<string>())
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var searchText = string.Join(
                ' ',
                new[]
                {
                    promotion.RepairId,
                    promotion.ImprovementState,
                    promotion.ConfidenceSignal,
                    promotion.ConfidenceText,
                    promotion.Status,
                    promotion.AdoptionState,
                    promotion.Reason,
                    promotion.OperatorNote,
                    comparison?.SourceFailedStage,
                    comparison?.SourceFirstFailureExcerpt,
                    string.Join(" ", changedFileNames)
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"promotion|{promotion.SourceRunId}|{promotion.RepairId}|{promotionPath}"),
                "repair_promotion_outcome",
                $"Promoted repair {promotion.RepairId}",
                $"{promotion.ConfidenceText} Promotion state {promotion.Status}; adoption state {promotion.AdoptionState}.",
                promotion.ImprovementState,
                promotion.SourceRunId,
                promotionPath,
                NormalizeArtifactLinks(new[]
                {
                    new SemanticReuseArtifactLink("Repair promotion", promotionPath),
                    new SemanticReuseArtifactLink("Repair bundle", promotion.RepairBundlePath),
                    new SemanticReuseArtifactLink("Repair result folder", promotion.RepairResultFolder),
                    new SemanticReuseArtifactLink("Linked validation run", promotion.LinkedValidationRunFolder),
                    new SemanticReuseArtifactLink("Audit summary JSON", promotion.AuditSummaryJsonPath),
                    new SemanticReuseArtifactLink("Audit summary markdown", promotion.AuditSummaryMarkdownPath)
                }),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("improvement_state", promotion.ImprovementState),
                    new SemanticReuseMetadataField("confidence_signal", promotion.ConfidenceSignal),
                    new SemanticReuseMetadataField("adoption_state", promotion.AdoptionState),
                    new SemanticReuseMetadataField("promotion_status", promotion.Status),
                    new SemanticReuseMetadataField("failing_stage", comparison?.SourceFailedStage ?? string.Empty),
                    new SemanticReuseMetadataField("first_failure_excerpt", comparison?.SourceFirstFailureExcerpt ?? string.Empty),
                    new SemanticReuseMetadataField("changed_file_names", string.Join("|", changedFileNames))
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                promotion.StateUpdatedUtc == default ? promotion.PromotedUtc : promotion.StateUpdatedUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildProviderDiagnosticDocuments()
    {
        var diagnosticsPath = Path.Combine(RepoRoot, "provider_diagnostics.json");
        if (!File.Exists(diagnosticsPath))
            yield break;

        var diagnostics = TryLoadArtifact<IReadOnlyList<SemanticReuseProviderDiagnosticArtifact>?>(diagnosticsPath, null)
            ?? Array.Empty<SemanticReuseProviderDiagnosticArtifact>();
        foreach (var diagnostic in diagnostics
                     .Where(item => !string.Equals(item.Classification, "available", StringComparison.Ordinal))
                     .OrderByDescending(item => item.ObservedAtUtc)
                     .ThenBy(item => item.Provider, StringComparer.Ordinal))
        {
            var searchText = string.Join(
                ' ',
                new[]
                {
                    diagnostic.Provider,
                    diagnostic.Classification,
                    diagnostic.State,
                    diagnostic.ErrorCode,
                    diagnostic.Summary,
                    diagnostic.Endpoint
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"provider|{diagnostic.Provider}|{diagnostic.ObservedAtUtc:O}|{diagnostic.Classification}|{diagnostic.ErrorCode}"),
                "provider_diagnostics_episode",
                $"{diagnostic.Provider} {diagnostic.Classification}",
                diagnostic.Summary,
                diagnostic.Classification,
                diagnostic.Provider,
                diagnosticsPath,
                NormalizeArtifactLinks(new[]
                {
                    new SemanticReuseArtifactLink("Provider diagnostics ledger", diagnosticsPath)
                }),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("provider_name", diagnostic.Provider),
                    new SemanticReuseMetadataField("provider_classification", diagnostic.Classification),
                    new SemanticReuseMetadataField("provider_state", diagnostic.State),
                    new SemanticReuseMetadataField("error_code", diagnostic.ErrorCode)
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                diagnostic.ObservedAtUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildReplayDiffDocuments()
    {
        foreach (var replayDiffPath in EnumerateRepoArtifactFiles(RunReplayService.ReplayDiffFileName))
        {
            var diff = TryLoadArtifact<ReplayDiffResult?>(replayDiffPath, null);
            if (diff is null)
                continue;

            var hasInterestingContent = !diff.IsMatch
                || diff.Mismatches.Count > 0
                || diff.StageDiffs.Any(stage => stage.MajorDeviation || !string.Equals(stage.DiffKind, "match", StringComparison.Ordinal));
            if (!hasInterestingContent)
                continue;

            var runPath = Path.GetDirectoryName(replayDiffPath) ?? string.Empty;
            var metadata = TryLoadArtifact<PersistedRunMetadata?>(RunReplayService.MetadataPath(runPath), null);
            var sourceRunId = metadata?.RunId ?? string.Empty;
            var searchText = string.Join(
                ' ',
                new[]
                {
                    diff.Summary,
                    string.Join(" ", diff.Mismatches),
                    string.Join(" ", diff.StageDiffs.Select(stage => $"{stage.StageName} {stage.DiffKind} {stage.Summary}"))
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            yield return new SemanticReuseIndexedCase(
                ComputeDeterministicHash($"replay|{sourceRunId}|{replayDiffPath}"),
                "replay_divergence_summary",
                string.IsNullOrWhiteSpace(sourceRunId) ? "Replay divergence" : $"Replay divergence for {sourceRunId}",
                diff.Summary,
                diff.IsMatch ? "matched" : "diverged",
                sourceRunId,
                replayDiffPath,
                NormalizeArtifactLinks(new[]
                {
                    new SemanticReuseArtifactLink("Replay diff", replayDiffPath),
                    new SemanticReuseArtifactLink("Replay metadata", RunReplayService.MetadataPath(runPath)),
                    new SemanticReuseArtifactLink("Replay timeline", RunReplayService.TimelinePath(runPath)),
                    new SemanticReuseArtifactLink("Replay error", RunReplayService.ReplayErrorPath(runPath))
                }),
                NormalizeMetadata(new[]
                {
                    new SemanticReuseMetadataField("mismatch_count", diff.Mismatches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new SemanticReuseMetadataField("major_deviation", diff.StageDiffs.Any(stage => stage.MajorDeviation).ToString()),
                    new SemanticReuseMetadataField("diff_kind", string.Join("|", diff.StageDiffs.Select(stage => stage.DiffKind).Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal)))
                }),
                searchText,
                ComputeDeterministicHash(searchText),
                metadata?.CreatedUtc == default ? File.GetLastWriteTimeUtc(replayDiffPath) : metadata!.CreatedUtc);
        }
    }

    private IEnumerable<SemanticReuseIndexedCase> BuildBaselineDriftDocuments()
    {
        var comparisonPath = ValidationRunnerService.BaselineComparisonPathForRepo(RepoRoot);
        var regressionPath = ValidationRunnerService.RegressionSummaryPathForRepo(RepoRoot);
        if (!File.Exists(comparisonPath) && !File.Exists(regressionPath))
            yield break;

        var comparison = ValidationRunnerService.LoadBaselineComparison(RepoRoot);
        var regression = ValidationRunnerService.LoadRegressionSummary(RepoRoot);
        if (string.Equals(comparison.DriftClassification, "no_baseline", StringComparison.Ordinal) &&
            string.Equals(regression.Classification, "no_history", StringComparison.Ordinal))
        {
            yield break;
        }

        var reasons = comparison.DriftReasons
            .Concat(regression.Reasons)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var searchText = string.Join(
            ' ',
            new[]
            {
                comparison.DriftClassification,
                comparison.ReadinessClassification,
                string.Join(" ", comparison.ChangedFailingStages),
                regression.Classification,
                string.Join(" ", reasons)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        yield return new SemanticReuseIndexedCase(
            ComputeDeterministicHash($"baseline|{comparison.LatestRunId}|{comparisonPath}|{regressionPath}"),
            "baseline_drift_regression_summary",
            "Validation baseline drift",
            reasons.Length == 0 ? "Baseline drift artifacts are available." : string.Join(" ", reasons),
            string.IsNullOrWhiteSpace(comparison.DriftClassification) ? regression.Classification : comparison.DriftClassification,
            comparison.LatestRunId,
            comparisonPath,
            NormalizeArtifactLinks(new[]
            {
                new SemanticReuseArtifactLink("Baseline comparison", comparisonPath),
                new SemanticReuseArtifactLink("Regression summary", regressionPath),
                new SemanticReuseArtifactLink("Trend summary", ValidationRunnerService.TrendSummaryPathForRepo(RepoRoot)),
                new SemanticReuseArtifactLink("Active baseline", ValidationRunnerService.ActiveBaselinePathForRepo(RepoRoot))
            }),
            NormalizeMetadata(new[]
            {
                new SemanticReuseMetadataField("drift_classification", comparison.DriftClassification),
                new SemanticReuseMetadataField("readiness_classification", comparison.ReadinessClassification),
                new SemanticReuseMetadataField("failing_stage", regression.CurrentFailingStage)
            }),
            searchText,
            ComputeDeterministicHash(searchText),
            comparison.GeneratedUtc == default ? regression.GeneratedUtc : comparison.GeneratedUtc);
    }

    private static string ResolveStabilityPath(ValidationRunResult result)
        => !string.IsNullOrWhiteSpace(result.StabilityArtifactPath)
            ? result.StabilityArtifactPath!
            : Path.Combine(result.OutputFolder, "validation_stability.json");

    private static string NormalizeValidationClassification(ValidationRunResult result)
        => string.IsNullOrWhiteSpace(result.StabilityClassification)
            ? (result.Success ? "passed" : "failed")
            : result.StabilityClassification;

    private static bool ShouldIgnorePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticReuseProjectDescriptorArtifact? TryLoadProjectDescriptor(string runPath)
    {
        var current = Path.GetFullPath(runPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, "project.json");
            if (File.Exists(candidate))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                    var root = document.RootElement;
                    var name = root.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                    var description = root.TryGetProperty("Description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(description))
                        return new SemanticReuseProjectDescriptorArtifact(name, description, current);
                }
                catch
                {
                    return null;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;

            current = parent;
        }

        return null;
    }

    private IEnumerable<string> EnumerateRepoArtifactFiles(string fileName)
    {
        if (!Directory.Exists(RepoRoot))
            yield break;

        foreach (var path in Directory.EnumerateFiles(RepoRoot, fileName, SearchOption.AllDirectories)
                     .Where(path => !ShouldIgnorePath(path))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private static SemanticReuseSuggestedCase BuildSuggestion(
        SemanticReuseQuery query,
        SemanticReuseIndexedCase entry,
        IReadOnlyDictionary<string, double> storeScores,
        IReadOnlyList<float> queryVector,
        SemanticReuseUsefulnessLedger usefulnessLedger)
    {
        var baseScore = storeScores.TryGetValue(entry.DocumentId, out var score)
            ? score
            : CosineSimilarity(queryVector, CreateVector(entry.SearchText));
        var bonus = 0d;
        var reasons = new List<string>();
        if (HasExactLinkedHistory(query, entry))
        {
            bonus += 0.35d;
            reasons.Add("exact linked history");
        }

        if (HasMatchingValue(query.Signals, entry.Metadata, "failing_stage", "repaired_stage"))
        {
            bonus += 0.18d;
            reasons.Add("same failing stage");
        }

        if (HasMatchingValue(query.Signals, entry.Metadata, "provider_classification"))
        {
            bonus += 0.18d;
            reasons.Add("similar provider classification");
        }

        if (HasMatchingValue(query.Signals, entry.Metadata, "failing_test_name"))
        {
            bonus += 0.16d;
            reasons.Add("same failing test");
        }

        if (HasMatchingValue(query.Signals, entry.Metadata, "changed_file_names"))
        {
            bonus += 0.10d;
            reasons.Add("similar changed file set");
        }

        if (HasMatchingValue(query.Signals, entry.Metadata, "project_name", "source_path"))
        {
            bonus += 0.14d;
            reasons.Add("same project scope");
        }

        var textOverlap = ComputeTokenOverlap(query.QueryText, entry.SearchText);
        if (textOverlap >= 0.24d)
        {
            bonus += 0.12d;
            reasons.Add(query.ContextKind switch
            {
                "planning" => "similar planning request",
                "provider_diagnostics" => "similar provider summary",
                _ => "similar first-failure text"
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome) &&
            string.Equals(query.Outcome, entry.Outcome, StringComparison.Ordinal))
        {
            bonus += 0.05d;
        }

        if (reasons.Count == 0 && baseScore >= 0.22d)
            reasons.Add("similar recorded validation context");

        var finalScore = Math.Min(0.99d, baseScore + bonus);
        var usefulnessSummary = BuildUsefulnessSummary(usefulnessLedger, entry.DocumentId);
        return new SemanticReuseSuggestedCase(
            query.ContextId,
            query.ContextLabel,
            entry.DocumentId,
            entry.CaseType,
            entry.Title,
            entry.Summary,
            entry.Outcome,
            finalScore,
            BuildRankingLabel(finalScore),
            string.Join("; ", reasons.Distinct(StringComparer.Ordinal)),
            entry.PrimaryArtifactPath,
            entry.ArtifactLinks,
            entry.SourceRunId,
            query.ContextKind,
            entry.Metadata,
            usefulnessSummary);
    }

    private static bool IsCandidateForQuery(SemanticReuseIndexedCase entry, SemanticReuseQuery query, ValidationSettings settings)
    {
        if (query.ApprovedCaseTypes.Count > 0 &&
            !query.ApprovedCaseTypes.Contains(entry.CaseType, StringComparer.Ordinal))
        {
            return false;
        }

        if (!settings.IncludePromotedRepairSuggestions &&
            string.Equals(entry.CaseType, "repair_promotion_outcome", StringComparison.Ordinal))
        {
            return false;
        }

        if (!settings.IncludeProviderEpisodeSuggestions &&
            string.Equals(entry.CaseType, "provider_diagnostics_episode", StringComparison.Ordinal))
        {
            return false;
        }

        if (settings.OnlyShowPassingOrImprovedReuseCases &&
            !IsPassingOrImprovedCase(entry))
        {
            return false;
        }

        if (query.ArtifactPaths.Any(path => string.Equals(path, entry.PrimaryArtifactPath, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !string.Equals(NormalizeSearchText(query.QueryText), NormalizeSearchText(entry.SearchText), StringComparison.Ordinal);
    }

    private static bool HasMatchingValue(
        IReadOnlyList<SemanticReuseMetadataField> signals,
        IReadOnlyList<SemanticReuseMetadataField> metadata,
        params string[] names)
    {
        foreach (var name in names)
        {
            var signalValues = signals
                .Where(signal => string.Equals(signal.Name, name, StringComparison.Ordinal))
                .SelectMany(signal => SplitMetadataValues(signal.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (signalValues.Count == 0)
                continue;

            var metadataValues = metadata
                .Where(field => string.Equals(field.Name, name, StringComparison.Ordinal))
                .SelectMany(field => SplitMetadataValues(field.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (metadataValues.Overlaps(signalValues))
                return true;
        }

        return false;
    }

    private static bool HasExactLinkedHistory(SemanticReuseQuery query, SemanticReuseIndexedCase entry)
    {
        var preferredSourceRunIds = (query.PreferredSourceRunIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (preferredSourceRunIds.Count > 0 &&
            !string.IsNullOrWhiteSpace(entry.SourceRunId) &&
            preferredSourceRunIds.Contains(entry.SourceRunId))
        {
            return true;
        }

        var entryPaths = entry.ArtifactLinks
            .Select(link => link.Path)
            .Append(entry.PrimaryArtifactPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        foreach (var queryPath in query.ArtifactPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (var entryPath in entryPaths)
            {
                if (PathsMatchOrContain(queryPath, entryPath))
                    return true;
            }
        }

        return false;
    }

    private static bool IsPassingOrImprovedCase(SemanticReuseIndexedCase entry)
        => entry.CaseType switch
        {
            "generated_output_pattern" => string.Equals(entry.Outcome, "passed", StringComparison.Ordinal),
            "repair_bundle_summary" => string.Equals(entry.Outcome, "improved", StringComparison.Ordinal)
                || string.Equals(entry.Outcome, "passed", StringComparison.Ordinal),
            "repair_promotion_outcome" => string.Equals(entry.Outcome, "improved", StringComparison.Ordinal)
                || string.Equals(entry.Outcome, "passed", StringComparison.Ordinal),
            "validation_failure_record" => string.Equals(entry.Outcome, "passed_on_retry", StringComparison.Ordinal),
            _ => false
        };

    private static string BuildUsefulnessSummary(SemanticReuseUsefulnessLedger ledger, string documentId)
    {
        var matches = ledger.Entries
            .Where(entry => string.Equals(entry.DocumentId, documentId, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.RecordedUtc)
            .ToArray();
        if (matches.Length == 0)
            return string.Empty;

        var cleanPasses = matches.Count(entry => string.Equals(entry.OutcomeClassification, "passed", StringComparison.Ordinal));
        var retryPasses = matches.Count(entry => string.Equals(entry.OutcomeClassification, "passed_on_retry", StringComparison.Ordinal));
        var improved = matches.Count(entry => string.Equals(entry.OutcomeClassification, "improved", StringComparison.Ordinal));
        var neutral = matches.Count(entry => string.Equals(entry.OutcomeClassification, "unchanged", StringComparison.Ordinal));
        var negative = matches.Count(entry => string.Equals(entry.OutcomeClassification, "regressed", StringComparison.Ordinal));
        return $"Follow-on evidence: clean pass {cleanPasses}, passed on retry {retryPasses}, improved {improved}, unchanged {neutral}, regressed {negative}.";
    }

    private static bool IsPositivePlaybookOutcome(string outcomeClassification)
        => string.Equals(outcomeClassification, "passed", StringComparison.Ordinal)
            || string.Equals(outcomeClassification, "passed_on_retry", StringComparison.Ordinal)
            || string.Equals(outcomeClassification, "improved", StringComparison.Ordinal);

    private static string BuildPlaybookKey(SemanticReuseIndexedCase entry, SemanticReuseUsefulnessEvidence evidence)
    {
        var contextKind = string.IsNullOrWhiteSpace(evidence.ContextKind) ? "general" : evidence.ContextKind;
        var primarySignal = contextKind switch
        {
            "provider_diagnostics" => $"{GetMetadataValue(entry.Metadata, "provider_name")}|{GetMetadataValue(entry.Metadata, "provider_classification")}",
            "planning" => $"{GetMetadataValue(entry.Metadata, "project_name")}|{GetMetadataValue(entry.Metadata, "source_path")}",
            _ => $"{GetMetadataValue(entry.Metadata, "failing_stage")}|{GetMetadataValue(entry.Metadata, "changed_file_names")}|{GetMetadataValue(entry.Metadata, "project_name")}"
        };

        if (string.IsNullOrWhiteSpace(primarySignal) || string.Equals(primarySignal, "|", StringComparison.Ordinal))
            primarySignal = $"{entry.CaseType}|{NormalizeSearchText(entry.Title)}";

        return $"{contextKind}|{ClassifyPlaybook(entry, evidence)}|{NormalizeSearchText(primarySignal)}";
    }

    private static SemanticReusePlaybook? BuildPlaybook(
        string key,
        IGrouping<string, SemanticReusePlaybookCandidate> group,
        int minimumEvidenceCount)
    {
        var items = group.ToArray();
        if (items.Length == 0)
            return null;

        var distinctEvidenceCount = items
            .Select(item => $"{item.Evidence.DocumentId}|{item.Evidence.ValidationRunId}|{item.Evidence.RepairId}|{item.Evidence.EvidenceArtifactPath}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctEvidenceCount < minimumEvidenceCount)
            return null;

        var exemplar = items
            .OrderByDescending(item => item.Evidence.RecordedUtc)
            .ThenBy(item => item.IndexedCase.DocumentId, StringComparer.Ordinal)
            .First();
        var contextKind = string.IsNullOrWhiteSpace(exemplar.Evidence.ContextKind) ? "general" : exemplar.Evidence.ContextKind;
        var playbookClass = ClassifyPlaybook(exemplar.IndexedCase, exemplar.Evidence);
        var confidence = ClassifyPlaybookConfidence(distinctEvidenceCount, minimumEvidenceCount);
        var outcomeCounts = items
            .GroupBy(item => item.Evidence.OutcomeClassification, StringComparer.Ordinal)
            .OrderBy(grouping => grouping.Key, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.Count(), StringComparer.Ordinal);
        var matchMetadata = BuildPlaybookMatchMetadata(exemplar.IndexedCase, exemplar.Evidence);
        var sourceDocumentIds = items
            .Select(item => item.IndexedCase.DocumentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var linkedArtifactPaths = items
            .SelectMany(item => item.IndexedCase.ArtifactLinks.Select(link => link.Path))
            .Concat(items.Select(item => item.IndexedCase.PrimaryArtifactPath))
            .Concat(items.SelectMany(item => item.Evidence.LinkedArtifactPaths ?? Array.Empty<string>()))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var evidenceArtifactPaths = items
            .Select(item => item.Evidence.EvidenceArtifactPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var outcomeClassifications = items
            .Select(item => item.Evidence.OutcomeClassification)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var title = BuildPlaybookTitle(contextKind, exemplar.IndexedCase, matchMetadata);
        var summary = BuildPlaybookSummary(contextKind, distinctEvidenceCount, outcomeCounts, matchMetadata);
        var explanation = BuildPlaybookExplanation(contextKind, playbookClass, matchMetadata, outcomeCounts);

        return new SemanticReusePlaybook(
            ComputeDeterministicHash($"{key}|{string.Join("|", sourceDocumentIds)}"),
            contextKind,
            playbookClass,
            title,
            summary,
            explanation,
            confidence,
            distinctEvidenceCount,
            matchMetadata,
            sourceDocumentIds,
            linkedArtifactPaths,
            evidenceArtifactPaths,
            outcomeClassifications,
            DateTimeOffset.UtcNow);
    }

    private static string ClassifyPlaybook(SemanticReuseIndexedCase entry, SemanticReuseUsefulnessEvidence evidence)
        => (string.IsNullOrWhiteSpace(evidence.ContextKind) ? "general" : evidence.ContextKind) switch
        {
            "planning" => "common_generation_validation_path",
            "validation_failure" => "common_validation_failure_response",
            "repair_bundle_reference" => "common_repair_review_path",
            "provider_diagnostics" => "provider_outage_retry_handling",
            _ => $"{entry.CaseType}_playbook"
        };

    private static string ClassifyPlaybookConfidence(int evidenceCount, int minimumEvidenceCount)
        => evidenceCount switch
        {
            _ when evidenceCount <= minimumEvidenceCount => "tentative",
            _ when evidenceCount <= minimumEvidenceCount + 2 => "corroborated",
            _ => "trusted"
        };

    private static int MapPlaybookConfidence(string confidence)
        => confidence switch
        {
            "trusted" => 3,
            "corroborated" => 2,
            "tentative" => 1,
            _ => 0
        };

    private static IReadOnlyList<SemanticReuseMetadataField> BuildPlaybookMatchMetadata(
        SemanticReuseIndexedCase entry,
        SemanticReuseUsefulnessEvidence evidence)
    {
        var names = (string.IsNullOrWhiteSpace(evidence.ContextKind) ? "general" : evidence.ContextKind) switch
        {
            "planning" => new[] { "project_name", "source_path" },
            "provider_diagnostics" => new[] { "provider_name", "provider_classification" },
            "repair_bundle_reference" => new[] { "failing_stage", "changed_file_names", "project_name" },
            _ => new[] { "failing_stage", "failing_test_name", "project_name" }
        };

        return entry.Metadata
            .Where(field => names.Contains(field.Name, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(field.Value))
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .Select(group => group.OrderBy(field => field.Value, StringComparer.Ordinal).First())
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildPlaybookTitle(
        string contextKind,
        SemanticReuseIndexedCase entry,
        IReadOnlyList<SemanticReuseMetadataField> matchMetadata)
    {
        var stage = GetMetadataValue(matchMetadata, "failing_stage");
        var project = GetMetadataValue(matchMetadata, "project_name");
        var provider = GetMetadataValue(matchMetadata, "provider_name");
        var classification = GetMetadataValue(matchMetadata, "provider_classification");
        return contextKind switch
        {
            "planning" => $"Planning pattern: {FirstNonEmpty(project, entry.Title, "validated output")}",
            "provider_diagnostics" => $"Provider handling: {FirstNonEmpty(provider, classification, entry.Title)}",
            "repair_bundle_reference" => $"Repair review path: {FirstNonEmpty(stage, entry.Title, "repair bundle")}",
            _ => $"Validation failure response: {FirstNonEmpty(stage, project, entry.Title)}"
        };
    }

    private static string BuildPlaybookSummary(
        string contextKind,
        int evidenceCount,
        IReadOnlyDictionary<string, int> outcomeCounts,
        IReadOnlyList<SemanticReuseMetadataField> matchMetadata)
    {
        var detail = contextKind switch
        {
            "planning" => $"for project {FirstNonEmpty(GetMetadataValue(matchMetadata, "project_name"), "current scope")}",
            "provider_diagnostics" => $"for provider {FirstNonEmpty(GetMetadataValue(matchMetadata, "provider_name"), GetMetadataValue(matchMetadata, "provider_classification"), "issue")}",
            _ => $"for stage {FirstNonEmpty(GetMetadataValue(matchMetadata, "failing_stage"), "current failure")}"
        };
        return $"Evidence-backed {detail}: {evidenceCount} corroborating outcome(s); {BuildOutcomeCountSummary(outcomeCounts)}.";
    }

    private static string BuildPlaybookExplanation(
        string contextKind,
        string playbookClass,
        IReadOnlyList<SemanticReuseMetadataField> matchMetadata,
        IReadOnlyDictionary<string, int> outcomeCounts)
    {
        var signal = contextKind switch
        {
            "planning" => FirstNonEmpty(GetMetadataValue(matchMetadata, "project_name"), "the same planning scope"),
            "provider_diagnostics" => FirstNonEmpty(GetMetadataValue(matchMetadata, "provider_classification"), GetMetadataValue(matchMetadata, "provider_name"), "the same provider issue"),
            "repair_bundle_reference" => FirstNonEmpty(GetMetadataValue(matchMetadata, "failing_stage"), "the same repair stage"),
            _ => FirstNonEmpty(GetMetadataValue(matchMetadata, "failing_stage"), "the same failing stage")
        };
        return playbookClass switch
        {
            "provider_outage_retry_handling" => $"Repeated provider episodes matching {signal} later produced {BuildOutcomeCountSummary(outcomeCounts)}.",
            "common_generation_validation_path" => $"Repeated planning suggestions for {signal} later produced {BuildOutcomeCountSummary(outcomeCounts)}.",
            "common_repair_review_path" => $"Repeated repair references for {signal} later produced {BuildOutcomeCountSummary(outcomeCounts)}.",
            _ => $"Repeated validation guidance for {signal} later produced {BuildOutcomeCountSummary(outcomeCounts)}."
        };
    }

    private static string BuildOutcomeCountSummary(IReadOnlyDictionary<string, int> outcomeCounts)
    {
        var parts = new List<string>();
        AppendOutcomeCount(parts, outcomeCounts, "passed", "clean pass");
        AppendOutcomeCount(parts, outcomeCounts, "passed_on_retry", "passed on retry");
        AppendOutcomeCount(parts, outcomeCounts, "improved", "improved repair");
        AppendOutcomeCount(parts, outcomeCounts, "unchanged", "unchanged");
        AppendOutcomeCount(parts, outcomeCounts, "regressed", "regressed");
        AppendOutcomeCount(parts, outcomeCounts, "failed", "failed");
        return parts.Count == 0 ? "no later outcomes" : string.Join(", ", parts);
    }

    private static void AppendOutcomeCount(ICollection<string> parts, IReadOnlyDictionary<string, int> outcomeCounts, string key, string label)
    {
        if (outcomeCounts.TryGetValue(key, out var count) && count > 0)
            parts.Add($"{label} {count}");
    }

    private static string GetMetadataValue(IReadOnlyList<SemanticReuseMetadataField> metadata, string name)
        => metadata
            .FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal))
            ?.Value
            ?? string.Empty;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool PathsMatchOrContain(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            return false;

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string>? paths)
        => (paths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> SplitMetadataValues(string value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

    private static string BuildRankingLabel(double score)
        => score switch
        {
            >= 0.80d => "High",
            >= 0.55d => "Medium",
            _ => "Related"
        };

    private static double ComputeTokenOverlap(string left, string right)
    {
        var leftTokens = Tokenize(left).ToHashSet(StringComparer.Ordinal);
        var rightTokens = Tokenize(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0d;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return intersection == 0 ? 0d : (double)intersection / Math.Max(leftTokens.Count, rightTokens.Count);
    }

    internal static IReadOnlyList<float> CreateVector(string text)
    {
        var vector = new float[EmbeddingDimensions];
        using var sha = SHA256.Create();
        foreach (var token in Tokenize(text))
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
            var index = hash[0] % EmbeddingDimensions;
            var sign = (hash[1] & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (magnitude <= double.Epsilon)
            return vector;

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }

        return vector;
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
            return 0d;

        double dot = 0d;
        double leftMagnitude = 0d;
        double rightMagnitude = 0d;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon)
            return 0d;

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in TokenPattern.Matches(text.ToLowerInvariant()))
        {
            var token = match.Value.Trim();
            if (token.Length >= 3)
                yield return token;
        }
    }

    private static string NormalizeSearchText(string text)
        => string.Join(' ', Tokenize(text));

    private static IReadOnlyList<SemanticReuseMetadataField> NormalizeMetadata(IEnumerable<SemanticReuseMetadataField> fields)
        => fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .Select(group => new SemanticReuseMetadataField(
                group.Key,
                string.Join("|", group
                    .SelectMany(field => SplitMetadataValues(field.Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<SemanticReuseArtifactLink> NormalizeArtifactLinks(IEnumerable<SemanticReuseArtifactLink> links)
        => links
            .Where(link => !string.IsNullOrWhiteSpace(link.Path))
            .GroupBy(link => link.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(link => link.Label, StringComparer.Ordinal)
                .First())
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .ThenBy(link => link.Path, StringComparer.Ordinal)
            .ToArray();

    private static string ComputeDeterministicHash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildDesignNote()
        => """
# Semantic Reuse Strategy

Shoots uses semantic reuse only for operator-visible suggestions. Deterministic run artifacts remain the authority for execution, validation, replay, repair, promotion, and release readiness.

## Approved Qdrant Uses
- Surface similar planning hints from prior generated outputs, validation failures, and repair outcomes.
- Retrieve similar validation failures.
- Retrieve prior repair bundles and repair promotions for similar failures.
- Retrieve similar provider diagnostics and retry outcomes.
- Retrieve replay divergence episodes.
- Retrieve baseline drift and regression summaries.

## Deferred Or Rejected Uses
- Qdrant does not decide execution results.
- Qdrant does not override validation outcomes or release readiness.
- Qdrant does not silently mutate code, plans, repairs, or repair bundles.
- Qdrant does not replace exact linked history or current run artifacts.

## Deterministic Safeguards
1. Current run artifacts are read first.
2. Exact linked history is read second.
3. Semantic suggestions are ranked after exact linkage and exact stage/failure matches.
4. If Qdrant is unavailable, Shoots falls back to deterministic local ranking without changing behavior.
5. Similar cases may suggest references, never auto-apply code or repair diffs.
6. Outcome learning and operator playbooks are derived only from recorded validation or repair artifacts.
7. Playbooks remain read-only operator guidance. They never trigger repairs, promotions, or baselines automatically.
""";

    private static string ResolveRepoRoot(string? repoRoot)
    {
        var current = string.IsNullOrWhiteSpace(repoRoot)
            ? AppContext.BaseDirectory
            : repoRoot;

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "Shoots.sln")))
                return Path.GetFullPath(current);

            current = Path.GetDirectoryName(current);
        }

        return Path.GetFullPath(repoRoot ?? Directory.GetCurrentDirectory());
    }

    private static T TryLoadArtifact<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var result = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions());
            return result is null ? fallback : result;
        }
        catch
        {
            return fallback;
        }
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
