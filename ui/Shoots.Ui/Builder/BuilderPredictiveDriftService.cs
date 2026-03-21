using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderPredictiveDriftEvidenceStepRecord(
    string StepId,
    string InputSource,
    string AppliedRule,
    string IntermediateResult);

public sealed record BuilderPredictiveDriftRecord(
    string PredictionId,
    string TargetId,
    string TargetType,
    string DriftTrend,
    double FailureProbability,
    string RiskEscalation,
    IReadOnlyList<BuilderPredictiveDriftEvidenceStepRecord> EvidenceChain,
    IReadOnlyList<string> LinkedArtifacts,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPredictiveDriftReport(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPredictiveDriftRecord> Predictions,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderPredictiveDriftService
{
    public const string PredictiveDriftFileName = "builder_predictive_drift.json";

    private const string SchemaVersion = "builder_predictive_drift.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string PredictiveDriftPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), PredictiveDriftFileName);

    public static BuilderPredictiveDriftReport? LoadPredictiveDrift(string repoRoot)
        => Load<BuilderPredictiveDriftReport>(PredictiveDriftPathForRepo(repoRoot));

    public static BuilderPredictiveDriftReport? RefreshPredictiveDrift(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderRecoveryComparisonsRecord? comparisons = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderAutoSuggestionsRecord? suggestions = null,
        BuilderTrustIndexRecord? trust = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderExecutionAuditReport? audit = null,
        BuilderExecutionReadinessRecord? readiness = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        comparisons ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        suggestions ??= BuilderAutoSuggestionService.LoadAutoSuggestions(repoRoot);
        readiness ??= BuilderExecutionReadinessService.LoadExecutionReadiness(repoRoot);
        audit ??= BuilderExecutionAuditService.LoadExecutionAudit(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        trust ??= BuilderTrustIndexService.LoadTrustIndex(repoRoot) ??
                 BuilderTrustIndexService.RefreshTrustIndex(
                     repoRoot,
                     playbooks,
                     simulations,
                     accuracy,
                     decisions,
                     suggestions,
                     readiness,
                     guardrails,
                     audit,
                     observedUtc);

        if (playbooks is null &&
            simulations is null &&
            comparisons is null &&
            accuracy is null &&
            decisions is null &&
            suggestions is null &&
            trust is null &&
            guardrails is null &&
            audit is null)
        {
            return null;
        }

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var observed = observedUtc ?? DateTimeOffset.UtcNow;
        var auditRecords = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .OrderBy(record => record.ObservedUtc)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.AuditId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accuracyRecords = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .OrderBy(record => record.ObservedUtc)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.RecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var decisionRecords = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .OrderBy(record => record.Timestamp)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var playbookSimulations = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .GroupBy(record => record.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var predictions = new List<BuilderPredictiveDriftRecord>();

        foreach (var playbook in playbooks?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
        {
            playbookSimulations.TryGetValue(playbook.PlaybookId, out var relatedSimulations);
            relatedSimulations ??= Array.Empty<BuilderRecoverySimulationRecord>();
            var simulationIds = relatedSimulations
                .Select(record => record.SimulationId)
                .Concat(playbook.SimulationIds)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            predictions.Add(BuildPrediction(
                repoRoot,
                workspaceId,
                targetType: "playbook",
                targetId: playbook.PlaybookId,
                route: playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty,
                audits: auditRecords.Where(record =>
                    string.Equals(record.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
                    simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                accuracyRecords: accuracyRecords.Where(record => simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                decisions: decisionRecords.Where(record =>
                    string.Equals(record.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
                    simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                playbookId: playbook.PlaybookId,
                simulationId: string.Empty,
                trustProfiles: BuilderTrustIndexService.ResolveMatchingProfiles(trust, playbook.PlaybookId),
                divergence: ResolveDivergence(suggestions, playbook.PlaybookId, simulationIds),
                additionalEvidenceLinks: playbook.ArtifactLinks,
                matchingGuardrails: ResolveGuardrailMatches(
                    guardrails,
                    workspaceId,
                    playbook.PlaybookId,
                    string.Empty,
                    playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty),
                observedUtc: observed));
        }

        foreach (var simulation in simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
        {
            predictions.Add(BuildPrediction(
                repoRoot,
                workspaceId,
                targetType: "simulation",
                targetId: simulation.SimulationId,
                route: simulation.TargetRoute,
                audits: auditRecords.Where(record => string.Equals(record.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)),
                accuracyRecords: accuracyRecords.Where(record => string.Equals(record.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)),
                decisions: decisionRecords.Where(record => string.Equals(record.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)),
                playbookId: simulation.PlaybookId,
                simulationId: simulation.SimulationId,
                trustProfiles: BuilderTrustIndexService.ResolveMatchingProfiles(trust, simulation.PlaybookId, simulation.SimulationId),
                divergence: ResolveDivergence(suggestions, simulation.PlaybookId, new[] { simulation.SimulationId }),
                additionalEvidenceLinks: simulation.ArtifactLinks,
                matchingGuardrails: ResolveGuardrailMatches(
                    guardrails,
                    workspaceId,
                    simulation.PlaybookId,
                    simulation.SimulationId,
                    simulation.TargetRoute),
                observedUtc: observed));
        }

        foreach (var comparison in comparisons?.ComparisonSets ?? Array.Empty<BuilderRecoveryComparisonSetRecord>())
        {
            var simulationIds = comparison.SimulationIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var playbookIds = comparison.PlaybookIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matchingGuardrails = comparison.ComparisonMetrics
                .SelectMany(metric => ResolveGuardrailMatches(guardrails, workspaceId, metric.PlaybookId, metric.SimulationId, string.Empty))
                .GroupBy(record => record.GuardrailId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var trustProfiles = (trust?.TargetProfiles ?? Array.Empty<BuilderTrustTargetProfileRecord>())
                .Where(record =>
                    string.Equals(record.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                    playbookIds.Contains(record.TargetId, StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(record.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                    simulationIds.Contains(record.TargetId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            predictions.Add(BuildPrediction(
                repoRoot,
                workspaceId,
                targetType: "comparison",
                targetId: comparison.ComparisonId,
                route: string.Empty,
                audits: auditRecords.Where(record =>
                    playbookIds.Contains(record.PlaybookId, StringComparer.OrdinalIgnoreCase) ||
                    simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                accuracyRecords: accuracyRecords.Where(record => simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                decisions: decisionRecords.Where(record =>
                    playbookIds.Contains(record.PlaybookId, StringComparer.OrdinalIgnoreCase) ||
                    simulationIds.Contains(record.SimulationId, StringComparer.OrdinalIgnoreCase)),
                playbookId: string.Empty,
                simulationId: string.Empty,
                trustProfiles: trustProfiles,
                divergence: ResolveDivergence(suggestions, playbookIds, simulationIds),
                additionalEvidenceLinks: comparison.ComparisonMetrics.SelectMany(metric => metric.EvidenceLinks),
                matchingGuardrails: matchingGuardrails,
                observedUtc: observed));
        }

        var orderedPredictions = predictions
            .GroupBy(record => $"{record.TargetType}|{record.TargetId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(record => RiskRank(record.RiskEscalation))
                .ThenByDescending(record => record.FailureProbability)
                .ThenBy(record => record.PredictionId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(record => TargetTypeRank(record.TargetType))
            .ThenBy(record => RiskRank(record.RiskEscalation))
            .ThenByDescending(record => record.FailureProbability)
            .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.PredictionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new BuilderPredictiveDriftReport(
            workspaceId,
            SchemaVersion,
            orderedPredictions,
            true,
            BuildSummary(workspaceId, orderedPredictions),
            PredictiveDriftPathForRepo(repoRoot),
            observed);
        Save(report.ArtifactPath, report);
        return report;
    }

    public static IReadOnlyList<BuilderPredictiveDriftRecord> ResolveMatchingPredictions(
        BuilderPredictiveDriftReport? report,
        string playbookId = "",
        string simulationId = "",
        string comparisonId = "")
    {
        if (report is null)
        {
            return Array.Empty<BuilderPredictiveDriftRecord>();
        }

        return report.Predictions
            .Where(record =>
                string.Equals(record.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, playbookId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, simulationId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.TargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.TargetId, comparisonId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => RiskRank(record.RiskEscalation))
            .ThenByDescending(record => record.FailureProbability)
            .ThenBy(record => record.TargetType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.PredictionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderPredictiveDriftRecord BuildPrediction(
        string repoRoot,
        string workspaceId,
        string targetType,
        string targetId,
        string route,
        IEnumerable<BuilderExecutionAuditRecord> audits,
        IEnumerable<BuilderSimulationAccuracyRecord> accuracyRecords,
        IEnumerable<BuilderOperatorDecisionRecord> decisions,
        string playbookId,
        string simulationId,
        IEnumerable<BuilderTrustTargetProfileRecord> trustProfiles,
        BuilderAutoSuggestionDivergenceRecord? divergence,
        IEnumerable<string> additionalEvidenceLinks,
        IEnumerable<BuilderPreventativeGuardrailRecord> matchingGuardrails,
        DateTimeOffset observedUtc)
    {
        var orderedAudits = audits
            .OrderBy(record => record.ObservedUtc)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.AuditId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedAccuracy = accuracyRecords
            .OrderBy(record => record.ObservedUtc)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.RecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedDecisions = decisions
            .OrderBy(record => record.Timestamp)
            .ThenBy(record => record.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var guardrails = matchingGuardrails
            .GroupBy(record => record.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(record => RiskRank(record.RiskLevel))
            .ThenBy(record => record.TargetScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trust = trustProfiles
            .OrderByDescending(record => record.TrustScore)
            .ThenBy(record => record.TargetType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var minorDriftCount = orderedAudits.Count(record => string.Equals(record.DriftType, "minor_drift", StringComparison.OrdinalIgnoreCase));
        var majorDriftCount = orderedAudits.Count(record =>
            string.Equals(record.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        var unexpectedSuccessCount = orderedAudits.Count(record => string.Equals(record.DriftType, "unexpected_success", StringComparison.OrdinalIgnoreCase));
        var stableAuditCount = orderedAudits.Count(record => string.Equals(record.DriftType, "no_drift", StringComparison.OrdinalIgnoreCase));
        var mismatchCount = orderedAccuracy.Count(record => !record.AccuracyFlag);
        var failureDecisionCount = orderedDecisions.Count(record => !record.SuccessFlag);
        var trustScore = trust.Length == 0 ? 50d : trust.Average(record => record.TrustScore);
        var driftPressure = orderedAudits.Length == 0
            ? 0.25d
            : Clamp01(((minorDriftCount * 0.45d) + (majorDriftCount * 1.00d) + (unexpectedSuccessCount * 0.20d)) /
                      Math.Max(1d, orderedAudits.Length * 1.15d));
        var mismatchRate = orderedAccuracy.Length == 0 ? 0.25d : mismatchCount / (double)orderedAccuracy.Length;
        var failureRate = orderedDecisions.Length == 0 ? 0.25d : failureDecisionCount / (double)orderedDecisions.Length;
        var guardrailRisk = guardrails.FirstOrDefault()?.RiskLevel ?? string.Empty;
        var stabilityCredit = orderedAudits.Length == 0 ? 0d : stableAuditCount / (double)orderedAudits.Length;
        var failureProbability = Clamp01(Math.Round(
            0.05d +
            (0.30d * driftPressure) +
            (0.20d * mismatchRate) +
            (0.15d * failureRate) +
            (0.15d * RiskPressure(guardrailRisk)) +
            (0.15d * (trust.Length == 0 ? 0.50d : Clamp01(1d - (trustScore / 100d)))) +
            (divergence is null ? 0d : 0.15d) -
            (0.10d * stabilityCredit),
            4));

        var driftTrend = DetermineTrend(minorDriftCount, majorDriftCount, mismatchCount, orderedAudits, orderedDecisions, guardrailRisk, failureProbability, trustScore);
        if (string.Equals(driftTrend, "critical_trajectory", StringComparison.OrdinalIgnoreCase))
        {
            failureProbability = Math.Max(failureProbability, 0.75d);
        }

        var riskEscalation = DetermineRiskEscalation(driftTrend, failureProbability, guardrailRisk);
        var evidenceChain = BuildEvidenceChain(
            repoRoot,
            targetType,
            orderedAudits,
            orderedAccuracy,
            orderedDecisions,
            guardrails,
            trust,
            divergence,
            driftTrend,
            failureProbability,
            riskEscalation,
            minorDriftCount,
            majorDriftCount,
            mismatchCount,
            trustScore);
        var linkedArtifacts = BuildArtifactLinks(
            new[] { BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot) },
            new[] { BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot) },
            new[] { BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot) },
            new[] { BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoRoot) },
            new[] { BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot) },
            new[] { BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot) },
            new[] { BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot) },
            new[] { BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot) },
            new[] { BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot) },
            additionalEvidenceLinks,
            orderedAudits.SelectMany(record => record.LinkedArtifacts),
            orderedAccuracy.SelectMany(record => record.ArtifactLinks),
            orderedDecisions.SelectMany(record => record.TriggerArtifacts),
            orderedDecisions.SelectMany(record => record.ResultArtifacts),
            guardrails.SelectMany(record => record.EvidenceLinks),
            trust.SelectMany(record => record.EvidenceLinks));
        var predictionId = ComputeDeterministicId(
            targetType,
            targetId,
            driftTrend,
            riskEscalation,
            failureProbability.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            workspaceId,
            playbookId,
            simulationId,
            route);

        return new BuilderPredictiveDriftRecord(
            predictionId,
            targetId,
            targetType,
            driftTrend,
            Math.Round(failureProbability, 4),
            riskEscalation,
            evidenceChain,
            linkedArtifacts,
            BuildPredictionSummary(targetType, targetId, driftTrend, failureProbability, riskEscalation, majorDriftCount, minorDriftCount, mismatchCount, trustScore),
            observedUtc);
    }

    private static BuilderAutoSuggestionDivergenceRecord? ResolveDivergence(
        BuilderAutoSuggestionsRecord? suggestions,
        string playbookId,
        IReadOnlyList<string> simulationIds)
        => suggestions?.LatestDecisionDivergence is { DivergedFromPrimary: true } divergence &&
           (string.Equals(divergence.DecisionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(divergence.DecisionTargetId, playbookId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(divergence.DecisionTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
            simulationIds.Contains(divergence.DecisionTargetId, StringComparer.OrdinalIgnoreCase))
            ? divergence
            : null;

    private static BuilderAutoSuggestionDivergenceRecord? ResolveDivergence(
        BuilderAutoSuggestionsRecord? suggestions,
        IReadOnlyList<string> playbookIds,
        IReadOnlyList<string> simulationIds)
        => suggestions?.LatestDecisionDivergence is { DivergedFromPrimary: true } divergence &&
           (string.Equals(divergence.DecisionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
            playbookIds.Contains(divergence.DecisionTargetId, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(divergence.DecisionTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
            simulationIds.Contains(divergence.DecisionTargetId, StringComparer.OrdinalIgnoreCase))
            ? divergence
            : null;

    private static IReadOnlyList<BuilderPreventativeGuardrailRecord> ResolveGuardrailMatches(
        BuilderPreventativeGuardrailsReport? report,
        string workspaceId,
        string playbookId,
        string simulationId,
        string route)
        => BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
            report,
            playbookId,
            simulationId,
            route,
            workspaceId);

    private static string DetermineTrend(
        int minorDriftCount,
        int majorDriftCount,
        int mismatchCount,
        IReadOnlyList<BuilderExecutionAuditRecord> audits,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions,
        string guardrailRisk,
        double failureProbability,
        double trustScore)
    {
        var mixedResults = audits.Select(record => record.DriftType).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
                           decisions.Any(record => record.SuccessFlag) && decisions.Any(record => !record.SuccessFlag);
        if (majorDriftCount >= 2 ||
            string.Equals(guardrailRisk, "critical", StringComparison.OrdinalIgnoreCase) ||
            failureProbability >= 0.85d)
        {
            return "critical_trajectory";
        }

        if (minorDriftCount >= 2 ||
            majorDriftCount >= 1 ||
            mismatchCount >= 1 && trustScore < 60d)
        {
            return "degrading";
        }

        if (mixedResults || mismatchCount >= 1)
        {
            return "unstable";
        }

        return "stable";
    }

    private static string DetermineRiskEscalation(string driftTrend, double failureProbability, string guardrailRisk)
    {
        if (string.Equals(driftTrend, "critical_trajectory", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guardrailRisk, "critical", StringComparison.OrdinalIgnoreCase) ||
            failureProbability >= 0.80d)
        {
            return "critical";
        }

        if (string.Equals(guardrailRisk, "high", StringComparison.OrdinalIgnoreCase) ||
            failureProbability >= 0.60d)
        {
            return "high";
        }

        if (string.Equals(driftTrend, "degrading", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driftTrend, "unstable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guardrailRisk, "moderate", StringComparison.OrdinalIgnoreCase) ||
            failureProbability >= 0.35d)
        {
            return "moderate";
        }

        return "low";
    }

    private static BuilderPredictiveDriftEvidenceStepRecord[] BuildEvidenceChain(
        string repoRoot,
        string targetType,
        IReadOnlyList<BuilderExecutionAuditRecord> audits,
        IReadOnlyList<BuilderSimulationAccuracyRecord> accuracyRecords,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions,
        IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrails,
        IReadOnlyList<BuilderTrustTargetProfileRecord> trustProfiles,
        BuilderAutoSuggestionDivergenceRecord? divergence,
        string driftTrend,
        double failureProbability,
        string riskEscalation,
        int minorDriftCount,
        int majorDriftCount,
        int mismatchCount,
        double trustScore)
        => new[]
        {
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-01-audit-history",
                BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
                "aggregate_drift_history",
                $"{audits.Count} audit record(s): {minorDriftCount} minor drift, {majorDriftCount} major or unexpected failure, trend candidate {FormatToken(driftTrend)}."),
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-02-simulation-accuracy",
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                "compare_predictions_to_outcomes",
                accuracyRecords.Count == 0
                    ? "No completed simulation accuracy history exists, so a neutral mismatch baseline is applied."
                    : $"{mismatchCount} mismatch record(s) across {accuracyRecords.Count} accuracy comparison(s)."),
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-03-guardrails",
                BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot),
                "amplify_known_risk_patterns",
                $"{guardrails.Count} matching guardrail(s) with maximum escalation {FormatToken(guardrails.FirstOrDefault()?.RiskLevel ?? "not_recorded")} for {FormatToken(targetType)}."),
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-04-trust-profile",
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                "apply_trust_penalty",
                trustProfiles.Count == 0
                    ? "No target trust profile exists, so the predictive model uses a neutral trust baseline."
                    : $"Trust score averages {trustScore:0.##} across {trustProfiles.Count} matching trust profile(s)."),
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-05-divergence",
                BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoRoot),
                "include_recent_suggestion_divergence",
                divergence is null
                    ? "No recent recommendation divergence is attached to this target."
                    : $"Latest divergence followed {FormatToken(divergence.DecisionTargetType)} {divergence.DecisionTargetId} instead of the current primary recommendation."),
            new BuilderPredictiveDriftEvidenceStepRecord(
                "step-06-probability",
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                "calculate_failure_probability",
                $"{decisions.Count} decision(s) produce {failureProbability:P0} failure likelihood, {FormatToken(driftTrend)} drift trend, and {FormatToken(riskEscalation)} escalation.")
        };

    private static string BuildPredictionSummary(
        string targetType,
        string targetId,
        string driftTrend,
        double failureProbability,
        string riskEscalation,
        int majorDriftCount,
        int minorDriftCount,
        int mismatchCount,
        double trustScore)
        => $"{FormatToken(targetType)} {targetId} is on a {FormatToken(driftTrend)} path with {failureProbability:P0} forecast failure likelihood. Escalation is {FormatToken(riskEscalation)} based on {majorDriftCount} major drift event(s), {minorDriftCount} minor drift event(s), {mismatchCount} simulation mismatch(es), and trust score {trustScore:0.##}.";

    private static string BuildSummary(string workspaceId, IReadOnlyList<BuilderPredictiveDriftRecord> predictions)
    {
        if (predictions.Count == 0)
        {
            return $"No predictive drift signals are currently recorded for {workspaceId}.";
        }

        var criticalCount = predictions.Count(record => string.Equals(record.RiskEscalation, "critical", StringComparison.OrdinalIgnoreCase));
        var degradingCount = predictions.Count(record =>
            string.Equals(record.DriftTrend, "degrading", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.DriftTrend, "critical_trajectory", StringComparison.OrdinalIgnoreCase));
        return $"Forecasted {predictions.Count} predictive drift signal(s) for {workspaceId}. Critical risks: {criticalCount}. Degrading paths: {degradingCount}.";
    }

    private static IReadOnlyList<string> BuildArtifactLinks(params IEnumerable<string>?[] sets)
        => sets
            .Where(set => set is not null)
            .SelectMany(set => set!)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static double RiskPressure(string riskLevel)
        => riskLevel switch
        {
            "critical" => 0.65d,
            "high" => 0.45d,
            "moderate" => 0.25d,
            "low" => 0.10d,
            _ => 0.05d
        };

    private static int RiskRank(string riskLevel)
        => riskLevel switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };

    private static int TargetTypeRank(string targetType)
        => targetType switch
        {
            "playbook" => 0,
            "simulation" => 1,
            "comparison" => 2,
            _ => 3
        };

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"forecast-{hash[..10]}";
    }

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
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, SerializerOptions));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (IOException)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(path, _ => new object());
}
