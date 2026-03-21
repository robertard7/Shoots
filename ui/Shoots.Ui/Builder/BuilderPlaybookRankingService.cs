using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderPlaybookRankingRecord(
    string PlaybookId,
    double RankingScore,
    double IntentAdjustedScore,
    string SelectedIntent,
    double IntentAlignmentScore,
    IReadOnlyList<string> BestForIntents,
    string IntentReason,
    double ConfidenceWeight,
    double AccuracyWeight,
    double OutcomeWeight,
    int SampleSize,
    int RankingPosition,
    IReadOnlyList<string> EvidenceLinks,
    double AccuracyScore,
    double OutcomeSuccessScore,
    double StabilityScore,
    double RiskPenalty,
    double HistoricalAccuracyRate,
    double OutcomeSuccessRate,
    double FailureRecurrenceRate,
    double CrossRepoSuccessRate,
    string ConfidenceIndicator,
    IReadOnlyList<string> Breakdown,
    string ReasoningSummary)
{
    public string Summary
        => string.IsNullOrWhiteSpace(SelectedIntent)
            ? $"Rank #{RankingPosition} with base score {RankingScore:0.##}. Accuracy weight {AccuracyWeight:P0}, outcome weight {OutcomeWeight:P0}, confidence weight {ConfidenceWeight:P0}, sample size {SampleSize}."
            : $"Rank #{RankingPosition} with base score {RankingScore:0.##} and intent-adjusted score {IntentAdjustedScore:0.##} for {BuilderOperatorIntentService.GetIntentLabel(SelectedIntent)}. Accuracy weight {AccuracyWeight:P0}, outcome weight {OutcomeWeight:P0}, confidence weight {ConfidenceWeight:P0}, sample size {SampleSize}.";
}

