using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderSimulationAccuracyRecord(
    string RecordId,
    string SimulationId,
    string DecisionId,
    string SimulationScenario,
    string TargetRoute,
    string FailureClass,
    string PredictedOutcome,
    string PredictedOutcomeClass,
    string ActualOutcome,
    string MatchType,
    double ConfidenceScore,
    bool AccuracyFlag,
    string ErrorClass,
    IReadOnlyList<string> ArtifactLinks,
    DateTimeOffset ObservedUtc)
{
    public string Summary
        => $"Predicted {FormatValue(PredictedOutcomeClass)} and observed {FormatValue(ActualOutcome)}. Match: {FormatValue(MatchType)}. Confidence: {ConfidenceScore:P0}. Error class: {FormatValue(ErrorClass)}.";

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderSimulationCalibrationRecord(
    string Dimension,
    string Key,
    string CalibratedConfidence,
    double HistoricalAccuracyRate,
    int SampleSize,
    string AccuracyIndicator,
    string Summary)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Key) ? "not recorded" : Key.Replace('_', ' ');
}

public sealed record BuilderSimulationAccuracyReport(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderSimulationAccuracyRecord> AccuracyRecords,
    IReadOnlyList<BuilderSimulationCalibrationRecord> SimulationTypeCalibration,
    IReadOnlyList<BuilderSimulationCalibrationRecord> RouteCalibration,
    IReadOnlyList<BuilderSimulationCalibrationRecord> FailureClassCalibration,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderSimulationAccuracyService
{
    public const string SimulationAccuracyFileName = "builder_simulation_accuracy.json";

    private const string SchemaVersion = "builder_simulation_accuracy.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string SimulationAccuracyPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), SimulationAccuracyFileName);

    public static BuilderSimulationAccuracyReport? LoadSimulationAccuracy(string repoRoot)
        => Load<BuilderSimulationAccuracyReport>(SimulationAccuracyPathForRepo(repoRoot));

    public static BuilderSimulationAccuracyReport? RefreshSimulationAccuracy(
        string repoRoot,
        BuilderRecoverySimulationsRecord? simulations = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        var decisions = BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        if (simulations is null && decisions is null)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var simulationIndex = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .Where(simulation => !string.IsNullOrWhiteSpace(simulation.SimulationId))
            .GroupBy(simulation => simulation.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var records = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(decision => !string.IsNullOrWhiteSpace(decision.SimulationId))
            .Select(decision => BuildAccuracyRecord(repoRoot, decision, simulationIndex))
            .Where(record => record is not null)
            .Select(record => record!)
            .OrderBy(record => record.ObservedUtc)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.RecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new BuilderSimulationAccuracyReport(
            workspaceId,
            SchemaVersion,
            records,
            BuildCalibration("simulation_type", records, record => record.SimulationScenario),
            BuildCalibration("route", records, record => record.TargetRoute),
            BuildCalibration("failure_class", records, record => record.FailureClass),
            true,
            BuildSummary(workspaceId, records),
            SimulationAccuracyPathForRepo(repoRoot),
            records.Length == 0 ? effectiveObservedUtc : records[^1].ObservedUtc);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderSimulationAccuracyRecord? BuildAccuracyRecord(
        string repoRoot,
        BuilderOperatorDecisionRecord decision,
        IReadOnlyDictionary<string, BuilderRecoverySimulationRecord> simulationIndex)
    {
        simulationIndex.TryGetValue(decision.SimulationId, out var simulation);
        var predictedOutcomeClass = NormalizeOutcomeClass(FirstNonEmpty(decision.PredictedOutcomeClass, simulation?.PredictedOutcomeClass));
        if (string.IsNullOrWhiteSpace(predictedOutcomeClass))
        {
            return null;
        }

        var actualOutcome = NormalizeOutcomeClass(decision.ResultState);
        if (string.IsNullOrWhiteSpace(actualOutcome))
        {
            return null;
        }

        var simulationScenario = FirstNonEmpty(decision.SimulationScenario, simulation?.Scenario);
        var targetRoute = FirstNonEmpty(decision.TargetRoute, simulation?.TargetRoute);
        var failureClass = FirstNonEmpty(decision.FailureClass, simulation?.FailureClass);
        var confidenceScore = ResolveConfidenceScore(decision, simulation);
        var matchType = DetermineMatchType(predictedOutcomeClass, actualOutcome);
        var accuracyFlag = string.Equals(matchType, "exact_match", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(matchType, "partial_match", StringComparison.OrdinalIgnoreCase);
        var errorClass = DetermineErrorClass(predictedOutcomeClass, actualOutcome, confidenceScore, accuracyFlag);
        var artifactLinks = decision.TriggerArtifacts
            .Concat(decision.ResultArtifacts)
            .Concat(new[]
            {
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var predictedOutcome = FirstNonEmpty(decision.PredictedOutcome, simulation?.PredictedOutcome);
        var recordId = ComputeDeterministicId(
            decision.SimulationId,
            decision.DecisionId,
            predictedOutcomeClass,
            actualOutcome,
            matchType,
            errorClass);

        return new BuilderSimulationAccuracyRecord(
            recordId,
            decision.SimulationId,
            decision.DecisionId,
            simulationScenario,
            targetRoute,
            failureClass,
            predictedOutcome,
            predictedOutcomeClass,
            actualOutcome,
            matchType,
            confidenceScore,
            accuracyFlag,
            errorClass,
            artifactLinks,
            decision.Timestamp);
    }

    private static IReadOnlyList<BuilderSimulationCalibrationRecord> BuildCalibration(
        string dimension,
        IReadOnlyList<BuilderSimulationAccuracyRecord> records,
        Func<BuilderSimulationAccuracyRecord, string> selector)
        => records
            .Select(record => new { Record = record, Key = selector(record) })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupedRecords = group.Select(entry => entry.Record).ToArray();
                var accuracyRate = groupedRecords.Length == 0
                    ? 0d
                    : groupedRecords.Count(entry => entry.AccuracyFlag) / (double)groupedRecords.Length;
                var calibratedConfidence = DetermineCalibratedConfidence(accuracyRate, groupedRecords.Length);
                var accuracyIndicator = calibratedConfidence switch
                {
                    "high" => "high_confidence",
                    "low" => "low_confidence",
                    _ => "unstable_confidence"
                };

                return new BuilderSimulationCalibrationRecord(
                    dimension,
                    group.Key,
                    calibratedConfidence,
                    accuracyRate,
                    groupedRecords.Length,
                    accuracyIndicator,
                    $"Historical accuracy is {accuracyRate:P0} across {groupedRecords.Length} comparison(s) for {FormatValue(group.Key)}.");
            })
            .ToArray();

    private static string DetermineMatchType(string predictedOutcomeClass, string actualOutcome)
    {
        if (string.Equals(predictedOutcomeClass, actualOutcome, StringComparison.OrdinalIgnoreCase))
        {
            return "exact_match";
        }

        if (IsProgressOutcome(predictedOutcomeClass) && IsProgressOutcome(actualOutcome))
        {
            return "partial_match";
        }

        if (IsFailureOutcome(predictedOutcomeClass) && IsFailureOutcome(actualOutcome))
        {
            return string.Equals(actualOutcome, "new_failure_pattern", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(predictedOutcomeClass, "new_failure_pattern", StringComparison.OrdinalIgnoreCase)
                ? "mismatch_new_failure"
                : "mismatch_same_class";
        }

        if (string.Equals(actualOutcome, "new_failure_pattern", StringComparison.OrdinalIgnoreCase))
        {
            return "mismatch_new_failure";
        }

        return "mismatch_same_class";
    }

    private static string DetermineErrorClass(
        string predictedOutcomeClass,
        string actualOutcome,
        double confidenceScore,
        bool accuracyFlag)
    {
        if (accuracyFlag)
        {
            return confidenceScore <= 0.4d && IsProgressOutcome(actualOutcome)
                ? "underconfidence"
                : "none";
        }

        if (IsProgressOutcome(predictedOutcomeClass) && IsFailureOutcome(actualOutcome))
        {
            return "incorrect_success_prediction";
        }

        if (IsFailureOutcome(predictedOutcomeClass) && IsProgressOutcome(actualOutcome))
        {
            return confidenceScore <= 0.4d ? "underconfidence" : "incorrect_failure_prediction";
        }

        return confidenceScore >= 0.75d ? "overconfidence" : "incorrect_failure_prediction";
    }

    private static string DetermineCalibratedConfidence(double accuracyRate, int sampleSize)
    {
        if (sampleSize < 3)
        {
            return "unstable";
        }

        return accuracyRate switch
        {
            >= 0.75d => "high",
            < 0.45d => "low",
            _ => "unstable"
        };
    }

    private static double ResolveConfidenceScore(
        BuilderOperatorDecisionRecord decision,
        BuilderRecoverySimulationRecord? simulation)
    {
        if (decision.PredictedConfidenceScore > 0d)
        {
            return decision.PredictedConfidenceScore;
        }

        if (simulation is not null && simulation.ConfidenceScore > 0d)
        {
            return simulation.ConfidenceScore;
        }

        return ConfidenceScoreFromLevel(FirstNonEmpty(decision.PredictedConfidenceLevel, simulation?.ConfidenceLevel));
    }

    private static double ConfidenceScoreFromLevel(string confidenceLevel)
        => confidenceLevel.Trim().ToLowerInvariant() switch
        {
            "high" => 0.85d,
            "medium" => 0.60d,
            "low" => 0.35d,
            _ => 0.15d
        };

    private static bool IsProgressOutcome(string outcome)
        => string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "partial_success", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "resolved_block", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailureOutcome(string outcome)
        => string.Equals(outcome, "failed_same_pattern", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(outcome, "new_failure_pattern", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeOutcomeClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "success" => normalized,
            "partial_success" => normalized,
            "resolved_block" => normalized,
            "failed_same_pattern" => normalized,
            "new_failure_pattern" => normalized,
            _ => string.Empty
        };
    }

    private static string BuildSummary(string workspaceId, IReadOnlyList<BuilderSimulationAccuracyRecord> records)
        => records.Count == 0
            ? $"No simulation accuracy comparisons are currently recorded for {workspaceId}. Calibration remains advisory only."
            : $"Recorded {records.Count} simulation accuracy comparison(s) for {workspaceId}. Exact or partial matches: {records.Count(record => record.AccuracyFlag)}.";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"accuracy-{hash[..10]}";
    }

    private static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
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
            return default;
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

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());
}
