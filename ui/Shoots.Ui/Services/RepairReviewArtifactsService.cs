using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Services;

public sealed record RepairComparisonRecord(
    string RepairId,
    string SourceValidationRunId,
    string SourceValidationStatus,
    string SourceValidationSummary,
    string SourceFailedStage,
    string SourceFirstFailureExcerpt,
    string RepairedValidationRunId,
    string RepairedValidationStatus,
    string RepairedValidationSummary,
    string RepairedFailedStage,
    string RepairedFirstFailureExcerpt,
    string ImprovementState,
    IReadOnlyList<string> ChangedFiles,
    string RepairSummary,
    string RepairBundlePath,
    string RepairResultFolder,
    string LinkedValidationRunFolder,
    DateTimeOffset RecordedUtc);

public sealed record RepairHistoryEntry(
    string RepairId,
    DateTimeOffset AttemptedUtc,
    string SourceValidationRunId,
    string RepairedValidationRunId,
    string RepairOutcome,
    string ImprovementState,
    string Summary,
    string RepairBundlePath,
    string RepairResultFolder,
    string LinkedValidationRunFolder,
    string ComparisonPath);

public sealed record RepairHistoryLedger(
    IReadOnlyList<RepairHistoryEntry> Attempts);

public sealed record RepairPromotionRecord(
    string SourceRunId,
    string SourceRunPath,
    string RepairId,
    string SourceValidationRunId,
    string RepairedValidationRunId,
    string ImprovementState,
    string ConfidenceSignal,
    string ConfidenceText,
    string Status,
    string Reason,
    string AdoptionState,
    string AdoptionReason,
    string OperatorNote,
    string RepairBundlePath,
    string RepairResultFolder,
    string LinkedValidationRunFolder,
    string AuditSummaryFolder,
    string AuditSummaryJsonPath,
    string AuditSummaryMarkdownPath,
    DateTimeOffset PromotedUtc,
    DateTimeOffset StateUpdatedUtc);

public sealed record PromotedRepairLedgerEntry(
    string SourceRunId,
    string SourceRunPath,
    string RepairId,
    DateTimeOffset PromotedUtc,
    string ImprovementState,
    string ConfidenceSignal,
    string ConfidenceText,
    IReadOnlyList<string> PromotedArtifactPaths,
    string OperatorNote);

public sealed record PromotedRepairLedger(
    IReadOnlyList<PromotedRepairLedgerEntry> Entries);

public sealed record RepairAuditSummaryRecord(
    string SourceRunId,
    string SourceRunPath,
    string SourceValidationRunId,
    string RepairedValidationRunId,
    string RepairId,
    string OriginalFailureStage,
    string OriginalFailureExcerpt,
    string RepairOutcome,
    string PromotionStatus,
    string PromotionReason,
    string ConfidenceSignal,
    string ConfidenceText,
    string AdoptionState,
    string AdoptionReason,
    string OperatorNote,
    IReadOnlyList<string> LinkedArtifactPaths,
    DateTimeOffset RecordedUtc);

public static class RepairReviewArtifactsService
{
    public const string HistoryFileName = "repair_history.json";
    public const string PromotionFileName = "repair_promotion.json";
    public const string ComparisonFileName = "repair_comparison.json";
    public const string PromotionLedgerFileName = "promoted_repair_ledger.json";
    public const string AuditSummaryJsonFileName = "repair_audit_summary.json";
    public const string AuditSummaryMarkdownFileName = "repair_audit_summary.md";

    public static string HistoryPathForRun(string runPath)
        => Path.Combine(runPath, HistoryFileName);

    public static string PromotionPathForRun(string runPath)
        => Path.Combine(runPath, PromotionFileName);

    public static string ComparisonPathForRepair(string repairFolder)
        => Path.Combine(repairFolder, ComparisonFileName);

    public static string PromotionLedgerPathForRepo(string repoRoot)
        => Path.Combine(repoRoot, ".codex", "validation-ui", PromotionLedgerFileName);

    public static string AuditSummaryFolderForRepair(string repairFolder)
        => Path.Combine(repairFolder, "audit");

    public static RepairHistoryLedger LoadHistory(string runPath)
    {
        var path = HistoryPathForRun(runPath);
        if (!File.Exists(path))
            return EmptyHistory();

        try
        {
            var history = JsonSerializer.Deserialize<RepairHistoryLedger>(File.ReadAllText(path), JsonOptions());
            return history ?? EmptyHistory();
        }
        catch
        {
            return EmptyHistory();
        }
    }

    public static RepairComparisonRecord? LoadComparison(string comparisonPath)
    {
        if (string.IsNullOrWhiteSpace(comparisonPath) || !File.Exists(comparisonPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RepairComparisonRecord>(File.ReadAllText(comparisonPath), JsonOptions());
        }
        catch
        {
            return null;
        }
    }

    public static RepairPromotionRecord? LoadPromotion(string runPath)
    {
        var path = PromotionPathForRun(runPath);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RepairPromotionRecord>(File.ReadAllText(path), JsonOptions());
        }
        catch
        {
            return null;
        }
    }