public sealed record BuilderPlaybookRankingsRecord(
    string WorkspaceId,
    string SchemaVersion,
    string SelectedIntent,
    IReadOnlyList<BuilderPlaybookRankingRecord> Rankings,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderPlaybookRankingService
{
    public const string PlaybookRankingsFileName = "builder_playbook_rankings.json";

    private const string SchemaVersion = "builder_playbook_rankings.v2";
    private const double AccuracyScoreWeight = 0.30d;
    private const double OutcomeScoreWeight = 0.30d;
    private const double ConfidenceScoreWeight = 0.20d;
    private const double StabilityScoreWeight = 0.10d;
    private const double CoordinationScoreWeight = 0.10d;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string PlaybookRankingsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), PlaybookRankingsFileName);

    public static BuilderPlaybookRankingsRecord? LoadPlaybookRankings(string repoRoot)
        => Load<BuilderPlaybookRankingsRecord>(PlaybookRankingsPathForRepo(repoRoot));

    public static BuilderPlaybookRankingsRecord? RefreshPlaybookRankings(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderFailurePatternsRecord? failurePatterns = null,
        BuilderRouteRiskWarningsRecord? riskWarnings = null,
        BuilderExecutionPatternsRecord? executionPatterns = null,
        BuilderOperatorIntentRecord? operatorIntent = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        if (playbooks is null)
        {
            return null;
        }

        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        failurePatterns ??= BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
        riskWarnings ??= BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        executionPatterns ??= BuilderKnowledgeGraphService.LoadExecutionPatterns(repoRoot);
        operatorIntent ??= BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        var selectedIntent = operatorIntent?.Intent ?? string.Empty;

        var simulationIndex = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var scenarioCalibrationIndex = (accuracy?.SimulationTypeCalibration ?? Array.Empty<BuilderSimulationCalibrationRecord>())
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var rankings = playbooks.Playbooks
            .Select(playbook => BuildRanking(
                repoRoot,
                playbooks,
                playbook,
                simulationIndex,
                scenarioCalibrationIndex,
                accuracy,
                decisions,
                failurePatterns,
                riskWarnings,
                executionPatterns,
                selectedIntent))
            .OrderByDescending(ranking => ranking.IntentAdjustedScore)
            .ThenByDescending(ranking => ranking.RankingScore)
            .ThenByDescending(ranking => ranking.SampleSize)
            .ThenBy(ranking => ranking.ConfidenceIndicator, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ranking => ranking.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .Select((ranking, index) => ranking with { RankingPosition = index + 1 })
            .ToArray();

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var report = new BuilderPlaybookRankingsRecord(
            playbooks.WorkspaceId,
            SchemaVersion,
            selectedIntent,
            rankings,
            true,
            rankings.Length == 0
                ? $"No evidence-weighted playbook rankings are currently recorded for {playbooks.WorkspaceId}."
                : string.IsNullOrWhiteSpace(selectedIntent)
                    ? $"Generated evidence-weighted ranking for {rankings.Length} playbook(s) in {playbooks.WorkspaceId} using fixed ranking formula v2."
                    : $"Generated evidence-weighted ranking for {rankings.Length} playbook(s) in {playbooks.WorkspaceId} aligned to {BuilderOperatorIntentService.GetIntentLabel(selectedIntent)} using fixed ranking formula v2.",
            PlaybookRankingsPathForRepo(repoRoot),
            effectiveObservedUtc);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderPlaybookRankingRecord BuildRanking(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord playbookArtifact,
        BuilderRecoveryPlaybookRecord playbook,
        IReadOnlyDictionary<string, BuilderRecoverySimulationRecord> simulationIndex,
        IReadOnlyDictionary<string, BuilderSimulationCalibrationRecord> scenarioCalibrationIndex,
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderFailurePatternsRecord? failurePatterns,
        BuilderRouteRiskWarningsRecord? riskWarnings,
        BuilderExecutionPatternsRecord? executionPatterns,
        string selectedIntent)
    {
        var playbookSimulations = playbook.SimulationIds
            .Select(simulationId => simulationIndex.TryGetValue(simulationId, out var simulation) ? simulation : null)
            .Where(simulation => simulation is not null)
            .Select(simulation => simulation!)
            .ToArray();
        var scenarios = playbookSimulations
            .Select(simulation => simulation.Scenario)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matchingAccuracyRecords = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .Where(record => playbook.SimulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matchingDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(record => string.Equals(record.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matchingPatternRecords = playbookArtifact.FailurePatterns
            .Where(pattern => playbook.TriggerPatternIds.Contains(pattern.PatternId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matchingFailureEntries = (failurePatterns?.Entries ?? Array.Empty<BuilderFailurePatternRecord>())
            .Where(entry =>
                playbook.AppliesToRoutes.Count == 0
                    ? string.Equals(entry.Workspace, playbook.RepoScope, StringComparison.OrdinalIgnoreCase)
                    : playbook.AppliesToRoutes.Contains(entry.RouteAttempted, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matchingWarnings = (riskWarnings?.Entries ?? Array.Empty<BuilderRouteRiskWarningEntryRecord>())
            .Where(entry => playbook.AppliesToRoutes.Contains(entry.RouteAttempted, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matchingExecutionPatterns = (executionPatterns?.Entries ?? Array.Empty<BuilderExecutionPatternRecord>())
            .Where(entry =>
                playbook.CrossRepoScope
                    ? entry.WorkspaceSequence.Count > 1
                    : entry.WorkspaceSequence.Count >= 1)
            .ToArray();

        var historicalAccuracyRate = matchingAccuracyRecords.Length == 0
            ? ResolveFallbackAccuracyRate(scenarios, scenarioCalibrationIndex)
            : matchingAccuracyRecords.Count(record => record.AccuracyFlag) / (double)matchingAccuracyRecords.Length;
        var accuracyWeight = Clamp01(historicalAccuracyRate);

        var outcomeSuccessScore = ResolveOutcomeSuccessScore(matchingDecisions);
        var outcomeSuccessRate = ResolveOutcomeSuccessRate(matchingDecisions);
        var outcomeWeight = matchingDecisions.Length == 0 ? 0.50d : outcomeSuccessScore;

        var predictedConfidence = ResolvePredictedConfidence(playbookSimulations, matchingDecisions);
        var calibratedConfidence = ResolveCalibratedConfidence(scenarios, scenarioCalibrationIndex, predictedConfidence);
        var confidenceWeight = Clamp01(Math.Round((predictedConfidence + calibratedConfidence) / 2d, 4));

        var recurrenceCount = matchingPatternRecords.Sum(record => Math.Max(record.HistoricalOccurrenceCount, 1)) +
                              matchingFailureEntries.Length;
        var stabilityScore = Math.Round(1d / (1d + 0.35d * Math.Max(recurrenceCount, 0)), 4);
        var failureRecurrenceRate = Math.Round(recurrenceCount / (double)(recurrenceCount + 2), 4);

        var crossRepoSuccessRate = ResolveCrossRepoSuccessRate(playbook.CrossRepoScope, matchingDecisions, matchingExecutionPatterns);
        var riskPenalty = ResolveRiskPenalty(matchingWarnings.Length, failureRecurrenceRate, playbook.CrossRepoScope, crossRepoSuccessRate);

        var rankingScore = Math.Round(Clamp01(
                AccuracyScoreWeight * accuracyWeight +
                OutcomeScoreWeight * outcomeWeight +
                ConfidenceScoreWeight * confidenceWeight +
                StabilityScoreWeight * stabilityScore +
                CoordinationScoreWeight * crossRepoSuccessRate -
                riskPenalty) * 100d,
            2);

        var evidenceLinks = new[]
            {
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoRoot),
                BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
                BuilderKnowledgeGraphService.ExecutionPatternsPathForRepo(repoRoot)
            }
            .Concat(playbook.ArtifactLinks)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in matchingDecisions)
        {
            evidenceIds.Add(decision.DecisionId);
        }

        foreach (var accuracyRecord in matchingAccuracyRecords)
        {
            evidenceIds.Add(accuracyRecord.RecordId);
        }

        foreach (var pattern in matchingPatternRecords)
        {
            evidenceIds.Add(pattern.PatternId);
        }

        foreach (var warning in matchingWarnings)
        {
            evidenceIds.Add($"{warning.Workspace}|{warning.RouteAttempted}|{warning.WarningReason}|{warning.RelatedKnowledgeGraphNode}");
        }

        foreach (var executionPattern in matchingExecutionPatterns)
        {
            evidenceIds.Add(executionPattern.PatternId);
        }

        var confidenceIndicator = ResolveConfidenceIndicator(accuracyWeight, outcomeWeight, confidenceWeight, evidenceIds.Count);
        var breakdown = BuildBreakdown(
            historicalAccuracyRate,
            predictedConfidence,
            calibratedConfidence,
            outcomeSuccessRate,
            stabilityScore,
            failureRecurrenceRate,
            crossRepoSuccessRate,
            matchingWarnings.Length,
            riskPenalty,
            evidenceIds.Count);
        var bestForIntents = ResolveBestForIntents(playbook, scenarios);
        var intentAlignment = ResolveIntentAlignment(
            selectedIntent,
            playbook,
            scenarios,
            bestForIntents,
            accuracyWeight,
            outcomeWeight,
            confidenceWeight,
            stabilityScore,
            riskPenalty,
            crossRepoSuccessRate);
        var intentAdjustedScore = string.IsNullOrWhiteSpace(selectedIntent)
            ? rankingScore
            : Math.Round(Clamp01((rankingScore + ((intentAlignment.Score - 50d) * 0.20d)) / 100d) * 100d, 2);
        breakdown = breakdown
            .Concat(string.IsNullOrWhiteSpace(selectedIntent)
                ? Array.Empty<string>()
                : new[]
                {
                    $"Intent alignment for {BuilderOperatorIntentService.GetIntentLabel(selectedIntent)}: {intentAlignment.Score:0.##}.",
                    $"Intent alignment reason: {intentAlignment.Reason}"
                })
            .ToArray();

        return new BuilderPlaybookRankingRecord(
            playbook.PlaybookId,
            rankingScore,
            intentAdjustedScore,
            selectedIntent,
            intentAlignment.Score,
            bestForIntents,
            intentAlignment.Reason,
            confidenceWeight,
            accuracyWeight,
            outcomeWeight,
            evidenceIds.Count,
            0,
            evidenceLinks,
            accuracyWeight,
            outcomeSuccessScore,
            stabilityScore,
            riskPenalty,
            historicalAccuracyRate,
            outcomeSuccessRate,
            failureRecurrenceRate,
            crossRepoSuccessRate,
            confidenceIndicator,
            breakdown,
            BuildReasoningSummary(rankingScore, intentAdjustedScore, selectedIntent, confidenceIndicator, breakdown));
    }

    private static double ResolveFallbackAccuracyRate(
        IReadOnlyList<string> scenarios,
        IReadOnlyDictionary<string, BuilderSimulationCalibrationRecord> scenarioCalibrationIndex)
    {
        var calibrations = scenarios
            .Select(scenario => scenarioCalibrationIndex.TryGetValue(scenario, out var calibration) ? calibration : null)
            .Where(calibration => calibration is not null)
            .Select(calibration => calibration!.HistoricalAccuracyRate)
            .ToArray();
        return calibrations.Length == 0 ? 0.50d : Clamp01(Math.Round(calibrations.Average(), 4));
    }

    private static double ResolveOutcomeSuccessScore(IReadOnlyList<BuilderOperatorDecisionRecord> decisions)
    {
        if (decisions.Count == 0)
        {
            return 0.50d;
        }

        return Clamp01(Math.Round(decisions.Average(decision => decision.ResultState switch
        {
            "success" => 1.00d,
            "resolved_block" => 0.95d,
            "partial_success" => 0.75d,
            "failed_same_pattern" => 0.15d,
            "new_failure_pattern" => 0.05d,
            _ => 0.40d
        }), 4));
    }

    private static double ResolveOutcomeSuccessRate(IReadOnlyList<BuilderOperatorDecisionRecord> decisions)
    {
        if (decisions.Count == 0)
        {
            return 0.50d;
        }

        return Clamp01(Math.Round(decisions.Count(decision =>
            string.Equals(decision.ResultState, "success", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.ResultState, "partial_success", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.ResultState, "resolved_block", StringComparison.OrdinalIgnoreCase)) / (double)decisions.Count, 4));
    }

    private static double ResolvePredictedConfidence(
        IReadOnlyList<BuilderRecoverySimulationRecord> simulations,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions)
    {
        var simulationConfidence = simulations
            .Where(simulation => simulation.ConfidenceScore > 0d)
            .Select(simulation => simulation.ConfidenceScore)
            .ToArray();
        var decisionConfidence = decisions
            .Where(decision => decision.PredictedConfidenceScore > 0d)
            .Select(decision => decision.PredictedConfidenceScore)
            .ToArray();

        if (simulationConfidence.Length == 0 && decisionConfidence.Length == 0)
        {
            return 0.50d;
        }

        return Clamp01(Math.Round(simulationConfidence.Concat(decisionConfidence).Average(), 4));
    }

    private static double ResolveCalibratedConfidence(
        IReadOnlyList<string> scenarios,
        IReadOnlyDictionary<string, BuilderSimulationCalibrationRecord> scenarioCalibrationIndex,
        double fallback)
    {
        var weights = scenarios
            .Select(scenario => scenarioCalibrationIndex.TryGetValue(scenario, out var calibration)
                ? ConfidenceWeightFromIndicator(calibration.AccuracyIndicator)
                : double.NaN)
            .Where(value => !double.IsNaN(value))
            .ToArray();

        return weights.Length == 0
            ? fallback
            : Clamp01(Math.Round(weights.Average(), 4));
    }

    private static double ResolveCrossRepoSuccessRate(
        bool isCrossRepo,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions,
        IReadOnlyList<BuilderExecutionPatternRecord> executionPatterns)
    {
        if (!isCrossRepo)
        {
            return 0.50d;
        }

        var successfulDecisions = decisions.Count(decision =>
            string.Equals(decision.ResultState, "success", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.ResultState, "resolved_block", StringComparison.OrdinalIgnoreCase));
        var coordinationSuccesses = executionPatterns.Count(pattern => pattern.WorkspaceSequence.Count > 1);
        var sample = decisions.Count + coordinationSuccesses;
        if (sample == 0)
        {
            return 0.50d;
        }

        return Clamp01(Math.Round((successfulDecisions + coordinationSuccesses) / (double)sample, 4));
    }

    private static double ResolveRiskPenalty(int warningCount, double failureRecurrenceRate, bool isCrossRepo, double crossRepoSuccessRate)
    {
        var warningPenalty = Math.Min(warningCount * 0.08d, 0.24d);
        var failurePenalty = Math.Min(Math.Max(0d, failureRecurrenceRate - 0.40d) * 0.20d, 0.12d);
        var coordinationPenalty = isCrossRepo && crossRepoSuccessRate < 0.50d ? 0.05d : 0d;
        return Clamp01(Math.Round(Math.Min(warningPenalty + failurePenalty + coordinationPenalty, 0.35d), 4));
    }

    private static string ResolveConfidenceIndicator(double accuracyWeight, double outcomeWeight, double confidenceWeight, int sampleSize)
    {
        if (sampleSize < 3)
        {
            return "unstable_confidence";
        }

        if (accuracyWeight >= 0.75d && outcomeWeight >= 0.65d && confidenceWeight >= 0.60d)
        {
            return "high_confidence";
        }

        if (accuracyWeight < 0.45d || outcomeWeight < 0.45d)
        {
            return "low_confidence";
        }

        return "unstable_confidence";
    }

    private static IReadOnlyList<string> BuildBreakdown(
        double historicalAccuracyRate,
        double predictedConfidence,
        double calibratedConfidence,
        double outcomeSuccessRate,
        double stabilityScore,
        double failureRecurrenceRate,
        double crossRepoSuccessRate,
        int warningCount,
        double riskPenalty,
        int sampleSize)
        => new[]
        {
            $"Historical accuracy rate: {historicalAccuracyRate:P0}.",
            $"Predicted confidence weight: {predictedConfidence:P0}.",
            $"Calibrated confidence weight: {calibratedConfidence:P0}.",
            $"Operator outcome success rate: {outcomeSuccessRate:P0}.",
            $"Stability score from failure recurrence: {stabilityScore:P0}.",
            $"Failure recurrence rate: {failureRecurrenceRate:P0}.",
            $"Cross-repo coordination success rate: {crossRepoSuccessRate:P0}.",
            $"Active risk warnings contributing penalty: {warningCount}.",
            $"Risk penalty applied: {riskPenalty:P0}.",
            $"Evidence sample size: {sampleSize}."
        };

    private static IReadOnlyList<string> ResolveBestForIntents(
        BuilderRecoveryPlaybookRecord playbook,
        IReadOnlyList<string> scenarios)
    {
        var intents = new List<string>();

        switch (playbook.FailureClass)
        {
            case "patch_rejected":
                intents.Add(BuilderOperatorIntentService.MinimalChangeIntent);
                intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
                break;
            case "review_blocked":
                intents.Add(BuilderOperatorIntentService.FastRecoveryIntent);
                intents.Add(BuilderOperatorIntentService.MinimalChangeIntent);
                break;
            case "route_failed":
                intents.Add(BuilderOperatorIntentService.FastRecoveryIntent);
                intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
                break;
            case "finalize_blocked":
                intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
                intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
                break;
            case "orchestration_blocked":
                intents.Add(BuilderOperatorIntentService.UnblockOrchestrationIntent);
                intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
                break;
            case "cross_repo_dependency_block":
                intents.Add(BuilderOperatorIntentService.UnblockOrchestrationIntent);
                intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
                break;
            case "repeated_failure_pattern":
                intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
                intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
                break;
            case "high_risk_change_stalled":
                intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
                intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
                break;
        }

        if (scenarios.Contains("retry_same_route", StringComparer.OrdinalIgnoreCase))
        {
            intents.Add(BuilderOperatorIntentService.FastRecoveryIntent);
        }

        if (scenarios.Contains("reduce_scope", StringComparer.OrdinalIgnoreCase))
        {
            intents.Add(BuilderOperatorIntentService.MinimalChangeIntent);
        }

        if (scenarios.Contains("switch_route_manual", StringComparer.OrdinalIgnoreCase))
        {
            intents.Add(BuilderOperatorIntentService.FullResolutionIntent);
        }

        if (scenarios.Contains("staged_orchestration", StringComparer.OrdinalIgnoreCase))
        {
            intents.Add(BuilderOperatorIntentService.UnblockOrchestrationIntent);
        }

        if (scenarios.Contains("isolate_high_risk_files", StringComparer.OrdinalIgnoreCase) ||
            playbook.RequiresApprovalGate)
        {
            intents.Add(BuilderOperatorIntentService.SafeRecoveryIntent);
        }

        if (!playbook.CrossRepoScope)
        {
            intents.Add(BuilderOperatorIntentService.MinimalChangeIntent);
        }

        return intents
            .Where(BuilderOperatorIntentService.IsSupportedIntent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(IntentRank)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderIntentAlignment ResolveIntentAlignment(
        string selectedIntent,
        BuilderRecoveryPlaybookRecord playbook,
        IReadOnlyList<string> scenarios,
        IReadOnlyList<string> bestForIntents,
        double accuracyWeight,
        double outcomeWeight,
        double confidenceWeight,
        double stabilityScore,
        double riskPenalty,
        double crossRepoSuccessRate)
    {
        if (string.IsNullOrWhiteSpace(selectedIntent) || !BuilderOperatorIntentService.IsSupportedIntent(selectedIntent))
        {
            return new BuilderIntentAlignment(0d, "No explicit operator intent is currently recorded.");
        }

        var inverseRisk = Clamp01(1d - riskPenalty);
        var bestIntentBonus = bestForIntents.Contains(selectedIntent, StringComparer.OrdinalIgnoreCase) ? 0.10d : 0d;
        double score;
        string reason;
        switch (selectedIntent)
        {
            case BuilderOperatorIntentService.FastRecoveryIntent:
                score = Clamp01(
                    0.35d * outcomeWeight +
                    0.20d * confidenceWeight +
                    0.15d * inverseRisk +
                    0.15d * (scenarios.Contains("retry_same_route", StringComparer.OrdinalIgnoreCase) || scenarios.Contains("reduce_scope", StringComparer.OrdinalIgnoreCase) ? 1d : 0.20d) +
                    0.15d * (playbook.CrossRepoScope ? 0.20d : 1d) +
                    bestIntentBonus);
                reason = $"Favours fast recovery through {(playbook.CrossRepoScope ? "cross-repo coordination" : "workspace-local scope")}, route retry/reduce-scope scenarios, and outcome evidence {outcomeWeight:P0}.";
                break;
            case BuilderOperatorIntentService.SafeRecoveryIntent:
                score = Clamp01(
                    0.30d * accuracyWeight +
                    0.20d * confidenceWeight +
                    0.20d * inverseRisk +
                    0.15d * stabilityScore +
                    0.15d * ((playbook.RequiresReviewGate || playbook.RequiresApprovalGate) ? 1d : 0.35d) +
                    bestIntentBonus);
                reason = $"Favours safe recovery through accuracy {accuracyWeight:P0}, low risk penalty {(1d - inverseRisk):P0}, and gate-aware review scope.";
                break;
            case BuilderOperatorIntentService.MinimalChangeIntent:
                score = Clamp01(
                    0.30d * (scenarios.Contains("reduce_scope", StringComparer.OrdinalIgnoreCase) ? 1d : 0.20d) +
                    0.20d * (playbook.CrossRepoScope ? 0.10d : 1d) +
                    0.15d * (playbook.AppliesToRoutes.Count <= 1 ? 1d : 0.35d) +
                    0.15d * inverseRisk +
                    0.10d * outcomeWeight +
                    0.10d * stabilityScore +
                    bestIntentBonus);
                reason = $"Favours minimal change through reduced-scope scenarios, {(playbook.CrossRepoScope ? "cross-repo breadth penalty" : "workspace-local scope")}, and limited route spread.";
                break;
            case BuilderOperatorIntentService.FullResolutionIntent:
                score = Clamp01(
                    0.25d * accuracyWeight +
                    0.20d * outcomeWeight +
                    0.20d * stabilityScore +
                    0.15d * (scenarios.Contains("switch_route_manual", StringComparer.OrdinalIgnoreCase) || scenarios.Contains("staged_orchestration", StringComparer.OrdinalIgnoreCase) ? 1d : 0.30d) +
                    0.10d * inverseRisk +
                    0.10d * ((playbook.RequiresFinalizeGate || playbook.CrossRepoScope) ? 1d : 0.40d) +
                    bestIntentBonus);
                reason = $"Favours full resolution through broader corrective scenarios, stability {stabilityScore:P0}, and finalize-ready remediation scope.";
                break;
            case BuilderOperatorIntentService.UnblockOrchestrationIntent:
                score = Clamp01(
                    0.35d * (playbook.CrossRepoScope ? 1d : 0.10d) +
                    0.25d * crossRepoSuccessRate +
                    0.20d * (scenarios.Contains("staged_orchestration", StringComparer.OrdinalIgnoreCase) ? 1d : 0.20d) +
                    0.10d * outcomeWeight +
                    0.10d * inverseRisk +
                    bestIntentBonus);
                reason = $"Favours orchestration unblock through cross-repo scope, coordination success {crossRepoSuccessRate:P0}, and staged recovery scenarios.";
                break;
            default:
                score = 0d;
                reason = "No explicit operator intent is currently recorded.";
                break;
        }

        return new BuilderIntentAlignment(Math.Round(score * 100d, 2), reason);
    }

    private static string BuildReasoningSummary(double rankingScore, double intentAdjustedScore, string selectedIntent, string confidenceIndicator, IReadOnlyList<string> breakdown)
        => string.IsNullOrWhiteSpace(selectedIntent)
            ? $"Evidence-weighted ranking score {rankingScore:0.##} with {FormatToken(confidenceIndicator)}. {string.Join(" ", breakdown)}"
            : $"Evidence-weighted ranking score {rankingScore:0.##}, intent-adjusted to {intentAdjustedScore:0.##} for {BuilderOperatorIntentService.GetIntentLabel(selectedIntent)}, with {FormatToken(confidenceIndicator)}. {string.Join(" ", breakdown)}";

    private static int IntentRank(string intent)
        => intent switch
        {
            BuilderOperatorIntentService.FastRecoveryIntent => 0,
            BuilderOperatorIntentService.SafeRecoveryIntent => 1,
            BuilderOperatorIntentService.MinimalChangeIntent => 2,
            BuilderOperatorIntentService.FullResolutionIntent => 3,
            BuilderOperatorIntentService.UnblockOrchestrationIntent => 4,
            _ => 5
        };

    private static double ConfidenceWeightFromIndicator(string indicator)
        => indicator switch
        {
            "high_confidence" => 0.90d,
            "low_confidence" => 0.25d,
            _ => 0.55d
        };

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

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

    private sealed record BuilderIntentAlignment(double Score, string Reason);
}
