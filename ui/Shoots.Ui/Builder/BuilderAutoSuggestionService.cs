using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderAutoSuggestionRecord(
    string SuggestionId,
    string SuggestionKind,
    string TargetType,
    string TargetId,
    string PlaybookId,
    string SimulationId,
    double SuggestionScore,
    string Confidence,
    string RiskLevel,
    string TradeoffLabel,
    string ConstraintStatus,
    string GuardrailStatus,
    string ReadinessState,
    string CalibrationProfile,
    string SignalBalanceSummary,
    IReadOnlyList<BuilderSignalContributionRecord> SignalContributions,
    string SelectionReason,
    IReadOnlyList<string> SupportingEvidence);

public sealed record BuilderAutoSuggestionDivergenceRecord(
    string DecisionId,
    bool DivergedFromPrimary,
    string DecisionTargetType,
    string DecisionTargetId,
    string Summary);

public sealed record BuilderAutoSuggestionsRecord(
    string WorkspaceId,
    string SchemaVersion,
    string CurrentSelectionTargetType,
    string CurrentSelectionTargetId,
    string PrimarySuggestionId,
    string AlternateSuggestionId,
    IReadOnlyList<BuilderAutoSuggestionRecord> Suggestions,
    BuilderAutoSuggestionDivergenceRecord? LatestDecisionDivergence,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderAutoSuggestionService
{
    public const string AutoSuggestionsFileName = "builder_auto_suggestions.json";

    private const string SchemaVersion = "builder_auto_suggestions.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string AutoSuggestionsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), AutoSuggestionsFileName);

    public static BuilderAutoSuggestionsRecord? LoadAutoSuggestions(string repoRoot)
        => Load<BuilderAutoSuggestionsRecord>(AutoSuggestionsPathForRepo(repoRoot));

    public static BuilderAutoSuggestionsRecord? RefreshAutoSuggestions(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderPlaybookContextFiltersRecord? contextFilters = null,
        BuilderRecoveryComparisonsRecord? comparisons = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderExecutionReadinessRecord? readiness = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        DateTimeOffset? observedUtc = null,
        BuilderTrustIndexRecord? trust = null,
        BuilderPredictiveDriftReport? predictiveDrift = null,
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
        contextFilters ??= BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot) ??
                          BuilderPlaybookContextFilterService.RefreshContextFilters(repoRoot, playbooks, rankings);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot) ??
                    BuilderSimulationAccuracyService.RefreshSimulationAccuracy(repoRoot, simulations);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        comparisons ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(repoRoot) ??
                       BuilderRecoveryComparisonService.RefreshRecoveryComparisons(repoRoot, playbooks, simulations, rankings, accuracy, decisions, contextFilters);
        readiness ??= BuilderExecutionReadinessService.LoadExecutionReadiness(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        trust ??= BuilderTrustIndexService.LoadTrustIndex(repoRoot);
        predictiveDrift ??= BuilderPredictiveDriftService.LoadPredictiveDrift(repoRoot);
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
                            BuilderOperatorIntentService.LoadOperatorIntent(repoRoot),
                            observedUtc);

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var playbookIndex = playbooks.Playbooks
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rankingIndex = (rankings?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contextIndex = (contextFilters?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var comparisonMetricIndex = (comparisons?.ComparisonSets ?? Array.Empty<BuilderRecoveryComparisonSetRecord>())
            .SelectMany(entry => entry.ComparisonMetrics)
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => ConstraintCompatibilityRank(entry.ConstraintCompatibility))
                    .ThenByDescending(entry => entry.ComparisonScore)
                    .ThenBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        var scenarioCalibrationIndex = (accuracy?.SimulationTypeCalibration ?? Array.Empty<BuilderSimulationCalibrationRecord>())
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var simulationDecisionIndex = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SimulationId))
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var trustPlaybookIndex = (trust?.TargetProfiles ?? Array.Empty<BuilderTrustTargetProfileRecord>())
            .Where(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.TrustScore).First(), StringComparer.OrdinalIgnoreCase);
        var trustSimulationIndex = (trust?.TargetProfiles ?? Array.Empty<BuilderTrustTargetProfileRecord>())
            .Where(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.TrustScore).First(), StringComparer.OrdinalIgnoreCase);
        var predictivePlaybookIndex = (predictiveDrift?.Predictions ?? Array.Empty<BuilderPredictiveDriftRecord>())
            .Where(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => RiskRank(entry.RiskEscalation)).ThenByDescending(entry => entry.FailureProbability).First(), StringComparer.OrdinalIgnoreCase);
        var predictiveSimulationIndex = (predictiveDrift?.Predictions ?? Array.Empty<BuilderPredictiveDriftRecord>())
            .Where(entry => string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => RiskRank(entry.RiskEscalation)).ThenByDescending(entry => entry.FailureProbability).First(), StringComparer.OrdinalIgnoreCase);

        var simulationCandidates = simulations.Simulations
            .Where(entry => playbookIndex.ContainsKey(entry.PlaybookId))
            .Select(entry => BuildSimulationCandidate(
                repoRoot,
                workspaceId,
                entry,
                playbookIndex[entry.PlaybookId],
                rankingIndex.TryGetValue(entry.PlaybookId, out var ranking) ? ranking : null,
                contextIndex.TryGetValue(entry.PlaybookId, out var contextFilter) ? contextFilter : null,
                comparisonMetricIndex.TryGetValue(entry.SimulationId, out var metric) ? metric : null,
                scenarioCalibrationIndex.TryGetValue(entry.Scenario, out var scenarioCalibration) ? scenarioCalibration : null,
                simulationDecisionIndex.TryGetValue(entry.SimulationId, out var simulationDecisions) ? simulationDecisions : Array.Empty<BuilderOperatorDecisionRecord>(),
                readiness,
                guardrails,
                trustSimulationIndex.TryGetValue(entry.SimulationId, out var trustProfile) ? trustProfile : null,
                predictiveSimulationIndex.TryGetValue(entry.SimulationId, out var predictiveSignal) ? predictiveSignal : null,
                calibration))
            .OrderByDescending(entry => entry.SelectionScore)
            .ThenBy(entry => RiskRank(entry.RiskLevel))
            .ThenBy(entry => entry.BranchId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bestSimulationByPlaybook = simulationCandidates
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(entry => entry.SelectionScore)
                    .ThenBy(entry => RiskRank(entry.RiskLevel))
                    .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var playbookCandidates = playbooks.Playbooks
            .Select(entry => BuildPlaybookCandidate(
                repoRoot,
                workspaceId,
                entry,
                rankingIndex.TryGetValue(entry.PlaybookId, out var ranking) ? ranking : null,
                contextIndex.TryGetValue(entry.PlaybookId, out var contextFilter) ? contextFilter : null,
                bestSimulationByPlaybook.TryGetValue(entry.PlaybookId, out var bestSimulation) ? bestSimulation : null,
                readiness,
                guardrails,
                trustPlaybookIndex.TryGetValue(entry.PlaybookId, out var trustProfile) ? trustProfile : null,
                predictivePlaybookIndex.TryGetValue(entry.PlaybookId, out var predictiveSignal) ? predictiveSignal : null,
                calibration))
            .OrderByDescending(entry => entry.SelectionScore)
            .ThenBy(entry => RiskRank(entry.RiskLevel))
            .ThenBy(entry => entry.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var primarySimulation = SelectPrimary(simulationCandidates);
        var alternateSimulation = SelectAlternate(
            simulationCandidates,
            primarySimulation,
            (candidate, primary) =>
                !string.Equals(candidate.BranchId, primary.BranchId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.RiskLevel, primary.RiskLevel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.PlaybookId, primary.PlaybookId, StringComparison.OrdinalIgnoreCase));
        var primaryPlaybook = SelectPrimary(playbookCandidates, candidate =>
            primarySimulation is null || string.Equals(candidate.PlaybookId, primarySimulation.PlaybookId, StringComparison.OrdinalIgnoreCase));
        var alternatePlaybook = SelectAlternate(
            playbookCandidates,
            primaryPlaybook,
            (candidate, primary) =>
                !string.Equals(candidate.FailureClass, primary.FailureClass, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.RiskLevel, primary.RiskLevel, StringComparison.OrdinalIgnoreCase));
        if (alternatePlaybook is null && alternateSimulation is not null)
        {
            alternatePlaybook = playbookCandidates.FirstOrDefault(candidate =>
                !string.Equals(candidate.PlaybookId, primaryPlaybook?.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.PlaybookId, alternateSimulation.PlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        var suggestions = BuildSuggestions(primaryPlaybook, alternatePlaybook, primarySimulation, alternateSimulation);
        var primarySuggestion = suggestions.FirstOrDefault(entry => string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase));
        var alternateSuggestion = suggestions.FirstOrDefault(entry => string.Equals(entry.SuggestionKind, "alternate", StringComparison.OrdinalIgnoreCase));
        var divergence = BuildLatestDecisionDivergence(decisions, primaryPlaybook, primarySimulation);
        var currentSelectionSummary = BuildCurrentSelectionSummary(readiness, comparisons, primaryPlaybook, primarySimulation);
        var report = new BuilderAutoSuggestionsRecord(
            workspaceId,
            SchemaVersion,
            readiness?.SelectionTargetType ?? string.Empty,
            readiness?.SelectionTargetId ?? string.Empty,
            primarySuggestion?.SuggestionId ?? string.Empty,
            alternateSuggestion?.SuggestionId ?? string.Empty,
            suggestions,
            divergence,
            true,
            BuildSummary(workspaceId, primaryPlaybook, primarySimulation, alternateSimulation, currentSelectionSummary, divergence),
            AutoSuggestionsPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderAutoSuggestionRecord[] BuildSuggestions(
        PlaybookSuggestionCandidate? primaryPlaybook,
        PlaybookSuggestionCandidate? alternatePlaybook,
        SimulationSuggestionCandidate? primarySimulation,
        SimulationSuggestionCandidate? alternateSimulation)
    {
        var suggestions = new List<BuilderAutoSuggestionRecord>();
        if (primaryPlaybook is not null)
        {
            suggestions.Add(primaryPlaybook.ToRecord("primary"));
        }

        if (alternatePlaybook is not null &&
            !string.Equals(alternatePlaybook.PlaybookId, primaryPlaybook?.PlaybookId, StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add(alternatePlaybook.ToRecord("alternate"));
        }

        if (primarySimulation is not null)
        {
            suggestions.Add(primarySimulation.ToRecord("primary"));
        }

        if (alternateSimulation is not null &&
            !string.Equals(alternateSimulation.SimulationId, primarySimulation?.SimulationId, StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add(alternateSimulation.ToRecord("alternate"));
        }

        return suggestions
            .OrderBy(entry => SuggestionKindRank(entry.SuggestionKind))
            .ThenBy(entry => TargetTypeRank(entry.TargetType))
            .ThenByDescending(entry => entry.SuggestionScore)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PlaybookSuggestionCandidate BuildPlaybookCandidate(
        string repoRoot,
        string workspaceId,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        SimulationSuggestionCandidate? bestSimulation,
        BuilderExecutionReadinessRecord? readiness,
        BuilderPreventativeGuardrailsReport? guardrails,
        BuilderTrustTargetProfileRecord? trustProfile,
        BuilderPredictiveDriftRecord? predictiveDrift,
        BuilderSignalCalibrationRecord? calibration)
    {
        var guardrailMatches = BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
            guardrails,
            playbook.PlaybookId,
            string.Empty,
            playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty,
            workspaceId);
        var guardrailPresentation = ResolveGuardrail(guardrailMatches);
        var rankingValue = ranking?.RankingScore ?? contextFilter?.BaseRelevanceScore ?? 50d;
        var intentValue = ranking?.IntentAlignmentScore ?? contextFilter?.IntentAlignmentScore ?? 50d;
        var rankingSignal = rankingValue;
        var intentSignal = intentValue;
        var constraintSignal = BuilderSignalCalibrationService.ResolveConstraintSignal(contextFilter?.ViolatesConstraints == true);
        var trustSignal = trustProfile?.TrustScore
                          ?? Math.Round((((ranking?.HistoricalAccuracyRate ?? 0.50d) + (ranking?.OutcomeSuccessRate ?? 0.50d)) / 2d) * 100d, 2);
        var guardrailSignal = BuilderSignalCalibrationService.ResolveGuardrailSafetySignal(guardrailPresentation.RiskLevel);
        var driftSignal = predictiveDrift is null
            ? BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                1d - ((bestSimulation?.SelectionScore ?? 50d) / 100d),
                bestSimulation is null ? string.Empty : "stable")
            : BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                predictiveDrift.FailureProbability,
                predictiveDrift.DriftTrend);
        var accuracySignal = ranking?.HistoricalAccuracyRate ?? 0.50d;
        var outcomeSignal = ranking?.OutcomeSuccessRate ?? 0.50d;
        var calibrationEvaluation = BuilderSignalCalibrationService.EvaluateCompositeScore(
            calibration,
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.RankingSignalId,
                rankingSignal,
                $"Ranking score {rankingValue:0.##} based on evidence-weighted ranking and contextual relevance."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.IntentSignalId,
                intentSignal,
                $"Intent alignment {intentValue:0.##} from the current playbook ranking and context filter."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.ConstraintSignalId,
                constraintSignal,
                contextFilter?.ViolatesConstraints == true
                    ? $"Constraint profile blocks this playbook via {string.Join(", ", contextFilter.ViolatedConstraints)}."
                    : "Constraint profile is compatible with this playbook."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.TrustSignalId,
                trustSignal,
                trustProfile is null
                    ? $"Fallback trust signal uses historical accuracy {accuracySignal:P0} and outcome success {outcomeSignal:P0}."
                    : $"Trust profile {trustProfile.ConfidenceProfile} scores {trustProfile.TrustScore:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.GuardrailSignalId,
                guardrailSignal,
                $"Guardrail risk {FormatToken(guardrailPresentation.RiskLevel)} maps to safety signal {guardrailSignal:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.DriftSignalId,
                driftSignal,
                predictiveDrift is null
                    ? "No predictive drift forecast exists, so the best scenario signal supplies a deterministic neutral baseline."
                    : $"Predictive drift {FormatToken(predictiveDrift.DriftTrend)} at {predictiveDrift.FailureProbability:P0} failure likelihood."))
            ;
        var selectionScore = Math.Round(Clamp01((calibrationEvaluation.CompositeScore + BuilderSignalCalibrationService.ResolveReadinessModifier(readiness?.ReadinessState ?? string.Empty)) / 100d) * 100d, 2);
        var evidence = BuildEvidenceLinks(
            new[]
            {
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
                BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot),
                BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
                BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot)
            },
            playbook.ArtifactLinks,
            ranking?.EvidenceLinks,
            contextFilter?.EvidenceLinks,
            bestSimulation?.SupportingEvidence,
            guardrailPresentation.EvidenceLinks,
            trustProfile?.EvidenceLinks,
            predictiveDrift?.LinkedArtifacts);
        var confidence = ResolveConfidenceLabel(accuracySignal, outcomeSignal, guardrailPresentation.RiskLevel);
        var tradeoffLabel = ResolvePlaybookTradeoffLabel(playbook, bestSimulation);
        var reason = $"{calibrationEvaluation.Summary} Readiness {FormatToken(readiness?.ReadinessState ?? string.Empty)} applies a deterministic modifier without changing operator control.";
        return new PlaybookSuggestionCandidate(
            playbook.PlaybookId,
            playbook.Title,
            playbook.FailureClass,
            playbook.RepoScope,
            bestSimulation?.SimulationId ?? string.Empty,
            selectionScore,
            confidence,
            guardrailPresentation.RiskLevel,
            tradeoffLabel,
            contextFilter?.ViolatesConstraints == true ? "blocked_by_constraints" : "compatible",
            guardrailPresentation.Summary,
            readiness?.ReadinessState ?? string.Empty,
            calibration?.CalibrationProfile ?? "balanced",
            calibrationEvaluation.Summary,
            calibrationEvaluation.Contributions,
            reason,
            evidence);
    }

    private static SimulationSuggestionCandidate BuildSimulationCandidate(
        string repoRoot,
        string workspaceId,
        BuilderRecoverySimulationRecord simulation,
        BuilderRecoveryPlaybookRecord playbook,
        BuilderPlaybookRankingRecord? ranking,
        BuilderPlaybookContextFilterEntryRecord? contextFilter,
        BuilderRecoveryComparisonMetricRecord? comparisonMetric,
        BuilderSimulationCalibrationRecord? scenarioCalibration,
        IReadOnlyList<BuilderOperatorDecisionRecord> simulationDecisions,
        BuilderExecutionReadinessRecord? readiness,
        BuilderPreventativeGuardrailsReport? guardrails,
        BuilderTrustTargetProfileRecord? trustProfile,
        BuilderPredictiveDriftRecord? predictiveDrift,
        BuilderSignalCalibrationRecord? calibration)
    {
        var guardrailMatches = BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
            guardrails,
            simulation.PlaybookId,
            simulation.SimulationId,
            simulation.TargetRoute,
            workspaceId);
        var guardrailPresentation = ResolveGuardrail(guardrailMatches);
        var rankingValue = ranking?.RankingScore ?? comparisonMetric?.RankingScore ?? 50d;
        var intentValue = ranking?.IntentAlignmentScore ?? comparisonMetric?.IntentAlignmentScore ?? contextFilter?.IntentAlignmentScore ?? 50d;
        var rankingSignal = rankingValue;
        var intentSignal = intentValue;
        var constraintSignal = BuilderSignalCalibrationService.ResolveConstraintSignal(
            string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase));
        var trustSignal = trustProfile?.TrustScore
                          ?? Math.Round((((comparisonMetric?.HistoricalAccuracyRate ?? ranking?.HistoricalAccuracyRate ?? 0.50d) +
                                           (comparisonMetric?.HistoricalOutcomeSuccessRate ?? ranking?.OutcomeSuccessRate ?? 0.50d)) / 2d) * 100d, 2);
        var guardrailSignal = BuilderSignalCalibrationService.ResolveGuardrailSafetySignal(
            ResolveHighestRiskLevel(guardrailPresentation.RiskLevel, simulation.RiskEscalation));
        var driftSignal = predictiveDrift is null
            ? BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                ResolveFailureLikelihoodProbability(simulation.FailureLikelihood),
                string.Empty)
            : BuilderSignalCalibrationService.ResolveDriftSafetySignal(
                predictiveDrift.FailureProbability,
                predictiveDrift.DriftTrend);
        var accuracySignal = scenarioCalibration?.HistoricalAccuracyRate ?? comparisonMetric?.HistoricalAccuracyRate ?? ranking?.HistoricalAccuracyRate ?? 0.50d;
        var outcomeSignal = simulationDecisions.Count == 0
            ? comparisonMetric?.HistoricalOutcomeSuccessRate ?? ranking?.OutcomeSuccessRate ?? 0.50d
            : simulationDecisions.Count(entry => entry.SuccessFlag) / (double)simulationDecisions.Count;
        var confidenceSignal = ResolveConfidenceSignal(simulation, scenarioCalibration, comparisonMetric);
        var calibrationEvaluation = BuilderSignalCalibrationService.EvaluateCompositeScore(
            calibration,
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.RankingSignalId,
                rankingSignal,
                $"Ranking signal {rankingValue:0.##} combines evidence-weighted ranking and comparison ordering context."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.IntentSignalId,
                intentSignal,
                $"Intent alignment {intentValue:0.##} reflects the scenario branch and current operator goal."),
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
                    ? $"Fallback trust signal blends historical accuracy {accuracySignal:P0}, outcome success {outcomeSignal:P0}, and confidence {confidenceSignal:P0}."
                    : $"Trust profile {trustProfile.ConfidenceProfile} scores {trustProfile.TrustScore:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.GuardrailSignalId,
                guardrailSignal,
                $"Guardrail risk {FormatToken(ResolveHighestRiskLevel(guardrailPresentation.RiskLevel, simulation.RiskEscalation))} maps to safety signal {guardrailSignal:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.DriftSignalId,
                driftSignal,
                predictiveDrift is null
                    ? $"Fallback drift signal uses failure likelihood {FormatToken(simulation.FailureLikelihood)} and scenario confidence."
                    : $"Predictive drift {FormatToken(predictiveDrift.DriftTrend)} at {predictiveDrift.FailureProbability:P0} failure likelihood."));
        var selectionScore = Math.Round(Clamp01((calibrationEvaluation.CompositeScore + BuilderSignalCalibrationService.ResolveReadinessModifier(readiness?.ReadinessState ?? string.Empty)) / 100d) * 100d, 2);
        var evidence = BuildEvidenceLinks(
            new[]
            {
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot),
                BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
                BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot)
            },
            simulation.ArtifactLinks,
            playbook.ArtifactLinks,
            comparisonMetric?.EvidenceLinks,
            ranking?.EvidenceLinks,
            contextFilter?.EvidenceLinks,
            simulationDecisions.SelectMany(entry => entry.TriggerArtifacts),
            simulationDecisions.SelectMany(entry => entry.ResultArtifacts),
            guardrailPresentation.EvidenceLinks,
            trustProfile?.EvidenceLinks,
            predictiveDrift?.LinkedArtifacts);
        var confidence = ResolveConfidenceLabel(accuracySignal, outcomeSignal, guardrailPresentation.RiskLevel, scenarioCalibration?.CalibratedConfidence ?? simulation.ConfidenceLevel);
        var tradeoffLabel = ResolveSimulationTradeoffLabel(simulation, playbook);
        var reason = $"{calibrationEvaluation.Summary} Readiness {FormatToken(readiness?.ReadinessState ?? string.Empty)} applies a deterministic modifier without changing operator control.";
        return new SimulationSuggestionCandidate(
            simulation.SimulationId,
            simulation.PlaybookId,
            playbook.Title,
            simulation.Scenario,
            comparisonMetric?.BranchId ?? ResolveBranchId(playbook, simulation, ranking),
            selectionScore,
            confidence,
            ResolveHighestRiskLevel(guardrailPresentation.RiskLevel, simulation.RiskEscalation),
            tradeoffLabel,
            string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)
                ? "blocked_by_constraints"
                : "compatible",
            guardrailPresentation.Summary,
            readiness?.ReadinessState ?? string.Empty,
            calibration?.CalibrationProfile ?? "balanced",
            calibrationEvaluation.Summary,
            calibrationEvaluation.Contributions,
            reason,
            evidence);
    }

    private static GuardrailResolution ResolveGuardrail(IReadOnlyList<BuilderPreventativeGuardrailRecord> matches)
    {
        var primary = matches
            .OrderBy(entry => RiskRank(entry.RiskLevel))
            .ThenBy(entry => entry.TargetScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return new GuardrailResolution(
            primary?.RiskLevel ?? "low",
            primary?.Summary ?? "No escalated guardrail recorded.",
            matches.SelectMany(entry => entry.EvidenceLinks)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static PlaybookSuggestionCandidate? SelectPrimary(
        IReadOnlyList<PlaybookSuggestionCandidate> candidates,
        Func<PlaybookSuggestionCandidate, bool>? preferredMatch = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var viable = FilterViable(candidates.Cast<ISuggestionCandidate>().ToArray()).Cast<PlaybookSuggestionCandidate>().ToArray();
        if (preferredMatch is not null)
        {
            var preferred = viable.Where(preferredMatch).ToArray();
            if (preferred.Length > 0)
            {
                return preferred[0];
            }
        }

        return viable.FirstOrDefault() ?? candidates.FirstOrDefault();
    }

    private static SimulationSuggestionCandidate? SelectPrimary(IReadOnlyList<SimulationSuggestionCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var viable = FilterViable(candidates.Cast<ISuggestionCandidate>().ToArray()).Cast<SimulationSuggestionCandidate>().ToArray();
        return viable.FirstOrDefault() ?? candidates.FirstOrDefault();
    }

    private static T? SelectAlternate<T>(
        IReadOnlyList<T> candidates,
        T? primary,
        Func<T, T, bool> differs)
        where T : class, ISuggestionCandidate
    {
        if (primary is null || candidates.Count == 0)
        {
            return null;
        }

        var viable = FilterViable(candidates.Cast<ISuggestionCandidate>().ToArray()).Cast<T>().ToArray();
        return viable.FirstOrDefault(candidate =>
                   !string.Equals(candidate.TargetId, primary.TargetId, StringComparison.OrdinalIgnoreCase) &&
                   differs(candidate, primary))
               ?? viable.FirstOrDefault(candidate =>
                   !string.Equals(candidate.TargetId, primary.TargetId, StringComparison.OrdinalIgnoreCase));
    }

    private static ISuggestionCandidate[] FilterViable(IReadOnlyList<ISuggestionCandidate> candidates)
    {
        var viable = candidates
            .Where(entry => !string.Equals(entry.ConstraintStatus, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.SelectionScore)
            .ThenBy(entry => RiskRank(entry.RiskLevel))
            .ThenBy(entry => entry.TargetTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (viable.Length == 0)
        {
            return candidates
                .OrderByDescending(entry => entry.SelectionScore)
                .ThenBy(entry => RiskRank(entry.RiskLevel))
                .ThenBy(entry => entry.TargetTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var nonCritical = viable
            .Where(entry => !string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return nonCritical.Length > 0 ? nonCritical : viable;
    }

    private static BuilderAutoSuggestionDivergenceRecord? BuildLatestDecisionDivergence(
        BuilderOperatorDecisionsRecord? decisions,
        PlaybookSuggestionCandidate? primaryPlaybook,
        SimulationSuggestionCandidate? primarySimulation)
    {
        var latestDecision = decisions?.Decisions
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
        if (latestDecision is null)
        {
            return null;
        }

        var recommendedTargetType = primarySimulation is not null ? "simulation" : primaryPlaybook is not null ? "playbook" : string.Empty;
        var recommendedTargetId = primarySimulation?.SimulationId ?? primaryPlaybook?.PlaybookId ?? string.Empty;
        var decisionTargetType = !string.IsNullOrWhiteSpace(latestDecision.SimulationId) ? "simulation" : "playbook";
        var decisionTargetId = !string.IsNullOrWhiteSpace(latestDecision.SimulationId)
            ? latestDecision.SimulationId
            : latestDecision.PlaybookId;
        var diverged = !string.IsNullOrWhiteSpace(recommendedTargetId) &&
                       !string.Equals(decisionTargetId, recommendedTargetId, StringComparison.OrdinalIgnoreCase);
        var summary = string.IsNullOrWhiteSpace(recommendedTargetId)
            ? $"Latest decision {latestDecision.DecisionId} has no current primary recommendation baseline."
            : diverged
                ? $"Latest decision {latestDecision.DecisionId} targeted {FormatToken(decisionTargetType)} {decisionTargetId} instead of recommended {FormatToken(recommendedTargetType)} {recommendedTargetId}."
                : $"Latest decision {latestDecision.DecisionId} aligned with recommended {FormatToken(recommendedTargetType)} {recommendedTargetId}.";
        return new BuilderAutoSuggestionDivergenceRecord(
            latestDecision.DecisionId,
            diverged,
            decisionTargetType,
            decisionTargetId,
            summary);
    }

    private static string BuildCurrentSelectionSummary(
        BuilderExecutionReadinessRecord? readiness,
        BuilderRecoveryComparisonsRecord? comparisons,
        PlaybookSuggestionCandidate? primaryPlaybook,
        SimulationSuggestionCandidate? primarySimulation)
    {
        if (readiness is null)
        {
            return "No readiness selection is recorded, so suggestion alignment is being evaluated at workspace scope only.";
        }

        var primaryPlaybookId = primaryPlaybook?.PlaybookId ?? string.Empty;
        var primarySimulationId = primarySimulation?.SimulationId ?? string.Empty;
        var matches = readiness.SelectionTargetType switch
        {
            "simulation" => string.Equals(readiness.SelectionTargetId, primarySimulationId, StringComparison.OrdinalIgnoreCase),
            "playbook" => string.Equals(readiness.SelectionTargetId, primaryPlaybookId, StringComparison.OrdinalIgnoreCase),
            "comparison" => comparisons?.ComparisonSets.Any(set =>
                string.Equals(set.ComparisonId, readiness.SelectionTargetId, StringComparison.OrdinalIgnoreCase) &&
                set.ComparisonMetrics.Any(metric =>
                    string.Equals(metric.SimulationId, primarySimulationId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(metric.PlaybookId, primaryPlaybookId, StringComparison.OrdinalIgnoreCase))) == true,
            _ => false
        };

        var recommendedId = !string.IsNullOrWhiteSpace(primarySimulationId) ? primarySimulationId : primaryPlaybookId;
        return matches
            ? $"Current {FormatToken(readiness.SelectionTargetType)} selection {readiness.SelectionTargetId} aligns with the primary recommendation."
            : $"Current {FormatToken(readiness.SelectionTargetType)} selection {readiness.SelectionTargetId} differs from the primary recommendation {recommendedId}.";
    }

    private static string BuildSummary(
        string workspaceId,
        PlaybookSuggestionCandidate? primaryPlaybook,
        SimulationSuggestionCandidate? primarySimulation,
        SimulationSuggestionCandidate? alternateSimulation,
        string currentSelectionSummary,
        BuilderAutoSuggestionDivergenceRecord? divergence)
    {
        var primarySummary = primarySimulation is not null
            ? $"Primary suggestion: {primarySimulation.PlaybookTitle} / {FormatToken(primarySimulation.Scenario)}."
            : primaryPlaybook is not null
                ? $"Primary suggestion: {primaryPlaybook.PlaybookTitle}."
                : "No primary suggestion is currently available.";
        var alternateSummary = alternateSimulation is null
            ? "No alternate suggestion is currently available."
            : $"Alternate suggestion: {alternateSimulation.PlaybookTitle} / {FormatToken(alternateSimulation.Scenario)}.";
        var divergenceSummary = divergence?.Summary ?? "No operator-decision divergence is currently recorded.";
        return $"Generated zero-execution suggestion set for {workspaceId}. {primarySummary} {alternateSummary} {currentSelectionSummary} {divergenceSummary}";
    }

    private static string ResolvePlaybookTradeoffLabel(BuilderRecoveryPlaybookRecord playbook, SimulationSuggestionCandidate? bestSimulation)
    {
        if (playbook.CrossRepoScope)
        {
            return "broader cross-repo recovery";
        }

        if (string.Equals(playbook.FailureClass, "patch_rejected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bestSimulation?.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase))
        {
            return "narrower safer recovery";
        }

        if (string.Equals(bestSimulation?.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase))
        {
            return "faster retry loop";
        }

        return "balanced recovery default";
    }

    private static string ResolveSimulationTradeoffLabel(BuilderRecoverySimulationRecord simulation, BuilderRecoveryPlaybookRecord playbook)
        => simulation.Scenario switch
        {
            "retry_same_route" => "faster retry loop",
            "switch_route_manual" => "safer route change",
            "reduce_scope" => "minimal change surface",
            "staged_orchestration" => playbook.CrossRepoScope ? "staged cross-repo recovery" : "staged recovery",
            "isolate_high_risk_files" => "high-risk isolation",
            _ => "balanced recovery path"
        };

    private static string ResolveBranchId(
        BuilderRecoveryPlaybookRecord playbook,
        BuilderRecoverySimulationRecord simulation,
        BuilderPlaybookRankingRecord? ranking)
    {
        if (!string.IsNullOrWhiteSpace(ranking?.SelectedIntent))
        {
            return ranking.SelectedIntent;
        }

        return simulation.Scenario switch
        {
            "retry_same_route" => BuilderOperatorIntentService.FastRecoveryIntent,
            "switch_route_manual" => BuilderOperatorIntentService.SafeRecoveryIntent,
            "reduce_scope" => BuilderOperatorIntentService.MinimalChangeIntent,
            "staged_orchestration" => BuilderOperatorIntentService.UnblockOrchestrationIntent,
            "isolate_high_risk_files" => BuilderOperatorIntentService.SafeRecoveryIntent,
            _ => playbook.CrossRepoScope ? BuilderOperatorIntentService.UnblockOrchestrationIntent : BuilderOperatorIntentService.FullResolutionIntent
        };
    }

    private static double ResolveFailureLikelihoodProbability(string failureLikelihood)
        => failureLikelihood switch
        {
            "high" => 0.80d,
            "medium" => 0.55d,
            "low" => 0.25d,
            _ => 0.40d
        };

    private static double ResolvePredictedSignal(BuilderRecoverySimulationRecord simulation)
    {
        var success = simulation.SuccessLikelihood switch
        {
            "high" => 0.85d,
            "medium" => 0.65d,
            "low" => 0.35d,
            _ => 0.50d
        };
        return Math.Round(((success + Clamp01(simulation.ConfidenceScore)) / 2d) * 100d, 2);
    }

    private static double ResolveConfidenceSignal(
        BuilderRecoverySimulationRecord simulation,
        BuilderSimulationCalibrationRecord? scenarioCalibration,
        BuilderRecoveryComparisonMetricRecord? comparisonMetric)
    {
        var calibration = scenarioCalibration?.CalibratedConfidence ?? comparisonMetric?.CalibratedConfidence ?? simulation.ConfidenceLevel;
        return calibration switch
        {
            "high_confidence" => 0.85d,
            "low_confidence" => 0.35d,
            "unstable_confidence" => 0.55d,
            "high" => 0.80d,
            "medium" => 0.60d,
            "low" => 0.35d,
            _ => Clamp01(simulation.ConfidenceScore)
        };
    }

    private static double ResolveReadinessSignal(
        BuilderExecutionReadinessRecord? readiness,
        string targetType,
        string targetId)
    {
        if (readiness is null)
        {
            return 0.60d;
        }

        var baseline = readiness.ReadinessState switch
        {
            "go" => 0.90d,
            "no_go" => 0.35d,
            _ => 0.60d
        };
        var currentSelectionPenalty = string.Equals(readiness.SelectionTargetType, targetType, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(readiness.SelectionTargetId, targetId, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(readiness.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase)
            ? 0.15d
            : 0d;
        return Clamp01(baseline - currentSelectionPenalty);
    }

    private static double ResolveRiskPenalty(string guardrailRiskLevel, string simulationRiskLevel = "")
    {
        var guardrailPenalty = guardrailRiskLevel switch
        {
            "critical" => 0.25d,
            "high" => 0.12d,
            "moderate" => 0.05d,
            _ => 0d
        };
        var simulationPenalty = simulationRiskLevel switch
        {
            "critical" => 0.15d,
            "high" => 0.08d,
            "moderate" => 0.03d,
            _ => 0d
        };
        return guardrailPenalty + simulationPenalty;
    }

    private static string ResolveConfidenceLabel(
        double accuracySignal,
        double outcomeSignal,
        string riskLevel,
        string preferredLabel = "")
    {
        if (!string.IsNullOrWhiteSpace(preferredLabel) &&
            (string.Equals(preferredLabel, "high_confidence", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(preferredLabel, "low_confidence", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(preferredLabel, "unstable_confidence", StringComparison.OrdinalIgnoreCase)))
        {
            return preferredLabel;
        }

        if (accuracySignal >= 0.75d &&
            outcomeSignal >= 0.70d &&
            RiskRank(riskLevel) >= 2)
        {
            return "high_confidence";
        }

        if (accuracySignal <= 0.45d || outcomeSignal <= 0.45d || string.Equals(riskLevel, "critical", StringComparison.OrdinalIgnoreCase))
        {
            return "low_confidence";
        }

        return "unstable_confidence";
    }

    private static string ResolveHighestRiskLevel(string guardrailRiskLevel, string simulationRiskLevel)
        => RiskRank(guardrailRiskLevel) <= RiskRank(simulationRiskLevel)
            ? guardrailRiskLevel
            : string.IsNullOrWhiteSpace(simulationRiskLevel) ? guardrailRiskLevel : simulationRiskLevel;

    private static IReadOnlyList<string> BuildEvidenceLinks(params IEnumerable<string>?[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(source => source!)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int ConstraintCompatibilityRank(string constraintCompatibility)
        => string.Equals(constraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static int SuggestionKindRank(string suggestionKind)
        => string.Equals(suggestionKind, "primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int TargetTypeRank(string targetType)
        => string.Equals(targetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

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
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');

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

    private readonly record struct GuardrailResolution(
        string RiskLevel,
        string Summary,
        IReadOnlyList<string> EvidenceLinks);

    private interface ISuggestionCandidate
    {
        string TargetId { get; }

        string TargetTitle { get; }

        double SelectionScore { get; }

        string RiskLevel { get; }

        string ConstraintStatus { get; }
    }

    private sealed record PlaybookSuggestionCandidate(
        string PlaybookId,
        string PlaybookTitle,
        string FailureClass,
        string RepoScope,
        string SimulationId,
        double SelectionScore,
        string Confidence,
        string RiskLevel,
        string TradeoffLabel,
        string ConstraintStatus,
        string GuardrailStatus,
        string ReadinessState,
        string CalibrationProfile,
        string SignalBalanceSummary,
        IReadOnlyList<BuilderSignalContributionRecord> SignalContributions,
        string SelectionReason,
        IReadOnlyList<string> SupportingEvidence) : ISuggestionCandidate
    {
        public string TargetId => PlaybookId;
        public string TargetTitle => PlaybookTitle;

        public BuilderAutoSuggestionRecord ToRecord(string suggestionKind)
            => new(
                ComputeDeterministicId("suggestion", suggestionKind, "playbook", PlaybookId, SimulationId),
                suggestionKind,
                "playbook",
                PlaybookId,
                PlaybookId,
                SimulationId,
                SelectionScore,
                Confidence,
                RiskLevel,
                TradeoffLabel,
                ConstraintStatus,
                GuardrailStatus,
                ReadinessState,
                CalibrationProfile,
                SignalBalanceSummary,
                SignalContributions,
                SelectionReason,
                SupportingEvidence);
    }

    private sealed record SimulationSuggestionCandidate(
        string SimulationId,
        string PlaybookId,
        string PlaybookTitle,
        string Scenario,
        string BranchId,
        double SelectionScore,
        string Confidence,
        string RiskLevel,
        string TradeoffLabel,
        string ConstraintStatus,
        string GuardrailStatus,
        string ReadinessState,
        string CalibrationProfile,
        string SignalBalanceSummary,
        IReadOnlyList<BuilderSignalContributionRecord> SignalContributions,
        string SelectionReason,
        IReadOnlyList<string> SupportingEvidence) : ISuggestionCandidate
    {
        public string TargetId => SimulationId;
        public string TargetTitle => $"{PlaybookTitle}/{Scenario}";

        public BuilderAutoSuggestionRecord ToRecord(string suggestionKind)
            => new(
                ComputeDeterministicId("suggestion", suggestionKind, "simulation", PlaybookId, SimulationId),
                suggestionKind,
                "simulation",
                SimulationId,
                PlaybookId,
                SimulationId,
                SelectionScore,
                Confidence,
                RiskLevel,
                TradeoffLabel,
                ConstraintStatus,
                GuardrailStatus,
                ReadinessState,
                CalibrationProfile,
                SignalBalanceSummary,
                SignalContributions,
                SelectionReason,
                SupportingEvidence);
    }

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"suggestion-{hash[..10]}";
    }
}
