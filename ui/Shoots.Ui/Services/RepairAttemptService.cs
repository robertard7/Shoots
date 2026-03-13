using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Services;

public sealed record RepairBundle(
    string RepairId,
    string RepoRoot,
    string TargetScopePath,
    string SourceRunId,
    string SourceRunPath,
    string ValidationRunId,
    string FailedStage,
    string FirstFailureExcerpt,
    string ValidationOutputFolder,
    string? FailureLogPath,
    IReadOnlyList<string> RelatedArtifactPaths,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<RepairReferenceCase>? ReferenceCases = null);

public sealed record RepairReferenceCase(
    string DocumentId,
    string ContextKind,
    string ContextLabel,
    string CaseType,
    string Title,
    string Outcome,
    string RankingLabel,
    string MatchExplanation,
    string SourceRunId,
    string PrimaryArtifactPath,
    IReadOnlyList<string> LinkedArtifactPaths,
    string UsefulnessSummary);

public sealed record RepairAttemptResult(
    string RepairId,
    string RepairFolder,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    string RepairOutcome,
    DateTimeOffset CompletedUtc);

public interface IRepairAttemptService
{
    string RepairsRoot { get; }

    Task<RepairAttemptResult> AttemptRepairAsync(RepairBundle bundle, CancellationToken ct = default);
}

public sealed class DeterministicRepairAttemptService : IRepairAttemptService
{
    public DeterministicRepairAttemptService(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentNullException(nameof(repoRoot));

        RepairsRoot = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs");
    }

    public string RepairsRoot { get; }

    public Task<RepairAttemptResult> AttemptRepairAsync(RepairBundle bundle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var repairFolder = Path.Combine(RepairsRoot, bundle.RepairId);
        Directory.CreateDirectory(repairFolder);
        var normalizedBundle = Normalize(bundle);

        File.WriteAllText(
            Path.Combine(repairFolder, "repair_bundle.json"),
            JsonSerializer.Serialize(normalizedBundle, JsonOptions()));

        var result = new RepairAttemptResult(
            normalizedBundle.RepairId,
            repairFolder,
            "No repair engine is configured. Failure context was captured without mutating files.",
            Array.Empty<string>(),
            "no_change",
            DateTimeOffset.UtcNow);

        File.WriteAllText(
            Path.Combine(repairFolder, "repair_result.json"),
            JsonSerializer.Serialize(result, JsonOptions()));

        File.WriteAllText(
            Path.Combine(repairFolder, "changed_files.json"),
            JsonSerializer.Serialize(result.ChangedFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray(), JsonOptions()));

        return Task.FromResult(result);
    }

    private static RepairBundle Normalize(RepairBundle bundle)
        => bundle with
        {
            RelatedArtifactPaths = (bundle.RelatedArtifactPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            ReferenceCases = (bundle.ReferenceCases ?? Array.Empty<RepairReferenceCase>())
                .Where(reference => !string.IsNullOrWhiteSpace(reference.DocumentId))
                .Select(reference => reference with
                {
                    LinkedArtifactPaths = (reference.LinkedArtifactPaths ?? Array.Empty<string>())
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray()
                })
                .OrderBy(reference => reference.DocumentId, StringComparer.Ordinal)
                .ThenBy(reference => reference.PrimaryArtifactPath, StringComparer.Ordinal)
                .ToArray()
        };

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