    public static PromotedRepairLedger LoadPromotionLedger(string repoRoot)
    {
        var path = PromotionLedgerPathForRepo(repoRoot);
        if (!File.Exists(path))
            return EmptyLedger();

        try
        {
            var ledger = JsonSerializer.Deserialize<PromotedRepairLedger>(File.ReadAllText(path), JsonOptions());
            return ledger ?? EmptyLedger();
        }
        catch
        {
            return EmptyLedger();
        }
    }

    public static void SaveComparison(RepairComparisonRecord comparison)
    {
        if (comparison is null)
            throw new ArgumentNullException(nameof(comparison));

        Directory.CreateDirectory(comparison.RepairResultFolder);
        File.WriteAllText(
            ComparisonPathForRepair(comparison.RepairResultFolder),
            JsonSerializer.Serialize(
                comparison with
                {
                    ChangedFiles = comparison.ChangedFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray()
                },
                JsonOptions()));
    }

    public static RepairHistoryLedger AppendHistory(string runPath, RepairHistoryEntry entry, int keepLast)
    {
        if (entry is null)
            throw new ArgumentNullException(nameof(entry));

        var existing = LoadHistory(runPath);
        var attempts = existing.Attempts
            .Append(entry)
            .OrderByDescending(item => item.AttemptedUtc)
            .ThenByDescending(item => item.RepairId, StringComparer.Ordinal)
            .Take(Math.Max(1, keepLast))
            .ToArray();

        var next = new RepairHistoryLedger(attempts);
        Directory.CreateDirectory(runPath);
        File.WriteAllText(HistoryPathForRun(runPath), JsonSerializer.Serialize(next, JsonOptions()));
        return next;
    }

    public static void SavePromotion(string runPath, RepairPromotionRecord promotion)
    {
        if (promotion is null)
            throw new ArgumentNullException(nameof(promotion));

        Directory.CreateDirectory(runPath);
        File.WriteAllText(PromotionPathForRun(runPath), JsonSerializer.Serialize(promotion, JsonOptions()));
    }

    public static PromotedRepairLedger AppendPromotionLedger(string repoRoot, PromotedRepairLedgerEntry entry, int keepLast)
    {
        if (entry is null)
            throw new ArgumentNullException(nameof(entry));

        var existing = LoadPromotionLedger(repoRoot);
        var entries = existing.Entries
            .Append(entry with
            {
                PromotedArtifactPaths = entry.PromotedArtifactPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
            })
            .OrderByDescending(item => item.PromotedUtc)
            .ThenByDescending(item => item.RepairId, StringComparer.Ordinal)
            .Take(Math.Max(1, keepLast))
            .ToArray();

        var next = new PromotedRepairLedger(entries);
        var path = PromotionLedgerPathForRepo(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(next, JsonOptions()));
        return next;
    }

