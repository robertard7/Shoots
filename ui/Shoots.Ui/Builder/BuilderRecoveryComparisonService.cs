using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderRecoveryComparisonMetricRecord(
    string MetricId,
    string PlaybookId,
    string PlaybookTitle,
    string SimulationId,
    string Scenario,
    string BranchId,
    string BranchLabel,
    double ComparisonScore,
    double PredictedSuccessRate,
    string CalibratedConfidence,
    double HistoricalAccuracyRate,
    double HistoricalOutcomeSuccessRate,
    double RankingScore,
    double IntentAlignmentScore,
    string ConstraintCompatibility,
    string CalibrationProfile,
    string ScoreSummary,
    string SignalBalanceSummary,
    IReadOnlyList<BuilderSignalContributionRecord> SignalContributions,
    string RiskSummary,
    string ExpectedBlockingGate,
    string ConfidenceBand,
    IReadOnlyList<string> EvidenceLinks,
    string Summary);

public sealed record BuilderRecoveryComparisonTradeoffRecord(
    string TradeoffId,
    string Dimension,
    string ComparisonResult,
    string Explanation);

public sealed record BuilderRecoveryComparisonSetRecord(
    string ComparisonId,
    string BranchId,
    string BranchLabel,
    IReadOnlyList<string> PlaybookIds,
    IReadOnlyList<string> SimulationIds,
    IReadOnlyList<BuilderRecoveryComparisonMetricRecord> ComparisonMetrics,
    IReadOnlyList<BuilderRecoveryComparisonTradeoffRecord> Tradeoffs,
    string Summary);

public sealed record BuilderRecoveryComparisonsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderRecoveryComparisonSetRecord> ComparisonSets,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderRecoveryComparisonService
{
    public const string RecoveryComparisonsFileName = "builder_recovery_comparisons.json";

    private const string SchemaVersion = "builder_recovery_comparisons.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string RecoveryComparisonsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), RecoveryComparisonsFileName);

    public static BuilderRecoveryComparisonsRecord? LoadRecoveryComparisons(string repoRoot)
        => Load<BuilderRecoveryComparisonsRecord>(RecoveryComparisonsPathForRepo(repoRoot));

