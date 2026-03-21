using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderTrustMetricRecord(
    string MetricId,
    string MetricName,
    double Score,
    int SampleSize,
    string Summary,
    IReadOnlyList<string> EvidenceLinks)
{
    public string DisplayName => FormatToken(MetricName);

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderTrustTargetProfileRecord(
    string TargetType,
    string TargetId,
    double TrustScore,
    string ConfidenceProfile,
    double OperatorAlignmentScore,
    int SampleSize,
    int DecisionCount,
    int AccuracyRecordCount,
    int AuditCount,
    string Summary,
    IReadOnlyList<string> EvidenceLinks)
{
    public string DisplayLabel => $"{FormatToken(TargetType)} {TargetId}";

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderTrustIndexRecord(
    string WorkspaceId,
    string SchemaVersion,
    double TrustScore,
    string ConfidenceProfile,
    double OperatorAlignmentScore,
    IReadOnlyList<BuilderTrustMetricRecord> Metrics,
    IReadOnlyList<BuilderTrustTargetProfileRecord> TargetProfiles,
    IReadOnlyList<string> EvidenceLinks,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderTrustIndexService
{
    public const string TrustIndexFileName = "builder_trust_index.json";

    private const string SchemaVersion = "builder_trust_index.v1";
    private const double AccuracyWeight = 0.30d;
    private const double DriftWeight = 0.25d;
    private const double SuggestionValueWeight = 0.20d;
    private const double OperatorAlignmentWeight = 0.15d;
    private const double StabilityWeight = 0.10d;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string TrustIndexPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), TrustIndexFileName);

    public static BuilderTrustIndexRecord? LoadTrustIndex(string repoRoot)
        => Load<BuilderTrustIndexRecord>(TrustIndexPathForRepo(repoRoot));

    public static BuilderTrustIndexRecord? RefreshTrustIndex(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderAutoSuggestionsRecord? suggestions = null,
        BuilderExecutionReadinessRecord? readiness = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderExecutionAuditReport? audit = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        readiness ??= BuilderExecutionReadinessService.LoadExecutionReadiness(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        audit ??= BuilderExecutionAuditService.LoadExecutionAudit(repoRoot);
        suggestions ??= BuilderAutoSuggestionService.LoadAutoSuggestions(repoRoot);
        if (suggestions is null && (playbooks is not null || simulations is not null))
        {
            suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoRoot,
                playbooks,
                simulations,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot),
                BuilderRecoveryComparisonService.LoadRecoveryComparisons(repoRoot),
                accuracy,
                readiness,
                guardrails,
                decisions,
                observedUtc);
        }

        if (playbooks is null &&
            simulations is null &&
            accuracy is null &&
            decisions is null &&
            suggestions is null &&
            readiness is null &&
            guardrails is null &&
            audit is null)
        {
            return null;
        }

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var orderedDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accuracyRecords = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .OrderBy(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var auditRecords = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .OrderBy(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.AuditId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var guardrailRecords = (guardrails?.Guardrails ?? Array.Empty<BuilderPreventativeGuardrailRecord>())
            .OrderBy(entry => entry.TargetScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var primaryPlaybookSuggestion = suggestions?.Suggestions.FirstOrDefault(entry =>
            string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase));
        var primarySimulationSuggestion = suggestions?.Suggestions.FirstOrDefault(entry =>
            string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase));

        var followedDecisions = orderedDecisions
            .Where(decision => MatchesPrimarySuggestion(decision, primaryPlaybookSuggestion, primarySimulationSuggestion))
            .ToArray();
        var ignoredDecisions = orderedDecisions
            .Where(decision => !MatchesPrimarySuggestion(decision, primaryPlaybookSuggestion, primarySimulationSuggestion))
            .ToArray();

        var accuracyRate = accuracyRecords.Length == 0
            ? 0.50d
            : accuracyRecords.Count(entry => entry.AccuracyFlag) / (double)accuracyRecords.Length;
        var driftConsistency = auditRecords.Length == 0
            ? 0.50d
            : auditRecords.Average(ComputeAuditConsistencyScore);
        var followRecommendationSuccessRate = followedDecisions.Length == 0
            ? 0.50d
            : followedDecisions.Count(entry => entry.SuccessFlag) / (double)followedDecisions.Length;
        var ignoredRecommendationFailureRate = ignoredDecisions.Length == 0
            ? 0.50d
            : ignoredDecisions.Count(entry => !entry.SuccessFlag) / (double)ignoredDecisions.Length;
        var suggestionValueScore = Math.Round((followRecommendationSuccessRate + ignoredRecommendationFailureRate) / 2d, 4);
        var operatorAlignmentRate = orderedDecisions.Length == 0
            ? 0.50d
            : orderedDecisions.Count(entry => entry.SuccessFlag) / (double)orderedDecisions.Length;
        var stabilityScore = ComputeStabilityScore(auditRecords, guardrailRecords);
        var sampleSize = Math.Max(orderedDecisions.Length, Math.Max(accuracyRecords.Length, auditRecords.Length));

        var metrics = new[]
            {
                new BuilderTrustMetricRecord(
                    "system_accuracy",
                    "system_accuracy",
                    Math.Round(accuracyRate * 100d, 2),
                    accuracyRecords.Length,
                    accuracyRecords.Length == 0
                        ? "Simulation accuracy is currently using a neutral baseline because no prediction versus outcome history is recorded yet."
                        : $"Simulation accuracy is {accuracyRate:P0} across {accuracyRecords.Length} recorded prediction comparison(s).",
                    BuildEvidenceLinks(
                        new[] { BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot) },
                        accuracyRecords.SelectMany(entry => entry.ArtifactLinks))),
                new BuilderTrustMetricRecord(
                    "audit_drift",
                    "audit_drift",
                    Math.Round(driftConsistency * 100d, 2),
                    auditRecords.Length,
                    auditRecords.Length == 0
                        ? "Audit drift is using a neutral baseline because no post-execution audit history is recorded yet."
                        : $"Audit drift consistency scores {driftConsistency:P0} across {auditRecords.Length} execution audit record(s).",
                    BuildEvidenceLinks(
                        new[] { BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot) },
                        auditRecords.SelectMany(entry => entry.LinkedArtifacts))),
                new BuilderTrustMetricRecord(
                    "suggestion_success",
                    "suggestion_success",
                    Math.Round(suggestionValueScore * 100d, 2),
                    orderedDecisions.Length,
                    $"Following the current primary recommendation succeeded {followRecommendationSuccessRate:P0} across {followedDecisions.Length} decision(s). Ignoring it failed {ignoredRecommendationFailureRate:P0} across {ignoredDecisions.Length} decision(s).",
                    BuildEvidenceLinks(
                        new[] { BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoRoot) },
                        new[] { BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot) },
                        suggestions is null
                            ? Array.Empty<string>()
                            : suggestions.Suggestions.SelectMany(entry => entry.SupportingEvidence),
                        orderedDecisions.SelectMany(entry => entry.TriggerArtifacts),
                        orderedDecisions.SelectMany(entry => entry.ResultArtifacts))),
                new BuilderTrustMetricRecord(
                    "operator_alignment",
                    "operator_alignment",
                    Math.Round(operatorAlignmentRate * 100d, 2),
                    orderedDecisions.Length,
                    orderedDecisions.Length == 0
                        ? "Operator alignment is using a neutral baseline because no supervised operator decisions are recorded yet."
                        : $"Operator decisions ended in success {operatorAlignmentRate:P0} across {orderedDecisions.Length} recorded action(s).",
                    BuildEvidenceLinks(
                        new[] { BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot) },
                        orderedDecisions.SelectMany(entry => entry.TriggerArtifacts),
                        orderedDecisions.SelectMany(entry => entry.ResultArtifacts))),
                new BuilderTrustMetricRecord(
                    "stability",
                    "stability",
                    Math.Round(stabilityScore * 100d, 2),
                    Math.Max(auditRecords.Length, guardrailRecords.Length),
                    BuildStabilitySummary(auditRecords, guardrailRecords, stabilityScore),
                    BuildEvidenceLinks(
                        new[] { BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot) },
                        new[] { BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot) },
                        new[] { BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot) },
                        guardrailRecords.SelectMany(entry => entry.EvidenceLinks),
                        auditRecords.SelectMany(entry => entry.LinkedArtifacts)))
            }
            .OrderBy(entry => entry.MetricId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var trustScore = Math.Round(
            Clamp01(
                AccuracyWeight * accuracyRate +
                DriftWeight * driftConsistency +
                SuggestionValueWeight * suggestionValueScore +
                OperatorAlignmentWeight * operatorAlignmentRate +
                StabilityWeight * stabilityScore) * 100d,
            2);
        var operatorAlignmentScore = Math.Round(operatorAlignmentRate * 100d, 2);
        var confidenceProfile = DetermineConfidenceProfile(trustScore, sampleSize);

        var targetProfiles = BuildTargetProfiles(
            repoRoot,
            playbooks,
            simulations,
            accuracyRecords,
            orderedDecisions,
            auditRecords,
            primaryPlaybookSuggestion,
            primarySimulationSuggestion);
        var evidenceLinks = BuildEvidenceLinks(
            new[] { BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot) },
            new[] { BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot) },
            new[] { BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot) },
            new[] { BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoRoot) },
            new[] { BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot) },
            new[] { BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot) },
            metrics.SelectMany(entry => entry.EvidenceLinks),
            targetProfiles.SelectMany(entry => entry.EvidenceLinks));
        var report = new BuilderTrustIndexRecord(
            workspaceId,
            SchemaVersion,
            trustScore,
            confidenceProfile,
            operatorAlignmentScore,
            metrics,
            targetProfiles,
            evidenceLinks,
            true,
            BuildSummary(workspaceId, trustScore, confidenceProfile, operatorAlignmentScore, followedDecisions.Length, ignoredDecisions.Length, sampleSize),
            TrustIndexPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(report.ArtifactPath, report);
        return report;
    }

    public static IReadOnlyList<BuilderTrustTargetProfileRecord> ResolveMatchingProfiles(
        BuilderTrustIndexRecord? report,
        string playbookId = "",
        string simulationId = "")
    {
        if (report is null)
        {
            return Array.Empty<BuilderTrustTargetProfileRecord>();
        }

        return report.TargetProfiles
            .Where(entry =>
                string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, playbookId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, simulationId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.TrustScore)
            .ThenBy(entry => entry.TargetType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderTrustTargetProfileRecord[] BuildTargetProfiles(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks,
        BuilderRecoverySimulationsRecord? simulations,
        IReadOnlyList<BuilderSimulationAccuracyRecord> accuracyRecords,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions,
        IReadOnlyList<BuilderExecutionAuditRecord> audits,
        BuilderAutoSuggestionRecord? primaryPlaybookSuggestion,
        BuilderAutoSuggestionRecord? primarySimulationSuggestion)
    {
        var targetProfiles = new List<BuilderTrustTargetProfileRecord>();
        var simulationsByPlaybook = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.SimulationId).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var playbook in playbooks?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
        {
            simulationsByPlaybook.TryGetValue(playbook.PlaybookId, out var simulationIds);
            simulationIds ??= playbook.SimulationIds.ToArray();
            targetProfiles.Add(BuildTargetProfile(
                repoRoot,
                "playbook",
                playbook.PlaybookId,
                decisions.Where(entry => string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                accuracyRecords.Where(entry => simulationIds.Contains(entry.SimulationId, StringComparer.OrdinalIgnoreCase)).ToArray(),
                audits.Where(entry =>
                        string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
                        simulationIds.Contains(entry.SimulationId, StringComparer.OrdinalIgnoreCase))
                    .ToArray(),
                primaryPlaybookSuggestion,
                primarySimulationSuggestion,
                playbook.ArtifactLinks));
        }

        foreach (var simulation in simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
        {
            targetProfiles.Add(BuildTargetProfile(
                repoRoot,
                "simulation",
                simulation.SimulationId,
                decisions.Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                accuracyRecords.Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                audits.Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                primaryPlaybookSuggestion,
                primarySimulationSuggestion,
                simulation.ArtifactLinks));
        }

        return targetProfiles
            .OrderBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.TrustScore)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderTrustTargetProfileRecord BuildTargetProfile(
        string repoRoot,
        string targetType,
        string targetId,
        IReadOnlyList<BuilderOperatorDecisionRecord> decisions,
        IReadOnlyList<BuilderSimulationAccuracyRecord> accuracyRecords,
        IReadOnlyList<BuilderExecutionAuditRecord> auditRecords,
        BuilderAutoSuggestionRecord? primaryPlaybookSuggestion,
        BuilderAutoSuggestionRecord? primarySimulationSuggestion,
        IEnumerable<string> artifactLinks)
    {
        var accuracyRate = accuracyRecords.Count == 0
            ? 0.50d
            : accuracyRecords.Count(entry => entry.AccuracyFlag) / (double)accuracyRecords.Count;
        var operatorAlignmentRate = decisions.Count == 0
            ? 0.50d
            : decisions.Count(entry => entry.SuccessFlag) / (double)decisions.Count;
        var driftConsistency = auditRecords.Count == 0
            ? 0.50d
            : auditRecords.Average(ComputeAuditConsistencyScore);
        var suggestionMatchBonus = string.Equals(targetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(targetId, primaryPlaybookSuggestion?.TargetId, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(targetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(targetId, primarySimulationSuggestion?.TargetId, StringComparison.OrdinalIgnoreCase)
            ? 0.05d
            : 0d;
        var trustScore = Math.Round(Clamp01(
                0.40d * accuracyRate +
                0.35d * operatorAlignmentRate +
                0.25d * driftConsistency +
                suggestionMatchBonus) * 100d,
            2);
        var sampleSize = Math.Max(decisions.Count, Math.Max(accuracyRecords.Count, auditRecords.Count));
        var profile = DetermineConfidenceProfile(trustScore, sampleSize);
        var evidenceLinks = BuildEvidenceLinks(
            new[] { BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot) },
            new[] { BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot) },
            new[] { BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot) },
            new[] { BuilderAutoSuggestionService.AutoSuggestionsPathForRepo(repoRoot) },
            artifactLinks,
            decisions.SelectMany(entry => entry.TriggerArtifacts),
            decisions.SelectMany(entry => entry.ResultArtifacts),
            accuracyRecords.SelectMany(entry => entry.ArtifactLinks),
            auditRecords.SelectMany(entry => entry.LinkedArtifacts));

        return new BuilderTrustTargetProfileRecord(
            targetType,
            targetId,
            trustScore,
            profile,
            Math.Round(operatorAlignmentRate * 100d, 2),
            sampleSize,
            decisions.Count,
            accuracyRecords.Count,
            auditRecords.Count,
            BuildTargetSummary(targetType, targetId, trustScore, profile, operatorAlignmentRate, decisions.Count, accuracyRecords.Count, auditRecords.Count),
            evidenceLinks);
    }

    private static bool MatchesPrimarySuggestion(
        BuilderOperatorDecisionRecord decision,
        BuilderAutoSuggestionRecord? primaryPlaybookSuggestion,
        BuilderAutoSuggestionRecord? primarySimulationSuggestion)
        => !string.IsNullOrWhiteSpace(primarySimulationSuggestion?.TargetId) &&
           string.Equals(decision.SimulationId, primarySimulationSuggestion.TargetId, StringComparison.OrdinalIgnoreCase) ||
           !string.IsNullOrWhiteSpace(primaryPlaybookSuggestion?.TargetId) &&
           string.Equals(decision.PlaybookId, primaryPlaybookSuggestion.TargetId, StringComparison.OrdinalIgnoreCase);

    private static double ComputeAuditConsistencyScore(BuilderExecutionAuditRecord audit)
    {
        var baseScore = audit.DriftType switch
        {
            "no_drift" => 1.00d,
            "minor_drift" => 0.75d,
            "unexpected_success" => 0.50d,
            "major_drift" => 0.35d,
            "unexpected_failure" => 0.15d,
            _ => 0.50d
        };
        var impactPenalty = audit.ImpactLevel switch
        {
            "high" => 0.20d,
            "moderate" => 0.10d,
            _ => 0d
        };
        var driftPenalty = audit.ConstraintDriftDetected || audit.IntentDriftDetected ? 0.05d : 0d;
        return Clamp01(Math.Round(baseScore - impactPenalty - driftPenalty, 4));
    }

    private static double ComputeStabilityScore(
        IReadOnlyList<BuilderExecutionAuditRecord> auditRecords,
        IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrailRecords)
    {
        if (auditRecords.Count == 0 && guardrailRecords.Count == 0)
        {
            return 0.50d;
        }

        var highImpactDriftCount = auditRecords.Count(entry =>
            string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        var highRiskGuardrails = guardrailRecords.Count(entry =>
            string.Equals(entry.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
        var criticalGuardrails = guardrailRecords.Count(entry =>
            string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
        var denominator = Math.Max(1d, auditRecords.Count + guardrailRecords.Count);
        var pressure = (highImpactDriftCount + (0.75d * highRiskGuardrails) + (0.50d * criticalGuardrails)) / denominator;
        return Clamp01(Math.Round(1d - pressure, 4));
    }

    private static string BuildStabilitySummary(
        IReadOnlyList<BuilderExecutionAuditRecord> auditRecords,
        IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrailRecords,
        double stabilityScore)
    {
        if (auditRecords.Count == 0 && guardrailRecords.Count == 0)
        {
            return "Stability is using a neutral baseline because no drift or guardrail history is recorded yet.";
        }

        var highImpactDriftCount = auditRecords.Count(entry =>
            string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        var highRiskGuardrails = guardrailRecords.Count(entry =>
            string.Equals(entry.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
        return $"Stability scores {stabilityScore:P0} with {highImpactDriftCount} high-impact drift record(s) and {highRiskGuardrails} elevated guardrail(s) in current history.";
    }

    private static string DetermineConfidenceProfile(double trustScore, int sampleSize)
    {
        if (sampleSize < 3)
        {
            return "unstable";
        }

        if (trustScore >= 80d)
        {
            return "high_trust";
        }

        if (trustScore >= 60d)
        {
            return "moderate_trust";
        }

        if (trustScore >= 40d)
        {
            return "unstable";
        }

        return "low_trust";
    }

    private static string BuildSummary(
        string workspaceId,
        double trustScore,
        string confidenceProfile,
        double operatorAlignmentScore,
        int followedDecisionCount,
        int ignoredDecisionCount,
        int sampleSize)
        => $"Trust score for {workspaceId} is {trustScore:0.##} with {FormatProfile(confidenceProfile)} profile. Operator alignment is {operatorAlignmentScore:0.##}. Recommendation-following decisions: {followedDecisionCount}. Ignored recommendations: {ignoredDecisionCount}. Evidence sample size: {sampleSize}.";

    private static string BuildTargetSummary(
        string targetType,
        string targetId,
        double trustScore,
        string confidenceProfile,
        double operatorAlignmentRate,
        int decisionCount,
        int accuracyCount,
        int auditCount)
        => $"{FormatToken(targetType)} {targetId} has trust score {trustScore:0.##} with {FormatProfile(confidenceProfile)} profile. Operator alignment is {operatorAlignmentRate:P0} from {decisionCount} decision(s), {accuracyCount} accuracy comparison(s), and {auditCount} audit record(s).";

    private static IReadOnlyList<string> BuildEvidenceLinks(params IEnumerable<string?>[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(source => source!)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static string FormatProfile(string value)
        => value switch
        {
            "high_trust" => "high trust",
            "moderate_trust" => "moderate trust",
            "low_trust" => "low trust",
            _ => "unstable"
        };

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
}
