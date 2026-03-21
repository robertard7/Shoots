using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderPreventativeGuardrailRecord(
    string GuardrailId,
    string TargetScope,
    string TargetId,
    string RiskLevel,
    IReadOnlyList<string> TriggerPatterns,
    IReadOnlyList<string> EvidenceLinks,
    string EscalationReason,
    string Summary,
    DateTimeOffset ObservedUtc)
{
    public string Badge => $"{FormatToken(RiskLevel)} {FormatToken(TargetScope)} guardrail";

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderPreventativeGuardrailsReport(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderPreventativeGuardrailRecord> Guardrails,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderPreventativeGuardrailService
{
    public const string PreventativeGuardrailsFileName = "builder_preventative_guardrails.json";

    private const string SchemaVersion = "builder_preventative_guardrails.v1";
    private const int RepeatedUnexpectedFailureThreshold = 2;
    private const int RepeatedMajorDriftThreshold = 2;
    private const int RepeatedFailureWithoutVariationThreshold = 2;
    private const double WeakAccuracyThreshold = 0.45d;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string PreventativeGuardrailsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), PreventativeGuardrailsFileName);

    public static BuilderPreventativeGuardrailsReport? LoadPreventativeGuardrails(string repoRoot)
        => Load<BuilderPreventativeGuardrailsReport>(PreventativeGuardrailsPathForRepo(repoRoot));

    public static BuilderPreventativeGuardrailsReport? RefreshPreventativeGuardrails(
        string repoRoot,
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderOperatorConstraintsRecord? constraints = null,
        BuilderExecutionReadinessRecord? readiness = null,
        BuilderExecutionAuditReport? audit = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        constraints ??= BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        readiness ??= BuilderExecutionReadinessService.LoadExecutionReadiness(repoRoot);
        audit ??= BuilderExecutionAuditService.LoadExecutionAudit(repoRoot);

        if (playbooks is null &&
            simulations is null &&
            accuracy is null &&
            decisions is null &&
            constraints is null &&
            readiness is null &&
            audit is null)
        {
            return null;
        }

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var observed = observedUtc ?? DateTimeOffset.UtcNow;
        var simulationIndex = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SimulationId))
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var playbookIndex = (playbooks?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PlaybookId))
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selectedRoute = ResolveSelectedRoute(readiness, playbookIndex, simulationIndex);

        var guardrails = new List<BuilderPreventativeGuardrailRecord>();
        foreach (var playbook in playbooks?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
        {
            var relatedSimulations = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
                .Where(entry => string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var record = BuildPlaybookGuardrail(
                repoRoot,
                workspaceId,
                playbook,
                relatedSimulations,
                accuracy,
                decisions,
                constraints,
                readiness,
                audit,
                observed);
            if (record is not null)
            {
                guardrails.Add(record);
            }
        }

        foreach (var simulation in simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
        {
            playbookIndex.TryGetValue(simulation.PlaybookId, out var playbook);
            var record = BuildSimulationGuardrail(
                repoRoot,
                workspaceId,
                simulation,
                playbook,
                accuracy,
                decisions,
                readiness,
                audit,
                observed);
            if (record is not null)
            {
                guardrails.Add(record);
            }
        }

        foreach (var route in (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
                     .Select(entry => entry.TargetRoute)
                     .Concat((decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>()).Select(entry => entry.TargetRoute))
                     .Where(route => !string.IsNullOrWhiteSpace(route))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(route => route, StringComparer.OrdinalIgnoreCase))
        {
            var record = BuildRouteGuardrail(
                repoRoot,
                workspaceId,
                route,
                selectedRoute,
                accuracy,
                decisions,
                readiness,
                audit,
                observed);
            if (record is not null)
            {
                guardrails.Add(record);
            }
        }

        var repoGuardrail = BuildRepoGuardrail(
            repoRoot,
            workspaceId,
            selectedRoute,
            accuracy,
            decisions,
            constraints,
            readiness,
            audit,
            observed);
        if (repoGuardrail is not null)
        {
            guardrails.Add(repoGuardrail);
        }

        var orderedGuardrails = guardrails
            .GroupBy(entry => $"{entry.TargetScope}|{entry.TargetId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(entry => RiskLevelRank(entry.RiskLevel))
                .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(entry => RiskLevelRank(entry.RiskLevel))
            .ThenBy(entry => TargetScopeRank(entry.TargetScope))
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new BuilderPreventativeGuardrailsReport(
            workspaceId,
            SchemaVersion,
            orderedGuardrails,
            true,
            BuildSummary(workspaceId, orderedGuardrails),
            PreventativeGuardrailsPathForRepo(repoRoot),
            orderedGuardrails.Length == 0 ? observed : orderedGuardrails[^1].ObservedUtc);
        Save(report.ArtifactPath, report);
        return report;
    }

    public static IReadOnlyList<BuilderPreventativeGuardrailRecord> ResolveMatchingGuardrails(
        BuilderPreventativeGuardrailsReport? report,
        string playbookId = "",
        string simulationId = "",
        string route = "",
        string workspaceId = "")
    {
        if (report is null)
        {
            return Array.Empty<BuilderPreventativeGuardrailRecord>();
        }

        return report.Guardrails
            .Where(entry =>
                string.Equals(entry.TargetScope, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, playbookId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.TargetScope, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, simulationId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.TargetScope, "route", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, route, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.TargetScope, "repo", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => RiskLevelRank(entry.RiskLevel))
            .ThenBy(entry => TargetScopeRank(entry.TargetScope))
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderPreventativeGuardrailRecord? BuildPlaybookGuardrail(
        string repoRoot,
        string workspaceId,
        BuilderRecoveryPlaybookRecord playbook,
        IReadOnlyList<BuilderRecoverySimulationRecord> relatedSimulations,
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderOperatorConstraintsRecord? constraints,
        BuilderExecutionReadinessRecord? readiness,
        BuilderExecutionAuditReport? audit,
        DateTimeOffset observedUtc)
    {
        var relatedSimulationIds = relatedSimulations
            .Select(entry => entry.SimulationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relatedAudits = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .Where(entry =>
                string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
                relatedSimulationIds.Contains(entry.SimulationId))
            .ToArray();
        var relatedDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry =>
                string.Equals(entry.PlaybookId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
                relatedSimulationIds.Contains(entry.SimulationId))
            .ToArray();
        var relatedAccuracy = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .Where(entry => relatedSimulationIds.Contains(entry.SimulationId))
            .ToArray();
        var evaluation = new GuardrailEvaluation();

        var repeatedSameRouteFailureCount = relatedDecisions
            .Where(entry => !entry.SuccessFlag)
            .GroupBy(entry => entry.TargetRoute, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        if (repeatedSameRouteFailureCount >= RepeatedFailureWithoutVariationThreshold)
        {
            evaluation.Add(
                "repeated_failure_without_variation",
                repeatedSameRouteFailureCount >= 3 ? "critical" : "high",
                $"This playbook has {repeatedSameRouteFailureCount} failed operator decisions on the same route without a recorded recovery win.");
        }

        var majorDriftCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase));
        if (majorDriftCount >= RepeatedMajorDriftThreshold)
        {
            evaluation.Add(
                "repeated_major_drift",
                "high",
                $"Audit history records {majorDriftCount} major drift event(s) for this playbook.");
        }

        var unexpectedFailureCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        if (unexpectedFailureCount >= RepeatedUnexpectedFailureThreshold)
        {
            evaluation.Add(
                "repeated_unexpected_failure",
                "critical",
                $"Audit history records {unexpectedFailureCount} unexpected failure event(s) for this playbook.");
        }
        else if (unexpectedFailureCount > 0)
        {
            evaluation.Add(
                "unexpected_failure_history",
                "high",
                $"Audit history records {unexpectedFailureCount} unexpected failure event(s) for this playbook.");
        }

        var constraintImpactCount = relatedAudits.Count(entry => entry.ConstraintDriftDetected &&
            (string.Equals(entry.ImpactLevel, "high", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(entry.ImpactLevel, "moderate", StringComparison.OrdinalIgnoreCase)));
        if (constraintImpactCount > 0)
        {
            evaluation.Add(
                "constraint_violation_with_impact",
                constraintImpactCount >= 2 ? "critical" : "high",
                $"Constraint drift appears in {constraintImpactCount} audit record(s) for this playbook.");
        }

        if (relatedSimulations.Any(entry =>
                string.Equals(entry.ConfidenceLevel, "low", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(entry.RiskEscalation, "high", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(entry.RiskEscalation, "medium", StringComparison.OrdinalIgnoreCase))))
        {
            evaluation.Add(
                "low_confidence_high_risk",
                "moderate",
                "At least one what-if scenario for this playbook combines low confidence with elevated projected risk.");
        }

        if (relatedAccuracy.Length > 0)
        {
            var accuracyRate = relatedAccuracy.Count(entry => entry.AccuracyFlag) / (double)relatedAccuracy.Length;
            if (accuracyRate < WeakAccuracyThreshold)
            {
                evaluation.Add(
                    "weak_historical_accuracy",
                    "moderate",
                    $"Historical simulation accuracy for this playbook is {accuracyRate:P0} across {relatedAccuracy.Length} record(s).");
            }
        }

        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var selectedConstraintViolation = readiness is not null &&
                                          string.Equals(readiness.SelectionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(readiness.SelectionTargetId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                                          readiness.ConstraintViolations.Count > 0;
        if (selectedConstraintViolation)
        {
            evaluation.Add(
                "selected_constraint_violation",
                string.Equals(readiness?.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase) ? "critical" : "high",
                $"{activeProfile?.ProfileName ?? "Active profile"} blocks the currently selected playbook path.");
        }

        var selectedNoGo = readiness is not null &&
                           string.Equals(readiness.SelectionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(readiness.SelectionTargetId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(readiness.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase);
        if (selectedNoGo)
        {
            evaluation.Add(
                "no_go_readiness",
                "high",
                "The current readiness snapshot marks this playbook path as NO-GO.");
        }

        if (readiness is not null &&
            string.Equals(readiness.SelectionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(readiness.SelectionTargetId, playbook.PlaybookId, StringComparison.OrdinalIgnoreCase) &&
            !readiness.AlignedWithIntent)
        {
            evaluation.Add(
                "intent_alignment_mismatch",
                "moderate",
                "The current readiness snapshot records an intent mismatch for this playbook path.");
        }

        if (!evaluation.HasSignals)
        {
            return null;
        }

        var evidenceLinks = BuildArtifactLinks(
            BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
            BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
            playbook.ArtifactLinks,
            relatedAudits.SelectMany(entry => entry.LinkedArtifacts),
            relatedDecisions.SelectMany(entry => entry.TriggerArtifacts),
            relatedDecisions.SelectMany(entry => entry.ResultArtifacts),
            relatedAccuracy.SelectMany(entry => entry.ArtifactLinks),
            readiness?.LinkedArtifacts);
        return BuildRecord(
            "playbook",
            playbook.PlaybookId,
            evaluation,
            observedUtc,
            evidenceLinks,
            $"Preventative guardrail for {playbook.Title} in {workspaceId}.");
    }

    private static BuilderPreventativeGuardrailRecord? BuildSimulationGuardrail(
        string repoRoot,
        string workspaceId,
        BuilderRecoverySimulationRecord simulation,
        BuilderRecoveryPlaybookRecord? playbook,
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderExecutionReadinessRecord? readiness,
        BuilderExecutionAuditReport? audit,
        DateTimeOffset observedUtc)
    {
        var relatedAudits = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var relatedDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var relatedAccuracy = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var evaluation = new GuardrailEvaluation();

        var repeatedFailures = relatedDecisions.Count(entry => !entry.SuccessFlag);
        if (repeatedFailures >= RepeatedFailureWithoutVariationThreshold)
        {
            evaluation.Add(
                "repeated_failure_without_variation",
                repeatedFailures >= 3 ? "critical" : "high",
                $"This scenario has {repeatedFailures} failed operator decisions without a recorded recovery win.");
        }

        var unexpectedFailureCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        if (unexpectedFailureCount > 0)
        {
            evaluation.Add(
                "unexpected_failure_history",
                unexpectedFailureCount >= RepeatedUnexpectedFailureThreshold ? "critical" : "high",
                $"Audit history records {unexpectedFailureCount} unexpected failure event(s) for this scenario.");
        }

        var majorDriftCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase));
        if (majorDriftCount >= RepeatedMajorDriftThreshold)
        {
            evaluation.Add(
                "repeated_major_drift",
                "high",
                $"Audit history records {majorDriftCount} major drift event(s) for this scenario.");
        }

        var lowConfidenceHighRisk = string.Equals(simulation.ConfidenceLevel, "low", StringComparison.OrdinalIgnoreCase) &&
                                    (string.Equals(simulation.RiskEscalation, "high", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(simulation.RiskEscalation, "medium", StringComparison.OrdinalIgnoreCase));
        if (lowConfidenceHighRisk)
        {
            evaluation.Add(
                "low_confidence_high_risk",
                string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? "high" : "moderate",
                $"Scenario {simulation.Scenario} combines {simulation.ConfidenceLevel} confidence with {simulation.RiskEscalation} projected risk.");
        }

        if (string.Equals(simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))
        {
            evaluation.Add(
                "constraint_blocked_simulation",
                string.Equals(readiness?.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase) ? "critical" : "high",
                simulation.ConstraintReason);
        }

        if (relatedAccuracy.Length > 0)
        {
            var accuracyRate = relatedAccuracy.Count(entry => entry.AccuracyFlag) / (double)relatedAccuracy.Length;
            if (accuracyRate < WeakAccuracyThreshold)
            {
                evaluation.Add(
                    "weak_historical_accuracy",
                    "moderate",
                    $"Historical simulation accuracy is {accuracyRate:P0} across {relatedAccuracy.Length} record(s).");
            }
        }

        var selectedNoGo = readiness is not null &&
                           string.Equals(readiness.SelectionTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(readiness.SelectionTargetId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(readiness.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase);
        if (selectedNoGo)
        {
            evaluation.Add(
                "selected_no_go_path",
                "high",
                "The current readiness snapshot marks this scenario as NO-GO.");
        }

        if (!evaluation.HasSignals)
        {
            return null;
        }

        var evidenceLinks = BuildArtifactLinks(
            BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
            BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
            playbook?.ArtifactLinks,
            simulation.ArtifactLinks,
            relatedAudits.SelectMany(entry => entry.LinkedArtifacts),
            relatedDecisions.SelectMany(entry => entry.TriggerArtifacts),
            relatedDecisions.SelectMany(entry => entry.ResultArtifacts),
            relatedAccuracy.SelectMany(entry => entry.ArtifactLinks),
            readiness?.LinkedArtifacts);
        return BuildRecord(
            "simulation",
            simulation.SimulationId,
            evaluation,
            observedUtc,
            evidenceLinks,
            $"Preventative guardrail for {simulation.Scenario} in {workspaceId}.");
    }

    private static BuilderPreventativeGuardrailRecord? BuildRouteGuardrail(
        string repoRoot,
        string workspaceId,
        string route,
        string selectedRoute,
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderExecutionReadinessRecord? readiness,
        BuilderExecutionAuditReport? audit,
        DateTimeOffset observedUtc)
    {
        var relatedAudits = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .Where(entry => string.Equals(entry.TargetRoute, route, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var relatedDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry => string.Equals(entry.TargetRoute, route, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var relatedAccuracy = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .Where(entry => string.Equals(entry.TargetRoute, route, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var evaluation = new GuardrailEvaluation();

        var repeatedFailures = relatedDecisions.Count(entry => !entry.SuccessFlag);
        if (repeatedFailures >= RepeatedFailureWithoutVariationThreshold)
        {
            evaluation.Add(
                "repeated_failure_without_variation",
                repeatedFailures >= 3 ? "critical" : "high",
                $"Route {route} has {repeatedFailures} failed operator decisions without a recorded recovery win.");
        }

        var unexpectedFailureCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        if (unexpectedFailureCount > 0)
        {
            evaluation.Add(
                "unexpected_failure_history",
                unexpectedFailureCount >= RepeatedUnexpectedFailureThreshold ? "critical" : "high",
                $"Audit history records {unexpectedFailureCount} unexpected failure event(s) on route {route}.");
        }

        var majorDriftCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase));
        if (majorDriftCount >= RepeatedMajorDriftThreshold)
        {
            evaluation.Add(
                "repeated_major_drift",
                "high",
                $"Audit history records {majorDriftCount} major drift event(s) on route {route}.");
        }

        var constraintImpactCount = relatedAudits.Count(entry => entry.ConstraintDriftDetected);
        if (constraintImpactCount > 0)
        {
            evaluation.Add(
                "constraint_violation_with_impact",
                constraintImpactCount >= 2 ? "high" : "moderate",
                $"Constraint drift appears in {constraintImpactCount} audit record(s) on route {route}.");
        }

        if (relatedAccuracy.Length > 0)
        {
            var accuracyRate = relatedAccuracy.Count(entry => entry.AccuracyFlag) / (double)relatedAccuracy.Length;
            if (accuracyRate < WeakAccuracyThreshold)
            {
                evaluation.Add(
                    "weak_historical_accuracy",
                    "moderate",
                    $"Historical route accuracy is {accuracyRate:P0} across {relatedAccuracy.Length} record(s).");
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedRoute) &&
            string.Equals(selectedRoute, route, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(readiness?.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase))
        {
            evaluation.Add(
                "selected_no_go_route",
                "high",
                $"The current readiness snapshot marks route {route} as part of a NO-GO path.");
        }

        if (!evaluation.HasSignals)
        {
            return null;
        }

        var evidenceLinks = BuildArtifactLinks(
            BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
            relatedAudits.SelectMany(entry => entry.LinkedArtifacts),
            relatedDecisions.SelectMany(entry => entry.TriggerArtifacts),
            relatedDecisions.SelectMany(entry => entry.ResultArtifacts),
            relatedAccuracy.SelectMany(entry => entry.ArtifactLinks),
            readiness?.LinkedArtifacts);
        return BuildRecord(
            "route",
            route,
            evaluation,
            observedUtc,
            evidenceLinks,
            $"Preventative guardrail for route {route} in {workspaceId}.");
    }

    private static BuilderPreventativeGuardrailRecord? BuildRepoGuardrail(
        string repoRoot,
        string workspaceId,
        string selectedRoute,
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderOperatorConstraintsRecord? constraints,
        BuilderExecutionReadinessRecord? readiness,
        BuilderExecutionAuditReport? audit,
        DateTimeOffset observedUtc)
    {
        var relatedAudits = (audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>())
            .ToArray();
        var relatedDecisions = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .ToArray();
        var relatedAccuracy = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .ToArray();
        var evaluation = new GuardrailEvaluation();

        var majorDriftCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase));
        var unexpectedFailureCount = relatedAudits.Count(entry => string.Equals(entry.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        var highImpactConstraintDriftCount = relatedAudits.Count(entry =>
            entry.ConstraintDriftDetected &&
            string.Equals(entry.ImpactLevel, "high", StringComparison.OrdinalIgnoreCase));
        if (majorDriftCount >= RepeatedMajorDriftThreshold)
        {
            evaluation.Add(
                "repeated_major_drift",
                "high",
                $"Workspace audit history records {majorDriftCount} major drift event(s).");
        }

        if (unexpectedFailureCount >= RepeatedUnexpectedFailureThreshold)
        {
            evaluation.Add(
                "repeated_unexpected_failure",
                "critical",
                $"Workspace audit history records {unexpectedFailureCount} unexpected failure event(s).");
        }
        else if (unexpectedFailureCount > 0)
        {
            evaluation.Add(
                "unexpected_failure_history",
                "high",
                $"Workspace audit history records {unexpectedFailureCount} unexpected failure event(s).");
        }

        if (highImpactConstraintDriftCount > 0)
        {
            evaluation.Add(
                "constraint_violation_with_impact",
                highImpactConstraintDriftCount >= 2 ? "critical" : "high",
                $"Constraint drift with high impact appears in {highImpactConstraintDriftCount} audit record(s).");
        }

        var repeatedSameRouteFailures = relatedDecisions
            .Where(entry => !entry.SuccessFlag)
            .GroupBy(entry => entry.TargetRoute, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Route = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Route, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (repeatedSameRouteFailures is not null &&
            repeatedSameRouteFailures.Count >= RepeatedFailureWithoutVariationThreshold)
        {
            evaluation.Add(
                "repeated_failure_without_variation",
                repeatedSameRouteFailures.Count >= 3 ? "critical" : "high",
                $"Route {repeatedSameRouteFailures.Route} has {repeatedSameRouteFailures.Count} failed operator decisions without a recorded recovery win.");
        }

        if (string.Equals(readiness?.ReadinessState, "no_go", StringComparison.OrdinalIgnoreCase))
        {
            evaluation.Add(
                "workspace_no_go_readiness",
                readiness?.ConstraintViolations.Count > 0 ? "critical" : "high",
                $"Workspace readiness is NO-GO for the currently selected path on route {FormatToken(selectedRoute)}.");
        }
        else if (string.Equals(readiness?.ReadinessState, "caution", StringComparison.OrdinalIgnoreCase))
        {
            evaluation.Add(
                "workspace_caution_readiness",
                "moderate",
                "Workspace readiness is CAUTION for the currently selected path.");
        }

        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        if (readiness?.ConstraintViolations.Count > 0)
        {
            evaluation.Add(
                "active_constraint_violations",
                readiness.ReadinessState == "no_go" ? "critical" : "high",
                $"{activeProfile?.ProfileName ?? "Active profile"} still records violated constraints: {string.Join(", ", readiness.ConstraintViolations)}.");
        }

        if (relatedAccuracy.Length > 0)
        {
            var accuracyRate = relatedAccuracy.Count(entry => entry.AccuracyFlag) / (double)relatedAccuracy.Length;
            if (accuracyRate < WeakAccuracyThreshold)
            {
                evaluation.Add(
                    "weak_workspace_accuracy",
                    "moderate",
                    $"Workspace-wide simulation accuracy is {accuracyRate:P0} across {relatedAccuracy.Length} record(s).");
            }
        }

        if (!evaluation.HasSignals)
        {
            return null;
        }

        var evidenceLinks = BuildArtifactLinks(
            BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
            BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
            relatedAudits.SelectMany(entry => entry.LinkedArtifacts),
            relatedDecisions.SelectMany(entry => entry.TriggerArtifacts),
            relatedDecisions.SelectMany(entry => entry.ResultArtifacts),
            relatedAccuracy.SelectMany(entry => entry.ArtifactLinks),
            readiness?.LinkedArtifacts);
        return BuildRecord(
            "repo",
            workspaceId,
            evaluation,
            observedUtc,
            evidenceLinks,
            $"Preventative guardrail for workspace {workspaceId}.");
    }

    private static BuilderPreventativeGuardrailRecord BuildRecord(
        string targetScope,
        string targetId,
        GuardrailEvaluation evaluation,
        DateTimeOffset observedUtc,
        IReadOnlyList<string> evidenceLinks,
        string prefix)
    {
        var riskLevel = evaluation.ResolveRiskLevel();
        var triggerPatterns = evaluation.TriggerPatterns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reason = string.Join(" ", evaluation.Reasons
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return new BuilderPreventativeGuardrailRecord(
            ComputeDeterministicId("guardrail", targetScope, targetId, riskLevel, string.Join("|", triggerPatterns)),
            targetScope,
            targetId,
            riskLevel,
            triggerPatterns,
            evidenceLinks,
            reason,
            $"{prefix} Risk {FormatToken(riskLevel)}. {reason}",
            observedUtc);
    }

    private static string ResolveSelectedRoute(
        BuilderExecutionReadinessRecord? readiness,
        IReadOnlyDictionary<string, BuilderRecoveryPlaybookRecord> playbookIndex,
        IReadOnlyDictionary<string, BuilderRecoverySimulationRecord> simulationIndex)
    {
        if (readiness is null)
        {
            return string.Empty;
        }

        if (string.Equals(readiness.SelectionTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
            simulationIndex.TryGetValue(readiness.SelectionTargetId, out var simulation))
        {
            return simulation.TargetRoute;
        }

        if (string.Equals(readiness.SelectionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
            playbookIndex.TryGetValue(readiness.SelectionTargetId, out var playbook))
        {
            return playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty;
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildArtifactLinks(params object?[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(ExpandArtifactSource)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> ExpandArtifactSource(object? source)
        => source switch
        {
            string path => new[] { path },
            IEnumerable<string> paths => paths,
            _ => Array.Empty<string>()
        };

    private static string BuildSummary(string workspaceId, IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrails)
    {
        if (guardrails.Count == 0)
        {
            return $"No preventative guardrails are currently recorded for {workspaceId}. Escalation remains advisory only.";
        }

        var critical = guardrails.Count(entry => string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
        var high = guardrails.Count(entry => string.Equals(entry.RiskLevel, "high", StringComparison.OrdinalIgnoreCase));
        var moderate = guardrails.Count(entry => string.Equals(entry.RiskLevel, "moderate", StringComparison.OrdinalIgnoreCase));
        var low = guardrails.Count(entry => string.Equals(entry.RiskLevel, "low", StringComparison.OrdinalIgnoreCase));
        return $"Generated {guardrails.Count} preventative guardrail(s) for {workspaceId}. Critical: {critical}. High: {high}. Moderate: {moderate}. Low: {low}.";
    }

    private static int RiskLevelRank(string riskLevel)
        => Normalize(riskLevel) switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };

    private static int TargetScopeRank(string targetScope)
        => Normalize(targetScope) switch
        {
            "playbook" => 0,
            "simulation" => 1,
            "route" => 2,
            "repo" => 3,
            _ => 4
        };

    private static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(string prefix, params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"{prefix}-{hash[..10]}";
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

    private sealed class GuardrailEvaluation
    {
        private readonly List<string> _levels = new();
        private readonly List<string> _reasons = new();
        private readonly List<string> _triggers = new();

        public bool HasSignals => _triggers.Count > 0;
        public IReadOnlyList<string> Reasons => _reasons;
        public IReadOnlyList<string> TriggerPatterns => _triggers;

        public void Add(string triggerPattern, string riskLevel, string reason)
        {
            if (!string.IsNullOrWhiteSpace(triggerPattern))
            {
                _triggers.Add(triggerPattern.Trim());
            }

            if (!string.IsNullOrWhiteSpace(riskLevel))
            {
                _levels.Add(riskLevel.Trim());
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                _reasons.Add(reason.Trim());
            }
        }

        public string ResolveRiskLevel()
            => _levels
                .OrderBy(level => RiskLevelRank(level))
                .ThenBy(level => level, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
               ?? "low";
    }
}