    public static RepairPromotionRecord WriteAuditSummary(RepairComparisonRecord comparison, RepairPromotionRecord promotion)
    {
        if (comparison is null)
            throw new ArgumentNullException(nameof(comparison));
        if (promotion is null)
            throw new ArgumentNullException(nameof(promotion));

        var auditFolder = AuditSummaryFolderForRepair(promotion.RepairResultFolder);
        Directory.CreateDirectory(auditFolder);

        var jsonPath = Path.Combine(auditFolder, AuditSummaryJsonFileName);
        var markdownPath = Path.Combine(auditFolder, AuditSummaryMarkdownFileName);
        var linkedArtifactPaths = BuildPromotedArtifactPaths(promotion)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var summary = new RepairAuditSummaryRecord(
            promotion.SourceRunId,
            promotion.SourceRunPath,
            promotion.SourceValidationRunId,
            promotion.RepairedValidationRunId,
            promotion.RepairId,
            comparison.SourceFailedStage,
            comparison.SourceFirstFailureExcerpt,
            comparison.ImprovementState,
            promotion.Status,
            promotion.Reason,
            promotion.ConfidenceSignal,
            promotion.ConfidenceText,
            promotion.AdoptionState,
            promotion.AdoptionReason,
            promotion.OperatorNote,
            linkedArtifactPaths,
            promotion.StateUpdatedUtc);

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, JsonOptions()));
        File.WriteAllText(markdownPath, BuildAuditMarkdown(summary));

        return promotion with
        {
            AuditSummaryFolder = auditFolder,
            AuditSummaryJsonPath = jsonPath,
            AuditSummaryMarkdownPath = markdownPath
        };
    }

    public static RepairPromotionRecord CreatePromotion(
        string sourceRunId,
        string sourceRunPath,
        RepairHistoryEntry historyEntry,
        RepairComparisonRecord comparison,
        string reason,
        string operatorNote,
        DateTimeOffset promotedUtc)
    {
        if (historyEntry is null)
            throw new ArgumentNullException(nameof(historyEntry));
        if (comparison is null)
            throw new ArgumentNullException(nameof(comparison));

        var improvementState = comparison.ImprovementState;
        return new RepairPromotionRecord(
            sourceRunId,
            sourceRunPath,
            historyEntry.RepairId,
            historyEntry.SourceValidationRunId,
            historyEntry.RepairedValidationRunId,
            improvementState,
            DetermineConfidenceSignal(improvementState),
            DetermineConfidenceText(improvementState),
            "promoted_from_repair",
            reason,
            "promoted_only",
            "Promoted repair is recorded but not yet adopted into the current working output.",
            NormalizeNote(operatorNote),
            historyEntry.RepairBundlePath,
            historyEntry.RepairResultFolder,
            historyEntry.LinkedValidationRunFolder,
            string.Empty,
            string.Empty,
            string.Empty,
            promotedUtc,
            promotedUtc);
    }

    public static RepairPromotionRecord UpdateAdoptionState(
        RepairPromotionRecord promotion,
        string adoptionState,
        string adoptionReason,
        string operatorNote,
        DateTimeOffset updatedUtc)
    {
        if (promotion is null)
            throw new ArgumentNullException(nameof(promotion));

        return promotion with
        {
            AdoptionState = adoptionState,
            AdoptionReason = adoptionReason,
            OperatorNote = NormalizeNote(operatorNote),
            StateUpdatedUtc = updatedUtc
        };
    }

    public static IReadOnlyList<string> BuildPromotedArtifactPaths(RepairPromotionRecord promotion)
    {
        if (promotion is null)
            throw new ArgumentNullException(nameof(promotion));

        return new[]
        {
            promotion.RepairBundlePath,
            promotion.RepairResultFolder,
            promotion.LinkedValidationRunFolder,
            promotion.AuditSummaryJsonPath,
            promotion.AuditSummaryMarkdownPath,
            promotion.AuditSummaryFolder
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    public static bool CanPromote(string improvementState)
        => string.Equals(improvementState, "improved", StringComparison.Ordinal)
            || string.Equals(improvementState, "passed", StringComparison.Ordinal);

    public static string DetermineConfidenceSignal(string improvementState)
        => improvementState switch
        {
            "passed" => "passed_validation",
            "improved" => "improved_validation",
            "unchanged" => "unchanged_validation",
            "regressed" => "regressed_validation",
            _ => "unknown_validation_signal"
        };

    public static string DetermineConfidenceText(string improvementState)
        => improvementState switch
        {
            "passed" => "Passed validation after repair.",
            "improved" => "Validation improved after repair.",
            "unchanged" => "Validation did not improve after repair.",
            "regressed" => "Validation regressed after repair.",
            _ => "Validation confidence could not be classified."
        };

    private static string NormalizeNote(string operatorNote)
        => string.IsNullOrWhiteSpace(operatorNote)
            ? string.Empty
            : operatorNote.Trim();

    private static PromotedRepairLedger EmptyLedger()
        => new(Array.Empty<PromotedRepairLedgerEntry>());

    private static RepairHistoryLedger EmptyHistory()
        => new(Array.Empty<RepairHistoryEntry>());

    private static string BuildAuditMarkdown(RepairAuditSummaryRecord summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Repair Audit Summary");
        builder.AppendLine();
        builder.AppendLine($"- Source run: `{summary.SourceRunId}`");
        builder.AppendLine($"- Source validation run: `{summary.SourceValidationRunId}`");
        builder.AppendLine($"- Repaired validation run: `{summary.RepairedValidationRunId}`");
        builder.AppendLine($"- Repair attempt: `{summary.RepairId}`");
        builder.AppendLine($"- Repair outcome: `{summary.RepairOutcome}`");
        builder.AppendLine($"- Promotion status: `{summary.PromotionStatus}`");
        builder.AppendLine($"- Promotion reason: {summary.PromotionReason}");
        builder.AppendLine($"- Adoption state: `{summary.AdoptionState}`");
        builder.AppendLine($"- Adoption reason: {summary.AdoptionReason}");
        builder.AppendLine($"- Confidence: `{summary.ConfidenceSignal}`");
        builder.AppendLine($"- Confidence text: {summary.ConfidenceText}");
        builder.AppendLine($"- Original failure stage: `{summary.OriginalFailureStage}`");
        builder.AppendLine($"- Original failure excerpt: {summary.OriginalFailureExcerpt}");
        if (!string.IsNullOrWhiteSpace(summary.OperatorNote))
        {
            builder.AppendLine($"- Operator note: {summary.OperatorNote}");
        }

        builder.AppendLine();
        builder.AppendLine("## Linked Artifacts");
        foreach (var path in summary.LinkedArtifactPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            builder.AppendLine($"- `{path}`");
        }

        return builder.ToString();
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
