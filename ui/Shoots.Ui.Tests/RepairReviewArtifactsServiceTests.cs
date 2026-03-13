using System;
using System.IO;
using System.Linq;
using Shoots.UI.Services;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class RepairReviewArtifactsServiceTests
{
    [Fact]
    public void Append_history_persists_and_prunes_to_keep_last_n()
    {
        var runPath = Path.Combine(Path.GetTempPath(), $"shoots-repair-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runPath);

        try
        {
            Append(runPath, "repair-001", DateTimeOffset.UtcNow.AddMinutes(-3));
            Append(runPath, "repair-002", DateTimeOffset.UtcNow.AddMinutes(-2));
            Append(runPath, "repair-003", DateTimeOffset.UtcNow.AddMinutes(-1));

            var history = RepairReviewArtifactsService.LoadHistory(runPath);
            Assert.Equal(2, history.Attempts.Count);
            Assert.Equal(new[] { "repair-003", "repair-002" }, history.Attempts.Select(item => item.RepairId).ToArray());
            Assert.True(File.Exists(RepairReviewArtifactsService.HistoryPathForRun(runPath)));
        }
        finally
        {
            Directory.Delete(runPath, recursive: true);
        }
    }

    [Fact]
    public void Comparison_and_promotion_round_trip_from_disk()
    {
        var runPath = Path.Combine(Path.GetTempPath(), $"shoots-repair-promotion-{Guid.NewGuid():N}");
        var repoRoot = Path.Combine(Path.GetTempPath(), $"shoots-repair-ledger-{Guid.NewGuid():N}");
        var repairFolder = Path.Combine(runPath, ".codex", "validation-ui", "repairs", "repair-001");
        var validationFolder = Path.Combine(runPath, ".codex", "validation-ui", "runs", "run-002");
        Directory.CreateDirectory(repairFolder);
        Directory.CreateDirectory(validationFolder);
        Directory.CreateDirectory(repoRoot);

        try
        {
            var comparison = new RepairComparisonRecord(
                "repair-001",
                "run-001",
                "failed",
                "Validation failed: Tests failed.",
                "Running UI tests",
                "Tests failed.",
                "run-002",
                "passed",
                "Validation passed (1 stage).",
                "Completed",
                string.Empty,
                "passed",
                new[] { Path.Combine(runPath, "src", "Generated.cs") },
                "Repair applied deterministic changes.",
                Path.Combine(repairFolder, "repair_bundle.json"),
                repairFolder,
                validationFolder,
                DateTimeOffset.UtcNow);
            RepairReviewArtifactsService.SaveComparison(comparison);

            var promotion = RepairReviewArtifactsService.CreatePromotion(
                "generated-run",
                runPath,
                new RepairHistoryEntry(
                    "repair-001",
                    DateTimeOffset.UtcNow,
                    "run-001",
                    "run-002",
                    "changed",
                    "passed",
                    "Repair applied deterministic changes.",
                    comparison.RepairBundlePath,
                    repairFolder,
                    validationFolder,
                    RepairReviewArtifactsService.ComparisonPathForRepair(repairFolder)),
                comparison,
                "Repair outcome passed.",
                string.Empty,
                DateTimeOffset.UtcNow);
            var audited = RepairReviewArtifactsService.WriteAuditSummary(comparison, promotion);
            RepairReviewArtifactsService.SavePromotion(runPath, audited);
            RepairReviewArtifactsService.AppendPromotionLedger(
                repoRoot,
                new PromotedRepairLedgerEntry(
                    audited.SourceRunId,
                    audited.SourceRunPath,
                    audited.RepairId,
                    audited.PromotedUtc,
                    audited.ImprovementState,
                    audited.ConfidenceSignal,
                    audited.ConfidenceText,
                    RepairReviewArtifactsService.BuildPromotedArtifactPaths(audited),
                    audited.OperatorNote),
                keepLast: 5);

            var loadedComparison = RepairReviewArtifactsService.LoadComparison(RepairReviewArtifactsService.ComparisonPathForRepair(repairFolder));
            var loadedPromotion = RepairReviewArtifactsService.LoadPromotion(runPath);
            var loadedLedger = RepairReviewArtifactsService.LoadPromotionLedger(repoRoot);

            Assert.NotNull(loadedComparison);
            Assert.NotNull(loadedPromotion);
            Assert.Equal("repair-001", loadedComparison!.RepairId);
            Assert.Equal("passed", loadedComparison.ImprovementState);
            Assert.Single(loadedComparison.ChangedFiles);
            Assert.Equal("promoted_from_repair", loadedPromotion!.Status);
            Assert.Equal("Repair outcome passed.", loadedPromotion.Reason);
            Assert.Equal("passed_validation", loadedPromotion.ConfidenceSignal);
            Assert.True(File.Exists(loadedPromotion.AuditSummaryJsonPath));
            Assert.True(File.Exists(loadedPromotion.AuditSummaryMarkdownPath));
            Assert.Single(loadedLedger.Entries);
        }
        finally
        {
            Directory.Delete(runPath, recursive: true);
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Confidence_classification_is_deterministic()
    {
        Assert.Equal("passed_validation", RepairReviewArtifactsService.DetermineConfidenceSignal("passed"));
        Assert.Equal("improved_validation", RepairReviewArtifactsService.DetermineConfidenceSignal("improved"));
        Assert.Equal("unchanged_validation", RepairReviewArtifactsService.DetermineConfidenceSignal("unchanged"));
        Assert.Equal("regressed_validation", RepairReviewArtifactsService.DetermineConfidenceSignal("regressed"));
        Assert.Equal("Validation improved after repair.", RepairReviewArtifactsService.DetermineConfidenceText("improved"));
    }

    [Fact]
    public void Promotion_ledger_prunes_to_keep_last_n()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"shoots-promotion-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            AppendLedger(repoRoot, "repair-001", DateTimeOffset.UtcNow.AddMinutes(-3));
            AppendLedger(repoRoot, "repair-002", DateTimeOffset.UtcNow.AddMinutes(-2));
            AppendLedger(repoRoot, "repair-003", DateTimeOffset.UtcNow.AddMinutes(-1));

            var ledger = RepairReviewArtifactsService.LoadPromotionLedger(repoRoot);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(new[] { "repair-003", "repair-002" }, ledger.Entries.Select(entry => entry.RepairId).ToArray());
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static void Append(string runPath, string repairId, DateTimeOffset attemptedUtc)
    {
        var repairFolder = Path.Combine(runPath, ".codex", "validation-ui", "repairs", repairId);
        var validationFolder = Path.Combine(runPath, ".codex", "validation-ui", "runs", $"{repairId}-validation");
        Directory.CreateDirectory(repairFolder);
        Directory.CreateDirectory(validationFolder);

        var comparison = new RepairComparisonRecord(
            repairId,
            $"source-{repairId}",
            "failed",
            "Validation failed.",
            "Running UI tests",
            "Tests failed.",
            $"repaired-{repairId}",
            "failed",
            "Validation failed again.",
            "Running smoke validation",
            "Smoke failed.",
            "improved",
            Array.Empty<string>(),
            "Repair captured context.",
            Path.Combine(repairFolder, "repair_bundle.json"),
            repairFolder,
            validationFolder,
            attemptedUtc);
        RepairReviewArtifactsService.SaveComparison(comparison);

        RepairReviewArtifactsService.AppendHistory(
            runPath,
            new RepairHistoryEntry(
                repairId,
                attemptedUtc,
                comparison.SourceValidationRunId,
                comparison.RepairedValidationRunId,
                "no_change",
                comparison.ImprovementState,
                comparison.RepairSummary,
                comparison.RepairBundlePath,
                repairFolder,
                validationFolder,
                RepairReviewArtifactsService.ComparisonPathForRepair(repairFolder)),
            keepLast: 2);
    }

    private static void AppendLedger(string repoRoot, string repairId, DateTimeOffset promotedUtc)
    {
        RepairReviewArtifactsService.AppendPromotionLedger(
            repoRoot,
            new PromotedRepairLedgerEntry(
                "run-001",
                repoRoot,
                repairId,
                promotedUtc,
                "passed",
                "passed_validation",
                "Passed validation after repair.",
                new[] { Path.Combine(repoRoot, repairId) },
                string.Empty),
            keepLast: 2);
    }
}