    public static BuilderRecoveryComparisonsRecord? RefreshRecoveryComparisons(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderPlaybookContextFiltersRecord? contextFilters = null,
        BuilderOperatorIntentRecord? operatorIntent = null,
        DateTimeOffset? observedUtc = null,
        int maxMetricsPerSet = 3,
        BuilderTrustIndexRecord? trust = null,
        BuilderPredictiveDriftReport? predictiveDrift = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderSignalCalibrationRecord? calibration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        if (playbooks is null || simulations is null)
        {
            return null;
        }

        rankings ??= BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot) ??
                     BuilderPlaybookRankingService.RefreshPlaybookRankings(repoRoot, playbooks, simulations);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot) ??
                    BuilderSimulationAccuracyService.RefreshSimulationAccuracy(repoRoot, simulations);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        contextFilters ??= BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot) ??
                          BuilderPlaybookContextFilterService.RefreshContextFilters(repoRoot, playbooks, rankings, decisions);
        operatorIntent ??= BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        trust ??= BuilderTrustIndexService.LoadTrustIndex(repoRoot);
        predictiveDrift ??= BuilderPredictiveDriftService.LoadPredictiveDrift(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        calibration ??= BuilderSignalCalibrationService.LoadSignalCalibration(repoRoot) ??
                        BuilderSignalCalibrationService.RefreshSignalCalibration(
                            repoRoot,
                            rankings,
                            contextFilters,
                            BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot),
                            accuracy,
                            decisions,
                            BuilderExecutionAuditService.LoadExecutionAudit(repoRoot),
                            guardrails,
                            operatorIntent,
                            observedUtc);

        var playbookIndex = playbooks.Playbooks
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rankingIndex = (rankings?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contextIndex = (contextFilters?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var scenarioCalibrationIndex = (accuracy?.SimulationTypeCalibration ?? Array.Empty<BuilderSimulationCalibrationRecord>())
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var decisionGroups = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SimulationId))
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var trustSimulationIndex = (trust?.TargetProfiles ?? Array.Empty<BuilderTrustTargetProfileRecord>())
            .Where(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.TrustScore).First(), StringComparer.OrdinalIgnoreCase);
        var predictiveSimulationIndex = (predictiveDrift?.Predictions ?? Array.Empty<BuilderPredictiveDriftRecord>())
            .Where(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => RiskRank(entry.RiskEscalation)).ThenByDescending(entry => entry.FailureProbability).First(), StringComparer.OrdinalIgnoreCase);
        var selectedIntent = operatorIntent?.Intent ?? string.Empty;

        var metrics = simulations.Simulations
            .Where(simulation => playbookIndex.ContainsKey(simulation.PlaybookId))
            .Select(simulation => BuildMetric(
                repoRoot,
                playbookIndex[simulation.PlaybookId],
                simulation,
                selectedIntent,
                rankingIndex.TryGetValue(simulation.PlaybookId, out var ranking) ? ranking : null,
                contextIndex.TryGetValue(simulation.PlaybookId, out var contextFilter) ? contextFilter : null,
                scenarioCalibrationIndex.TryGetValue(simulation.Scenario, out var scenarioCalibration) ? scenarioCalibration : null,
                decisionGroups.TryGetValue(simulation.SimulationId, out var simulationDecisions) ? simulationDecisions : Array.Empty<BuilderOperatorDecisionRecord>(),
                trustSimulationIndex.TryGetValue(simulation.SimulationId, out var trustProfile) ? trustProfile : null,
                predictiveSimulationIndex.TryGetValue(simulation.SimulationId, out var driftPrediction) ? driftPrediction : null,
                BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
                    guardrails,
                    simulation.PlaybookId,
                    simulation.SimulationId,
                    simulation.TargetRoute,
                    playbooks.WorkspaceId),
                calibration))
            .OrderByDescending(entry => entry.ComparisonScore)
            .ThenBy(entry => ConstraintCompatibilityRank(entry.ConstraintCompatibility))
            .ThenBy(entry => BranchRank(entry.BranchId, selectedIntent))
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var comparisonSets = BuildComparisonSets(metrics, selectedIntent, maxMetricsPerSet);
        var report = new BuilderRecoveryComparisonsRecord(
            playbooks.WorkspaceId,
            SchemaVersion,
            comparisonSets,
            true,
            comparisonSets.Count == 0
                ? $"No recovery comparisons are currently recorded for {playbooks.WorkspaceId}."
                : $"Generated {comparisonSets.Count} deterministic recovery comparison set(s) for {playbooks.WorkspaceId} using fixed comparison formula v1.",
            RecoveryComparisonsPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderRecoveryComparisonMetricRecord BuildMetric(
        string repoRoot,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderRecoverySimulationRecord simulation,
        string selectedIntent,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        BuilderSimulationCalibrationRecord? scenarioCalibration,
        IReadOnlyList<BuilderOperatorDecisionRecord> simulationDecisions,
        BuilderTrustTargetProfileRecord? trustProfile,
        BuilderPredictiveDriftRecord? predictiveDrift,
        IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrailMatches,
        BuilderSignalCalibrationRecord? calibration)
    {
        var branchId = ResolveBranchId(selectedIntent, playbook, simulation, ranking);
        var branchLabel = ResolveBranchLabel(branchId);
        var predictedSuccessRate = ResolvePredictedSuccessRate(simulation.SuccessLikelihood, simulation.ConfidenceScore);
        var historicalAccuracyRate = scenarioCalibration?.HistoricalAccuracyRate ?? ranking?.HistoricalAccuracyRate ?? 0.50d;
        var historicalOutcomeSuccessRate = simulationDecisions.Count == 0
            ? ranking?.OutcomeSuccessRate ?? 0.50d
            : simulationDecisions.Count(entry => entry.SuccessFlag) / (double)simulationDecisions.Count;
        var rankingScore = !string.IsNullOrWhiteSpace(selectedIntent)
            ? ranking?.IntentAdjustedScore ?? ranking?.RankingScore ?? contextFilter?.RelevanceScore ?? 0d
            : ranking?.RankingScore ?? contextFilter?.RelevanceScore ?? 0d;
        var intentAlignmentScore = ranking?.IntentAlignmentScore ?? 0d;
        var primaryGuardrail = guardrailMatches
            .OrderBy(record => RiskRank(record.RiskLevel))
            .ThenBy(record => record.TargetScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var constraintSignal = BuilderSignalCalibrationService.ResolveConstraintSignal(
            string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase));
        var trustSignal = trustProfile?.TrustScore
                          ?? Math.Round(((historicalAccuracyRate + historicalOutcomeSuccessRate) / 2d) * 100d, 2);
        var guardrailSignal = BuilderSignalCalibrationService.ResolveGuardrailSafetySignal(
            primaryGuardrail?.RiskLevel ?? simulation.RiskEscalation);
        var driftSignal = predictiveDrift is null
            ? BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                ResolveFailureLikelihoodProbability(simulation.FailureLikelihood),
                string.Empty)
            : BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                predictiveDrift.FailureProbability,
                predictiveDrift.DriftTrend);
        var calibrationEvaluation = BuilderSignalCalibrationService.EvaluateCompositeScore(
            calibration,
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.RankingSignalId,
                rankingScore,
                $"Ranking score {rankingScore:0.##} based on evidence-weighted ranking and comparison context."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.IntentSignalId,
                intentAlignmentScore,
                $"Intent alignment {intentAlignmentScore:0.##} for branch {branchLabel}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.ConstraintSignalId,
                constraintSignal,
                string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)
                    ? $"Constraint profile blocks this scenario via {string.Join(", ", simulation.BlockedByConstraints)}."
                    : "Constraint profile is compatible with this scenario."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.TrustSignalId,
                trustSignal,
                trustProfile is null
                    ? $"Fallback trust signal blends historical accuracy {historicalAccuracyRate:P0} and historical outcome success {historicalOutcomeSuccessRate:P0}."
                    : $"Trust profile {trustProfile.ConfidenceProfile} scores {trustProfile.TrustScore:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.GuardrailSignalId,
                guardrailSignal,
                $"Guardrail risk {FormatToken(primaryGuardrail?.RiskLevel ?? simulation.RiskEscalation)} maps to safety signal {guardrailSignal:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.DriftSignalId,
                driftSignal,
                predictiveDrift is null
                    ? $"Fallback drift signal uses failure likelihood {FormatToken(simulation.FailureLikelihood)}."
                    : $"Predictive drift {FormatToken(predictiveDrift.DriftTrend)} at {predictiveDrift.FailureProbability:P0} failure likelihood."));
        var comparisonScore = calibrationEvaluation.CompositeScore;
        var confidenceBand = scenarioCalibration?.CalibratedConfidence ?? simulation.ConfidenceLevel;
        var riskFlags = simulation.RiskFlags.Count == 0
            ? string.Empty
            : $" Flags: {string.Join(", ", simulation.RiskFlags.Select(FormatToken))}.";
        var scoreSummary = $"Comparison score {comparisonScore:0.##}. Predicted success {predictedSuccessRate:0.##}%. Historical success {historicalOutcomeSuccessRate:P0}. Historical accuracy {historicalAccuracyRate:P0}. Ranking signal {rankingScore:0.##}. Intent alignment {intentAlignmentScore:0.##}.";
        var riskSummary = $"Risk {FormatToken(simulation.RiskEscalation)}. Constraint compatibility: {FormatToken(simulation.ConstraintCompatibility)}.{riskFlags}";
        var evidenceLinks = new[]
            {
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot),
                BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot)
            }
            .Concat(playbook.ArtifactLinks)
            .Concat(simulation.ArtifactLinks)
            .Concat(guardrailMatches.SelectMany(record => record.EvidenceLinks))
            .Concat(trustProfile?.EvidenceLinks ?? Array.Empty<string>())
            .Concat(predictiveDrift?.LinkedArtifacts ?? Array.Empty<string>())
            .Concat(simulationDecisions.SelectMany(entry => entry.ResultArtifacts))
            .Concat(simulationDecisions.SelectMany(entry => entry.TriggerArtifacts))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderRecoveryComparisonMetricRecord(
            ComputeDeterministicId("metric", playbook.PlaybookId, simulation.SimulationId, branchId),
            playbook.PlaybookId,
            playbook.Title,
            simulation.SimulationId,
            simulation.Scenario,
            branchId,
            branchLabel,
            comparisonScore,
            predictedSuccessRate,
            scenarioCalibration?.CalibratedConfidence ?? "unstable",
            historicalAccuracyRate,
            historicalOutcomeSuccessRate,
            rankingScore,
            intentAlignmentScore,
            simulation.ConstraintCompatibility,
            calibration?.CalibrationProfile ?? "balanced",
            scoreSummary,
            calibrationEvaluation.Summary,
            calibrationEvaluation.Contributions,
            riskSummary,
            simulation.ExpectedNextBlockingGate,
            confidenceBand,
            evidenceLinks,
            $"{playbook.Title} / {FormatToken(simulation.Scenario)} scores {comparisonScore:0.##} with {FormatToken(confidenceBand)} confidence and {FormatToken(simulation.ConstraintCompatibility)} constraint status.");
    }

    private static IReadOnlyList<BuilderRecoveryComparisonSetRecord> BuildComparisonSets(
        IReadOnlyList<BuilderRecoveryComparisonMetricRecord> metrics,
        string selectedIntent,
        int maxMetricsPerSet)
    {
        if (metrics.Count == 0)
        {
            return Array.Empty<BuilderRecoveryComparisonSetRecord>();
        }

        var orderedMetrics = metrics
            .OrderBy(entry => ConstraintCompatibilityRank(entry.ConstraintCompatibility))
            .ThenByDescending(entry => entry.ComparisonScore)
            .ThenBy(entry => BranchRank(entry.BranchId, selectedIntent))
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var comparisonSets = new List<BuilderRecoveryComparisonSetRecord>
        {
            BuildComparisonSet(
                "all_candidates",
                "Current Focus Comparison",
                SelectRepresentativeMetrics(orderedMetrics, Math.Max(maxMetricsPerSet, 1), selectedIntent))
        };

        foreach (var branchGroup in metrics
                     .GroupBy(entry => entry.BranchId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => BranchRank(group.Key, selectedIntent))
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var branchMetrics = SelectRepresentativeMetrics(
                branchGroup.ToArray(),
                Math.Max(maxMetricsPerSet, 1),
                selectedIntent);
            comparisonSets.Add(BuildComparisonSet(branchGroup.Key, ResolveBranchLabel(branchGroup.Key), branchMetrics));
        }

        return comparisonSets
            .Where(entry => entry.ComparisonMetrics.Count > 0)
            .OrderBy(entry => entry.BranchId == "all_candidates" ? 0 : 1)
            .ThenBy(entry => BranchRank(entry.BranchId, selectedIntent))
            .ThenBy(entry => entry.BranchId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BuilderRecoveryComparisonMetricRecord> SelectRepresentativeMetrics(
        IReadOnlyList<BuilderRecoveryComparisonMetricRecord> metrics,
        int maxMetricsPerSet,
        string selectedIntent)
    {
        if (metrics.Count == 0)
        {
            return Array.Empty<BuilderRecoveryComparisonMetricRecord>();
        }

        var limit = Math.Max(maxMetricsPerSet, 1);
        var ordered = metrics
            .OrderBy(entry => ConstraintCompatibilityRank(entry.ConstraintCompatibility))
            .ThenByDescending(entry => entry.ComparisonScore)
            .ThenBy(entry => BranchRank(entry.BranchId, selectedIntent))
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = ordered
            .Take(limit)
            .ToList();
        var blockedRepresentative = ordered.FirstOrDefault(entry =>
            string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase));

        if (blockedRepresentative is not null &&
            selected.All(entry => !string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)))
        {
            if (selected.Count == limit)
            {
                selected[^1] = blockedRepresentative;
            }
            else
            {
                selected.Add(blockedRepresentative);
            }
        }

        return selected
            .DistinctBy(entry => entry.MetricId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => ConstraintCompatibilityRank(entry.ConstraintCompatibility))
            .ThenByDescending(entry => entry.ComparisonScore)
            .ThenBy(entry => BranchRank(entry.BranchId, selectedIntent))
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderRecoveryComparisonSetRecord BuildComparisonSet(
        string branchId,
        string branchLabel,
        IReadOnlyList<BuilderRecoveryComparisonMetricRecord> metrics)
    {
        var playbookIds = metrics.Select(entry => entry.PlaybookId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var simulationIds = metrics.Select(entry => entry.SimulationId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tradeoffs = BuildTradeoffs(metrics);
        return new BuilderRecoveryComparisonSetRecord(
            ComputeDeterministicId("comparison", branchId, string.Join("|", playbookIds), string.Join("|", simulationIds)),
            branchId,
            branchLabel,
            playbookIds,
            simulationIds,
            metrics,
            tradeoffs,
            $"{branchLabel} compares {metrics.Count} scenario(s) across {playbookIds.Length} playbook(s).");
    }

    private static IReadOnlyList<BuilderRecoveryComparisonTradeoffRecord> BuildTradeoffs(IReadOnlyList<BuilderRecoveryComparisonMetricRecord> metrics)
        => new[]
        {
            BuildTradeoff("speed", metrics, ResolveSpeedScore, "faster than"),
            BuildTradeoff("safety", metrics, ResolveSafetyScore, "safer than"),
            BuildTradeoff("scope", metrics, ResolveScopeScore, "broader in scope than"),
            BuildTradeoff("risk", metrics, ResolveRiskScore, "riskier than")
        }
        .OrderBy(entry => TradeoffRank(entry.Dimension))
        .ThenBy(entry => entry.TradeoffId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static BuilderRecoveryComparisonTradeoffRecord BuildTradeoff(
        string dimension,
        IReadOnlyList<BuilderRecoveryComparisonMetricRecord> metrics,
        Func<BuilderRecoveryComparisonMetricRecord, double> selector,
        string relationLabel)
    {
        if (metrics.Count == 0)
        {
            return new BuilderRecoveryComparisonTradeoffRecord(
                ComputeDeterministicId("tradeoff", dimension, "empty"),
                dimension,
                "No scenarios available.",
                "No deterministic comparison could be derived because no scenarios are present.");
        }

        var ordered = metrics
            .OrderByDescending(selector)
            .ThenByDescending(entry => entry.ComparisonScore)
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var first = ordered.First();
        var last = ordered.Last();
        if (ordered.Length == 1 || Math.Abs(selector(first) - selector(last)) < 0.01d)
        {
            return new BuilderRecoveryComparisonTradeoffRecord(
                ComputeDeterministicId("tradeoff", dimension, first.SimulationId, last.SimulationId),
                dimension,
                $"No material {dimension} difference detected.",
                $"{DescribeMetric(first)} is the only or effectively equivalent scenario for {dimension} in this comparison set.");
        }

        return new BuilderRecoveryComparisonTradeoffRecord(
            ComputeDeterministicId("tradeoff", dimension, first.SimulationId, last.SimulationId),
            dimension,
            $"{DescribeMetric(first)} is {relationLabel} {DescribeMetric(last)}.",
            $"{DescribeMetric(first)} scores {selector(first):0.##} on {dimension} while {DescribeMetric(last)} scores {selector(last):0.##}. Expected blocking gates remain {FormatToken(first.ExpectedBlockingGate)} and {FormatToken(last.ExpectedBlockingGate)}.");
    }

    private static string ResolveBranchId(
        string selectedIntent,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderRecoverySimulationRecord simulation,
        BuilderPlaybookRankingRecord? ranking)
    {
        if (!string.IsNullOrWhiteSpace(selectedIntent) &&
            ranking?.BestForIntents.Contains(selectedIntent, StringComparer.OrdinalIgnoreCase) == true &&
            ranking.IntentAlignmentScore >= 60d)
        {
            return selectedIntent;
        }

        return simulation.Scenario switch
        {
            "retry_same_route" => BuilderOperatorIntentService.FastRecoveryIntent,
            "reduce_scope" => BuilderOperatorIntentService.MinimalChangeIntent,
            "isolate_high_risk_files" => BuilderOperatorIntentService.SafeRecoveryIntent,
            "staged_orchestration" => BuilderOperatorIntentService.UnblockOrchestrationIntent,
            "switch_route_manual" when playbook.CrossRepoScope || playbook.RequiresFinalizeGate => BuilderOperatorIntentService.FullResolutionIntent,
            "switch_route_manual" => BuilderOperatorIntentService.SafeRecoveryIntent,
            _ => BuilderOperatorIntentService.FullResolutionIntent
        };
    }

    private static string ResolveBranchLabel(string branchId)
        => branchId switch
        {
            "all_candidates" => "Current Focus Comparison",
            var value when BuilderOperatorIntentService.IsSupportedIntent(value) => BuilderOperatorIntentService.GetIntentLabel(value),
            _ => FormatToken(branchId)
        };

    private static double ResolvePredictedSuccessRate(string likelihood, double confidenceScore)
        => likelihood switch
        {
            "high" => 80d,
            "medium" => 55d,
            "low" => 30d,
            _ => Math.Round(Clamp01(confidenceScore) * 100d, 2)
        };

    private static double ResolveFailureLikelihoodProbability(string failureLikelihood)
        => failureLikelihood switch
        {
            "high" => 0.80d,
            "medium" => 0.55d,
            "low" => 0.25d,
            _ => 0.40d
        };

    private static double ResolveRiskPenalty(BuilderRecoverySimulationRecord simulation)
    {
        var basePenalty = simulation.RiskEscalation switch
        {
            "high" => 22d,
            "medium" => 12d,
            _ => 4d
        };
        var constraintPenalty = string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)
            ? 12d
            : 0d;
        return basePenalty + constraintPenalty + Math.Min(simulation.RiskFlags.Count * 1.5d, 8d);
    }

    private static double ResolveSpeedScore(BuilderRecoveryComparisonMetricRecord metric)
        => metric.Scenario switch
        {
            "retry_same_route" => 95d,
            "reduce_scope" => 88d,
            "switch_route_manual" => 62d,
            "isolate_high_risk_files" => 48d,
            "staged_orchestration" => 34d,
            _ => 40d
        };

    private static double ResolveSafetyScore(BuilderRecoveryComparisonMetricRecord metric)
    {
        var baseScore = metric.ConstraintCompatibility switch
        {
            "compatible" => 70d,
            _ => 35d
        };
        var riskAdjustment = metric.RiskSummary.Contains("Risk high", StringComparison.OrdinalIgnoreCase)
            ? -25d
            : metric.RiskSummary.Contains("Risk medium", StringComparison.OrdinalIgnoreCase)
                ? -10d
                : 8d;
        return baseScore + riskAdjustment + (metric.HistoricalAccuracyRate * 20d);
    }

    private static double ResolveScopeScore(BuilderRecoveryComparisonMetricRecord metric)
        => metric.Scenario switch
        {
            "staged_orchestration" => 95d,
            "switch_route_manual" => 72d,
            "retry_same_route" => 58d,
            "isolate_high_risk_files" => 42d,
            "reduce_scope" => 25d,
            _ => 40d
        };

    private static double ResolveRiskScore(BuilderRecoveryComparisonMetricRecord metric)
        => metric.RiskSummary.Contains("Risk high", StringComparison.OrdinalIgnoreCase)
            ? 90d
            : metric.RiskSummary.Contains("Risk medium", StringComparison.OrdinalIgnoreCase)
                ? 60d
                : 25d;

    private static string DescribeMetric(BuilderRecoveryComparisonMetricRecord metric)
        => $"{metric.PlaybookTitle} / {FormatToken(metric.Scenario)}";

    private static int BranchRank(string branchId, string selectedIntent)
    {
        if (!string.IsNullOrWhiteSpace(selectedIntent) &&
            string.Equals(branchId, selectedIntent, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return branchId switch
        {
            "all_candidates" => 0,
            var value when string.Equals(value, BuilderOperatorIntentService.SafeRecoveryIntent, StringComparison.OrdinalIgnoreCase) => 1,
            var value when string.Equals(value, BuilderOperatorIntentService.FastRecoveryIntent, StringComparison.OrdinalIgnoreCase) => 2,
            var value when string.Equals(value, BuilderOperatorIntentService.MinimalChangeIntent, StringComparison.OrdinalIgnoreCase) => 3,
            var value when string.Equals(value, BuilderOperatorIntentService.FullResolutionIntent, StringComparison.OrdinalIgnoreCase) => 4,
            var value when string.Equals(value, BuilderOperatorIntentService.UnblockOrchestrationIntent, StringComparison.OrdinalIgnoreCase) => 5,
            _ => 6
        };
    }

    private static int TradeoffRank(string dimension)
        => dimension switch
        {
            "speed" => 0,
            "safety" => 1,
            "scope" => 2,
            "risk" => 3,
            _ => 4
        };

    private static int ConstraintCompatibilityRank(string compatibility)
        => string.Equals(compatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static int RiskRank(string riskLevel)
        => riskLevel switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"comparison-{hash[..10]}";
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
