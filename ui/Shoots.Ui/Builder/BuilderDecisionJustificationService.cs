using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderDecisionJustificationStepRecord(
    string StepId,
    string InputSource,
    string AppliedRule,
    string IntermediateResult);

public sealed record BuilderDecisionJustificationRecord(
    string JustificationId,
    string TargetType,
    string TargetId,
    string TargetLabel,
    IReadOnlyList<BuilderDecisionJustificationStepRecord> ReasoningChain,
    IReadOnlyList<string> EvidenceLinks,
    string Summary,
    string AuditNarrative);

public sealed record BuilderDecisionJustificationsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderDecisionJustificationRecord> Justifications,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderDecisionJustificationService
{
    public const string DecisionJustificationsFileName = "builder_decision_justifications.json";

    private const string SchemaVersion = "builder_decision_justifications.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string DecisionJustificationsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), DecisionJustificationsFileName);

    public static BuilderDecisionJustificationsRecord? LoadDecisionJustifications(string repoRoot)
        => Load<BuilderDecisionJustificationsRecord>(DecisionJustificationsPathForRepo(repoRoot));

    public static BuilderDecisionJustificationsRecord? RefreshDecisionJustifications(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderPlaybookContextFiltersRecord? contextFilters = null,
        BuilderRecoveryComparisonsRecord? comparisons = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorIntentRecord? operatorIntent = null,
        BuilderOperatorConstraintsRecord? constraints = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        if (playbooks is null)
        {
            return null;
        }

        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot) ??
                       BuilderRecoverySimulationService.RefreshRecoverySimulations(repoRoot, playbooks);
        rankings ??= BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot) ??
                    BuilderPlaybookRankingService.RefreshPlaybookRankings(repoRoot, playbooks, simulations);
        contextFilters ??= BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot) ??
                          BuilderPlaybookContextFilterService.RefreshContextFilters(repoRoot, playbooks, rankings);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot) ??
                    BuilderSimulationAccuracyService.RefreshSimulationAccuracy(repoRoot, simulations);
        comparisons ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(repoRoot) ??
                       BuilderRecoveryComparisonService.RefreshRecoveryComparisons(repoRoot, playbooks, simulations, rankings, accuracy, contextFilters: contextFilters);
        operatorIntent ??= BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        constraints ??= BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);

        var contextSnapshot = contextFilters?.ContextSnapshot;
        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var rankingIndex = (rankings?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contextIndex = (contextFilters?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var simulationAccuracyIndex = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(entry => entry.ObservedUtc)
                .ThenBy(entry => entry.RecordId, StringComparer.OrdinalIgnoreCase)
                .ToArray(), StringComparer.OrdinalIgnoreCase);
        var simulationCalibrationIndex = (accuracy?.SimulationTypeCalibration ?? Array.Empty<BuilderSimulationCalibrationRecord>())
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var playbookIndex = playbooks.Playbooks
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var playbookJustifications = playbooks.Playbooks
            .OrderBy(playbook => rankingIndex.TryGetValue(playbook.PlaybookId, out var ranking) ? ranking.RankingPosition : int.MaxValue)
            .ThenBy(playbook => playbook.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .Select(playbook => BuildPlaybookJustification(
                repoRoot,
                playbook,
                rankingIndex.TryGetValue(playbook.PlaybookId, out var ranking) ? ranking : null,
                contextIndex.TryGetValue(playbook.PlaybookId, out var contextFilter) ? contextFilter : null,
                contextSnapshot,
                operatorIntent,
                activeProfile))
            .ToArray();

        var simulationJustifications = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .OrderBy(simulation => rankingIndex.TryGetValue(simulation.PlaybookId, out var ranking) ? ranking.RankingPosition : int.MaxValue)
            .ThenBy(simulation => ScenarioRank(simulation.Scenario))
            .ThenBy(simulation => simulation.SimulationId, StringComparer.OrdinalIgnoreCase)
            .Select(simulation => BuildSimulationJustification(
                repoRoot,
                simulation,
                playbookIndex.TryGetValue(simulation.PlaybookId, out var playbook) ? playbook : null,
                rankingIndex.TryGetValue(simulation.PlaybookId, out var ranking) ? ranking : null,
                contextIndex.TryGetValue(simulation.PlaybookId, out var contextFilter) ? contextFilter : null,
                simulationCalibrationIndex.TryGetValue(simulation.Scenario, out var calibration) ? calibration : null,
                simulationAccuracyIndex.TryGetValue(simulation.SimulationId, out var history) ? history : Array.Empty<BuilderSimulationAccuracyRecord>(),
                operatorIntent,
                activeProfile))
            .ToArray();

        var comparisonJustifications = (comparisons?.ComparisonSets ?? Array.Empty<BuilderRecoveryComparisonSetRecord>())
            .OrderBy(set => string.Equals(set.BranchId, "all_candidates", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(set => BranchRank(set.BranchId, operatorIntent?.Intent ?? string.Empty))
            .ThenBy(set => set.ComparisonId, StringComparer.OrdinalIgnoreCase)
            .Select(set => BuildComparisonJustification(
                repoRoot,
                set,
                operatorIntent,
                activeProfile))
            .ToArray();

        var justifications = playbookJustifications
            .Concat(simulationJustifications)
            .Concat(comparisonJustifications)
            .OrderBy(justification => TargetTypeRank(justification.TargetType))
            .ThenBy(justification => justification.TargetLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(justification => justification.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new BuilderDecisionJustificationsRecord(
            playbooks.WorkspaceId,
            SchemaVersion,
            justifications,
            true,
            justifications.Length == 0
                ? $"No decision justifications are currently recorded for {playbooks.WorkspaceId}."
                : $"Generated {justifications.Length} deterministic decision justification(s) for {playbooks.WorkspaceId} covering playbooks, simulations, and comparisons.",
            DecisionJustificationsPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderDecisionJustificationRecord BuildPlaybookJustification(
        string repoRoot,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        BuilderPlaybookContextSnapshotRecord? contextSnapshot,
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorConstraintProfileRecord? activeProfile)
    {
        var intent = operatorIntent?.Intent ?? ranking?.SelectedIntent ?? string.Empty;
        var chain = new[]
        {
            BuildStep(
                playbook.PlaybookId,
                1,
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                "fixed_ranking_formula_v2",
                ranking is null
                    ? "No evidence-weighted ranking is recorded yet, so this playbook remains available without a computed position."
                    : $"Rank #{ranking.RankingPosition} with base score {ranking.RankingScore:0.##}, intent-adjusted score {ranking.IntentAdjustedScore:0.##}, confidence {FormatToken(ranking.ConfidenceIndicator)}, and sample size {ranking.SampleSize}."),
            BuildStep(
                playbook.PlaybookId,
                2,
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                "contextual_visibility_v3",
                contextFilter is null
                    ? "No contextual narrowing artifact is recorded yet, so this playbook is only explained by its recovery definition."
                    : $"Visibility {FormatToken(contextFilter.VisibilityState)}, priority {FormatToken(contextFilter.PriorityBand)}, relevance {contextFilter.RelevanceScore:0.##}, and filter reason: {contextFilter.FilterReason}"),
            BuildStep(
                playbook.PlaybookId,
                3,
                BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
                "intent_alignment_overlay_v1",
                string.IsNullOrWhiteSpace(intent)
                    ? "No explicit operator intent is currently recorded, so ranking and filtering stay on their base evidence."
                    : $"Selected intent {BuilderOperatorIntentService.GetIntentLabel(intent)} with alignment {(ranking?.IntentAlignmentScore ?? contextFilter?.IntentAlignmentScore ?? 0d):0.##}. {(ranking?.IntentReason ?? contextFilter?.IntentReason ?? "No additional intent reason is recorded.")}"),
            BuildStep(
                playbook.PlaybookId,
                4,
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                "hard_bound_constraint_profile_v1",
                activeProfile is null
                    ? "No active operator constraint profile is recorded, so this playbook is not blocked by hard-bound constraints."
                    : contextFilter?.ViolatesConstraints == true
                        ? contextFilter.ConstraintReason
                        : $"Profile {activeProfile.ProfileName} leaves this playbook constraint-compatible."),
            BuildStep(
                playbook.PlaybookId,
                5,
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                "playbook_scope_gate_summary_v1",
                $"{playbook.GateSummary} Repo scope: {playbook.RepoScope}. Cross-repo scope: {playbook.CrossRepoScope}. Current blocking state: {FormatToken(playbook.CurrentBlockingState)}. Evidence basis: {playbook.EvidenceBasis}")
        };

        var summary = ranking is null
            ? $"{playbook.Title} remains available because its recovery pattern is recorded, but no ranking artifact currently positions it."
            : $"{playbook.Title} is ranked #{ranking.RankingPosition} because {FirstSentence(ranking.ReasoningSummary)} It is {DescribeVisibility(contextFilter)} and {DescribeConstraintState(contextFilter, activeProfile)}.";
        var auditNarrative = BuildNarrative(
            $"Decision context: {BuildPlaybookDecisionContext(playbook, contextSnapshot)}",
            $"Evaluated option: {playbook.Title}.",
            $"Ranking outcome: {(ranking is null ? "no ranking artifact recorded" : $"rank #{ranking.RankingPosition} with base score {ranking.RankingScore:0.##} and intent-adjusted score {ranking.IntentAdjustedScore:0.##}")}.",
            $"Filtering outcome: {(contextFilter is null ? "no context filter recorded" : $"{FormatToken(contextFilter.VisibilityState)} with priority {FormatToken(contextFilter.PriorityBand)} and relevance {contextFilter.RelevanceScore:0.##}")}.",
            $"Constraint outcome: {DescribeConstraintState(contextFilter, activeProfile)}",
            $"Gate reminder: {playbook.GateSummary}",
            $"Evidence basis: {playbook.EvidenceBasis}",
            "This explanation is advisory only and does not bypass routing, review, approval, or finalize gates.");

        return new BuilderDecisionJustificationRecord(
            ComputeDeterministicId("playbook", playbook.PlaybookId),
            "playbook",
            playbook.PlaybookId,
            playbook.Title,
            chain,
            BuildEvidenceLinks(
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                playbook.ArtifactLinks,
                ranking?.EvidenceLinks,
                contextFilter?.EvidenceLinks),
            summary,
            auditNarrative);
    }

    private static BuilderDecisionJustificationRecord BuildSimulationJustification(
        string repoRoot,
        BuilderRecoverySimulationRecord simulation,
        BuilderRecoveryPlaybookRecord? playbook,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        BuilderSimulationCalibrationRecord? scenarioCalibration,
        IReadOnlyList<BuilderSimulationAccuracyRecord> accuracyHistory,
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorConstraintProfileRecord? activeProfile)
    {
        var chain = new[]
        {
            BuildStep(
                simulation.SimulationId,
                1,
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                "simulation_projection_v2",
                $"{simulation.ReasoningSummary} Predicted outcome class {FormatToken(simulation.PredictedOutcomeClass)} with success {FormatToken(simulation.SuccessLikelihood)}, failure {FormatToken(simulation.FailureLikelihood)}, and next gate {FormatToken(simulation.ExpectedNextBlockingGate)}."),
            BuildStep(
                simulation.SimulationId,
                2,
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                "calibration_tracking_v1",
                scenarioCalibration is null
                    ? $"No scenario calibration is recorded yet. Predicted confidence remains {FormatToken(simulation.ConfidenceLevel)} at {simulation.ConfidenceScore:P0}."
                    : $"Predicted confidence {FormatToken(simulation.ConfidenceLevel)} at {simulation.ConfidenceScore:P0}; calibrated confidence {FormatToken(scenarioCalibration.CalibratedConfidence)} with historical accuracy {scenarioCalibration.HistoricalAccuracyRate:P0} across {scenarioCalibration.SampleSize} matching simulations."),
            BuildStep(
                simulation.SimulationId,
                3,
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                "ranking_and_intent_rollup_v2",
                ranking is null
                    ? "No parent playbook ranking is recorded yet, so this simulation is explained directly from its scenario projection."
                    : $"Parent playbook rank #{ranking.RankingPosition} with score {ranking.RankingScore:0.##}. Intent alignment {(ranking.IntentAlignmentScore):0.##} for {BuilderOperatorIntentService.GetIntentLabel(operatorIntent?.Intent ?? ranking.SelectedIntent)}."),
            BuildStep(
                simulation.SimulationId,
                4,
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                "hard_bound_constraint_profile_v1",
                activeProfile is null
                    ? "No active operator constraint profile is recorded, so this scenario remains constraint-compatible."
                    : simulation.ConstraintReason),
            BuildStep(
                simulation.SimulationId,
                5,
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                "context_and_blocking_projection_v3",
                $"Parent playbook visibility {(contextFilter is null ? "not recorded" : FormatToken(contextFilter.VisibilityState))}. Blocking conditions: {(simulation.BlockingConditions.Count == 0 ? "none recorded" : string.Join(", ", simulation.BlockingConditions.Select(FormatToken)))}. Expected state changes: {(simulation.ExpectedStateChanges.Count == 0 ? "none recorded" : string.Join(" ", simulation.ExpectedStateChanges))}")
        };

        var historySummary = accuracyHistory.Count == 0
            ? "No completed operator outcome has been recorded for this exact simulation yet."
            : $"Recent accuracy history records {accuracyHistory.Count(record => record.AccuracyFlag)}/{accuracyHistory.Count} accurate outcome match(es).";
        var summary = $"{DescribeScenario(simulation)} predicts {simulation.PredictedOutcome}. {historySummary} Constraint state: {DescribeSimulationConstraintState(simulation, activeProfile)}";
        var auditNarrative = BuildNarrative(
            $"Decision context: {BuildSimulationDecisionContext(playbook, simulation, contextFilter)}",
            $"Evaluated scenario: {DescribeScenario(simulation)}.",
            $"Prediction: {simulation.PredictedOutcome}",
            $"Calibration outcome: {(scenarioCalibration is null ? "no historical calibration recorded" : $"{FormatToken(scenarioCalibration.CalibratedConfidence)} confidence with {scenarioCalibration.HistoricalAccuracyRate:P0} historical accuracy across {scenarioCalibration.SampleSize} samples")}.",
            $"Constraint outcome: {DescribeSimulationConstraintState(simulation, activeProfile)}",
            $"Blocking gate: {FormatToken(simulation.ExpectedNextBlockingGate)}. Blocking conditions: {(simulation.BlockingConditions.Count == 0 ? "none recorded" : string.Join(", ", simulation.BlockingConditions.Select(FormatToken)))}.",
            $"Intent context: {(string.IsNullOrWhiteSpace(operatorIntent?.Intent) ? "no explicit operator intent recorded" : BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent))}.",
            "This explanation is advisory only and does not execute recovery or mutate workspace state.");

        return new BuilderDecisionJustificationRecord(
            ComputeDeterministicId("simulation", simulation.SimulationId),
            "simulation",
            simulation.SimulationId,
            $"{playbook?.Title ?? "Recovery scenario"} / {DescribeScenario(simulation)}",
            chain,
            BuildEvidenceLinks(
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                simulation.ArtifactLinks,
                playbook?.ArtifactLinks,
                ranking?.EvidenceLinks,
                contextFilter?.EvidenceLinks,
                accuracyHistory.SelectMany(record => record.ArtifactLinks)),
            summary,
            auditNarrative);
    }

    private static BuilderDecisionJustificationRecord BuildComparisonJustification(
        string repoRoot,
        BuilderRecoveryComparisonSetRecord comparison,
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorConstraintProfileRecord? activeProfile)
    {
        var orderedMetrics = comparison.ComparisonMetrics
            .OrderBy(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(metric => metric.ComparisonScore)
            .ThenBy(metric => metric.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(metric => metric.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var topMetric = orderedMetrics.FirstOrDefault();
        var secondMetric = orderedMetrics.Skip(1).FirstOrDefault();
        var blockedCount = orderedMetrics.Count(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase));
        var tradeoffSummary = comparison.Tradeoffs.Count == 0
            ? "No tradeoffs are recorded for this comparison set."
            : string.Join(" ", comparison.Tradeoffs.Select(tradeoff => $"{FormatToken(tradeoff.Dimension)}: {tradeoff.ComparisonResult}"));
        var chain = new[]
        {
            BuildStep(
                comparison.ComparisonId,
                1,
                BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
                "comparison_formula_v1",
                topMetric is null
                    ? "No comparison metrics are recorded for this branch."
                    : $"Branch {comparison.BranchLabel} compares {comparison.ComparisonMetrics.Count} scenario(s). Leading option {topMetric.PlaybookTitle} / {FormatToken(topMetric.Scenario)} scores {topMetric.ComparisonScore:0.##} with predicted success {topMetric.PredictedSuccessRate:0.##}% and {FormatToken(topMetric.ConstraintCompatibility)} constraint state."),
            BuildStep(
                comparison.ComparisonId,
                2,
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                "ranking_and_intent_rollup_v2",
                topMetric is null
                    ? "No leading comparison metric is available to explain ranking or intent signals."
                    : $"Leading option ranking signal {topMetric.RankingScore:0.##}, intent alignment {topMetric.IntentAlignmentScore:0.##}, and branch {(string.IsNullOrWhiteSpace(operatorIntent?.Intent) ? comparison.BranchLabel : $"{comparison.BranchLabel} under {BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent)}")}."),
            BuildStep(
                comparison.ComparisonId,
                3,
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                "simulation_projection_rollup_v2",
                topMetric is null
                    ? "No scenario projection is recorded for this comparison."
                    : $"Leading option expects blocking gate {FormatToken(topMetric.ExpectedBlockingGate)} with {FormatToken(topMetric.ConfidenceBand)} confidence and historical accuracy {topMetric.HistoricalAccuracyRate:P0}. {(secondMetric is null ? "No competing scenario is recorded." : $"Runner-up {secondMetric.PlaybookTitle} / {FormatToken(secondMetric.Scenario)} scores {secondMetric.ComparisonScore:0.##}.")}"),
            BuildStep(
                comparison.ComparisonId,
                4,
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                "hard_bound_constraint_profile_v1",
                activeProfile is null
                    ? $"No active operator constraint profile is recorded. Blocked scenarios in this set: {blockedCount}."
                    : blockedCount == 0
                        ? $"Profile {activeProfile.ProfileName} does not block any scenario in this comparison set."
                        : $"Profile {activeProfile.ProfileName} blocks {blockedCount} scenario(s) in this comparison set."),
            BuildStep(
                comparison.ComparisonId,
                5,
                BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
                "tradeoff_analysis_v1",
                tradeoffSummary)
        };

        var summary = topMetric is null
            ? $"{comparison.BranchLabel} has no recorded comparison metrics yet."
            : $"{comparison.BranchLabel} favors {topMetric.PlaybookTitle} / {FormatToken(topMetric.Scenario)} over competing scenarios because its comparison score is {topMetric.ComparisonScore:0.##}. {blockedCount} scenario(s) are blocked by constraints.";
        var auditNarrative = BuildNarrative(
            $"Decision context: comparison branch {comparison.BranchLabel} with {comparison.PlaybookIds.Count} playbook(s) and {comparison.SimulationIds.Count} simulation(s).",
            $"Evaluated options: {(comparison.ComparisonMetrics.Count == 0 ? "none recorded" : string.Join("; ", comparison.ComparisonMetrics.Select(metric => $"{metric.PlaybookTitle} / {FormatToken(metric.Scenario)} ({metric.ComparisonScore:0.##})")))}.",
            $"Leading option: {(topMetric is null ? "none recorded" : $"{topMetric.PlaybookTitle} / {FormatToken(topMetric.Scenario)} with score {topMetric.ComparisonScore:0.##}")}.",
            $"Constraint outcome: {(activeProfile is null ? "no active constraint profile recorded" : $"{blockedCount} scenario(s) blocked by {activeProfile.ProfileName}")}.",
            $"Tradeoffs: {tradeoffSummary}",
            $"Summary: {comparison.Summary}",
            "This comparison remains advisory only and does not select, execute, approve, or finalize any option.");

        return new BuilderDecisionJustificationRecord(
            ComputeDeterministicId("comparison", comparison.ComparisonId),
            "comparison",
            comparison.ComparisonId,
            comparison.BranchLabel,
            chain,
            BuildEvidenceLinks(
                BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                comparison.ComparisonMetrics.SelectMany(metric => metric.EvidenceLinks)),
            summary,
            auditNarrative);
    }

    private static BuilderDecisionJustificationStepRecord BuildStep(
        string targetId,
        int stepNumber,
        string inputSource,
        string appliedRule,
        string intermediateResult)
        => new(
            ComputeDeterministicId("step", targetId, stepNumber.ToString(), appliedRule),
            inputSource,
            appliedRule,
            intermediateResult);

    private static IReadOnlyList<string> BuildEvidenceLinks(params object?[] sources)
    {
        var links = new List<string>();
        foreach (var source in sources)
        {
            switch (source)
            {
                case null:
                    continue;
                case string path when !string.IsNullOrWhiteSpace(path):
                    links.Add(path);
                    break;
                case IEnumerable<string> paths:
                    links.AddRange(paths.Where(path => !string.IsNullOrWhiteSpace(path)));
                    break;
            }
        }

        return links
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildPlaybookDecisionContext(BuilderRecoveryPlaybookRecord playbook, BuilderPlaybookContextSnapshotRecord? snapshot)
        => snapshot is null
            ? $"{FormatToken(playbook.FailureClass)} in {playbook.RepoScope} with route {(playbook.AppliesToRoutes.FirstOrDefault() ?? "not recorded")}."
            : $"{FormatToken(snapshot.CurrentFailureClass)} in {snapshot.RepoFocus}, active route {snapshot.ActiveRoute}, blocking state {FormatToken(snapshot.CurrentBlockingState)}, evaluating playbook {playbook.Title}.";

    private static string BuildSimulationDecisionContext(
        BuilderRecoveryPlaybookRecord? playbook,
        BuilderRecoverySimulationRecord simulation,
        BuilderPlaybookContextFilterEntryRecord? contextFilter)
        => $"{playbook?.Title ?? "Recovery scenario"} for {FormatToken(simulation.FailureClass)} on route {simulation.TargetRoute}. Parent visibility {(contextFilter is null ? "not recorded" : FormatToken(contextFilter.VisibilityState))}.";

    private static string DescribeVisibility(BuilderPlaybookContextFilterEntryRecord? contextFilter)
        => contextFilter is null
            ? "visible without a current context filter"
            : $"{FormatToken(contextFilter.VisibilityState)} with {FormatToken(contextFilter.PriorityBand)} priority";

    private static string DescribeConstraintState(
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        BuilderOperatorConstraintProfileRecord? activeProfile)
        => activeProfile is null
            ? "not blocked by any active constraint profile"
            : contextFilter?.ViolatesConstraints == true
                ? $"blocked by constraints because {contextFilter.ConstraintReason}"
                : $"compatible with profile {activeProfile.ProfileName}";

    private static string DescribeSimulationConstraintState(
        BuilderRecoverySimulationRecord simulation,
        BuilderOperatorConstraintProfileRecord? activeProfile)
        => activeProfile is null
            ? "no active constraint profile is recorded"
            : string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)
                ? simulation.ConstraintReason
                : $"profile {activeProfile.ProfileName} leaves this scenario constraint-compatible";

    private static string DescribeScenario(BuilderRecoverySimulationRecord simulation)
        => FormatToken(simulation.Scenario);

    private static string FirstSentence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var index = value.IndexOf('.');
        return index < 0 ? value : value[..(index + 1)];
    }

    private static string BuildNarrative(params string[] lines)
        => string.Join(global::System.Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));

    private static int TargetTypeRank(string targetType)
        => targetType switch
        {
            "playbook" => 0,
            "simulation" => 1,
            "comparison" => 2,
            _ => 3
        };

    private static int ScenarioRank(string scenario)
        => scenario switch
        {
            "retry_same_route" => 0,
            "switch_route_manual" => 1,
            "reduce_scope" => 2,
            "staged_orchestration" => 3,
            "isolate_high_risk_files" => 4,
            _ => 5
        };

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

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"justification-{hash[..10]}";
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
