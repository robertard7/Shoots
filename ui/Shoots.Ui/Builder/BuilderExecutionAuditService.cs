using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderExecutionAuditEvidenceStepRecord(
    string StepId,
    string InputSource,
    string AppliedRule,
    string IntermediateResult);

public sealed record BuilderExecutionAuditRecord(
    string AuditId,
    string DecisionId,
    string ActionTaken,
    string TargetRepo,
    string TargetRoute,
    string PlaybookId,
    string SimulationId,
    string ExpectedOutcome,
    string ExpectedOutcomeClass,
    string ActualOutcome,
    bool DriftDetected,
    string DriftType,
    string ImpactLevel,
    string MatchType,
    string ErrorClass,
    string ReadinessState,
    bool ConstraintDriftDetected,
    bool IntentDriftDetected,
    string DriftReason,
    IReadOnlyList<BuilderExecutionAuditEvidenceStepRecord> EvidenceChain,
    IReadOnlyList<string> LinkedArtifacts,
    string Summary,
    DateTimeOffset ObservedUtc)
{
    public string DriftBadge => $"{FormatValue(DriftType)} | {FormatValue(ImpactLevel)}";

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderExecutionAuditReport(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderExecutionAuditRecord> AuditRecords,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderExecutionAuditService
{
    public const string ExecutionAuditFileName = "builder_execution_audit.json";

    private const string SchemaVersion = "builder_execution_audit.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string ExecutionAuditPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), ExecutionAuditFileName);

    public static BuilderExecutionAuditReport? LoadExecutionAudit(string repoRoot)
        => Load<BuilderExecutionAuditReport>(ExecutionAuditPathForRepo(repoRoot));

    public static BuilderExecutionAuditReport? RefreshExecutionAudit(
        string repoRoot,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderExecutionReadinessRecord? readiness = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        if (decisions is null || decisions.Decisions.Count == 0)
        {
            return null;
        }

        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        readiness ??= BuilderExecutionReadinessService.LoadExecutionReadiness(repoRoot);

        var operatorIntent = BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        var constraints = BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        var activeConstraintProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var simulationIndex = (simulations?.Simulations ?? Array.Empty<BuilderRecoverySimulationRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SimulationId))
            .GroupBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var accuracyIndex = (accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DecisionId))
            .GroupBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var orderedDecisions = decisions.Decisions
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var audits = orderedDecisions
            .Select((decision, index) => BuildAuditRecord(
                repoRoot,
                decision,
                index,
                orderedDecisions,
                simulationIndex,
                accuracyIndex,
                readiness,
                operatorIntent,
                activeConstraintProfile))
            .OrderBy(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.AuditId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new BuilderExecutionAuditReport(
            workspaceId,
            SchemaVersion,
            audits,
            true,
            BuildSummary(workspaceId, audits),
            ExecutionAuditPathForRepo(repoRoot),
            audits.Length == 0 ? observedUtc ?? DateTimeOffset.UtcNow : audits[^1].ObservedUtc);
        Save(report.ArtifactPath, report);
        return report;
    }

    private static BuilderExecutionAuditRecord BuildAuditRecord(
        string repoRoot,
        BuilderOperatorDecisionRecord decision,
        int decisionIndex,
        IReadOnlyList<BuilderOperatorDecisionRecord> orderedDecisions,
        IReadOnlyDictionary<string, BuilderRecoverySimulationRecord> simulationIndex,
        IReadOnlyDictionary<string, BuilderSimulationAccuracyRecord> accuracyIndex,
        BuilderExecutionReadinessRecord? readiness,
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorConstraintProfileRecord? activeConstraintProfile)
    {
        simulationIndex.TryGetValue(decision.SimulationId, out var simulation);
        accuracyIndex.TryGetValue(decision.DecisionId, out var accuracy);
        var readinessContext = ResolveReadinessContext(decision, readiness);
        var expectedOutcomeClass = NormalizeOutcomeClass(FirstNonEmpty(decision.PredictedOutcomeClass, simulation?.PredictedOutcomeClass));
        var actualOutcome = NormalizeOutcomeClass(decision.ResultState);
        var matchType = accuracy?.MatchType ?? DetermineMatchType(expectedOutcomeClass, actualOutcome, readinessContext.State, decision.SuccessFlag);
        var errorClass = accuracy?.ErrorClass ?? DetermineErrorClass(expectedOutcomeClass, actualOutcome, matchType, decision.PredictedConfidenceScore);
        var constraintDriftDetected = DetectConstraintDrift(simulation, readinessContext);
        var intentDriftDetected = DetectIntentDrift(operatorIntent, decision, simulation, constraintDriftDetected);
        var driftType = DetermineDriftType(expectedOutcomeClass, actualOutcome, matchType, readinessContext.State, decision.SuccessFlag, constraintDriftDetected, intentDriftDetected);
        var repeatedFailureCount = CountPreviousFailures(decision, decisionIndex, orderedDecisions);
        var impactLevel = DetermineImpactLevel(driftType, decision.SuccessFlag, readinessContext.State, constraintDriftDetected, intentDriftDetected, repeatedFailureCount, errorClass);
        var driftReason = BuildDriftReason(driftType, expectedOutcomeClass, actualOutcome, readinessContext, constraintDriftDetected, intentDriftDetected, repeatedFailureCount);
        var expectedOutcome = BuildExpectedOutcome(decision, simulation, readinessContext);
        var actualOutcomeSummary = BuildActualOutcomeSummary(decision);
        var evidenceChain = BuildEvidenceChain(
            repoRoot,
            decision,
            simulation,
            accuracy,
            readinessContext,
            operatorIntent,
            activeConstraintProfile,
            driftType,
            impactLevel,
            repeatedFailureCount);
        var linkedArtifacts = BuildArtifactLinks(
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
            BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
            BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
            decision.TriggerArtifacts,
            decision.ResultArtifacts,
            simulation?.ArtifactLinks,
            readinessContext.LinkedArtifacts);
        var auditId = ComputeDeterministicId(
            decision.DecisionId,
            expectedOutcomeClass,
            actualOutcome,
            driftType,
            impactLevel,
            string.Join("|", linkedArtifacts));

        return new BuilderExecutionAuditRecord(
            auditId,
            decision.DecisionId,
            decision.ActionTaken,
            decision.TargetRepo,
            decision.TargetRoute,
            decision.PlaybookId,
            decision.SimulationId,
            expectedOutcome,
            expectedOutcomeClass,
            actualOutcomeSummary,
            !string.Equals(driftType, "no_drift", StringComparison.OrdinalIgnoreCase),
            driftType,
            impactLevel,
            matchType,
            errorClass,
            readinessContext.State,
            constraintDriftDetected,
            intentDriftDetected,
            driftReason,
            evidenceChain,
            linkedArtifacts,
            BuildAuditSummary(decision, driftType, impactLevel, expectedOutcomeClass, actualOutcome),
            decision.Timestamp);
    }

    private static ReadinessContext ResolveReadinessContext(
        BuilderOperatorDecisionRecord decision,
        BuilderExecutionReadinessRecord? readiness)
    {
        if (readiness is null)
        {
            return new ReadinessContext(
                "not_recorded",
                false,
                "No readiness snapshot was recorded before this decision.",
                Array.Empty<string>());
        }

        var appliesDirectly =
            string.Equals(readiness.SelectionTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(readiness.SelectionTargetId, decision.SimulationId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(readiness.SelectionTargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(readiness.SelectionTargetId, decision.PlaybookId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(readiness.SelectionTargetType, "workspace", StringComparison.OrdinalIgnoreCase);
        var summary = appliesDirectly
            ? $"Readiness snapshot recorded {FormatValue(readiness.ReadinessState)} for the selected decision path."
            : $"Current readiness snapshot records {FormatValue(readiness.ReadinessState)} for {FormatValue(readiness.SelectionTargetType)} {FormatValue(readiness.SelectionTargetId)} and is used as workspace baseline context only.";
        return new ReadinessContext(
            readiness.ReadinessState,
            appliesDirectly,
            summary,
            readiness.LinkedArtifacts);
    }

    private static bool DetectConstraintDrift(
        BuilderRecoverySimulationRecord? simulation,
        ReadinessContext readinessContext)
        => string.Equals(simulation?.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ||
           readinessContext.AppliesDirectly &&
           string.Equals(readinessContext.State, "no_go", StringComparison.OrdinalIgnoreCase) &&
           readinessContext.Summary.Contains("constraint", StringComparison.OrdinalIgnoreCase);

    private static bool DetectIntentDrift(
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorDecisionRecord decision,
        BuilderRecoverySimulationRecord? simulation,
        bool constraintDriftDetected)
    {
        if (string.IsNullOrWhiteSpace(operatorIntent?.Intent) || !BuilderOperatorIntentService.IsSupportedIntent(operatorIntent.Intent))
        {
            return false;
        }

        var actualOutcome = NormalizeOutcomeClass(decision.ResultState);
        var scenario = FirstNonEmpty(decision.SimulationScenario, simulation?.Scenario);
        return operatorIntent.Intent switch
        {
            var intent when string.Equals(intent, BuilderOperatorIntentService.FastRecoveryIntent, StringComparison.OrdinalIgnoreCase)
                => !IsProgressOutcome(actualOutcome),
            var intent when string.Equals(intent, BuilderOperatorIntentService.SafeRecoveryIntent, StringComparison.OrdinalIgnoreCase)
                => !IsProgressOutcome(actualOutcome) || constraintDriftDetected,
            var intent when string.Equals(intent, BuilderOperatorIntentService.MinimalChangeIntent, StringComparison.OrdinalIgnoreCase)
                => !string.Equals(scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase) || !IsProgressOutcome(actualOutcome),
            var intent when string.Equals(intent, BuilderOperatorIntentService.FullResolutionIntent, StringComparison.OrdinalIgnoreCase)
                => !string.Equals(actualOutcome, "success", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(actualOutcome, "resolved_block", StringComparison.OrdinalIgnoreCase),
            var intent when string.Equals(intent, BuilderOperatorIntentService.UnblockOrchestrationIntent, StringComparison.OrdinalIgnoreCase)
                => !string.Equals(scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase) ||
                   (!string.Equals(actualOutcome, "resolved_block", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(actualOutcome, "partial_success", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static string DetermineDriftType(
        string expectedOutcomeClass,
        string actualOutcome,
        string matchType,
        string readinessState,
        bool successFlag,
        bool constraintDriftDetected,
        bool intentDriftDetected)
    {
        string driftType;
        if (!string.IsNullOrWhiteSpace(expectedOutcomeClass) && !string.IsNullOrWhiteSpace(actualOutcome))
        {
            if (IsProgressOutcome(expectedOutcomeClass) && IsFailureOutcome(actualOutcome))
            {
                driftType = "unexpected_failure";
            }
            else if (IsFailureOutcome(expectedOutcomeClass) && IsProgressOutcome(actualOutcome))
            {
                driftType = "unexpected_success";
            }
            else
            {
                driftType = matchType switch
                {
                    "exact_match" => "no_drift",
                    "partial_match" => "minor_drift",
                    _ => "major_drift"
                };
            }
        }
        else
        {
            driftType = readinessState switch
            {
                "no_go" when !successFlag => "no_drift",
                "no_go" when successFlag => "unexpected_success",
                "go" when !successFlag => "major_drift",
                "caution" when !successFlag => "minor_drift",
                _ => "no_drift"
            };
        }

        if (string.Equals(driftType, "no_drift", StringComparison.OrdinalIgnoreCase) &&
            (constraintDriftDetected || intentDriftDetected))
        {
            return successFlag ? "minor_drift" : "major_drift";
        }

        return driftType;
    }

    private static string DetermineImpactLevel(
        string driftType,
        bool successFlag,
        string readinessState,
        bool constraintDriftDetected,
        bool intentDriftDetected,
        int repeatedFailureCount,
        string errorClass)
    {
        if (string.Equals(driftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase) ||
            !successFlag && string.Equals(readinessState, "no_go", StringComparison.OrdinalIgnoreCase) ||
            constraintDriftDetected && !successFlag ||
            repeatedFailureCount >= 2 && !successFlag)
        {
            return "high";
        }

        if (string.Equals(driftType, "major_drift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driftType, "unexpected_success", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(readinessState, "caution", StringComparison.OrdinalIgnoreCase) ||
            constraintDriftDetected ||
            intentDriftDetected ||
            string.Equals(errorClass, "overconfidence", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorClass, "incorrect_success_prediction", StringComparison.OrdinalIgnoreCase))
        {
            return "moderate";
        }

        return "low";
    }

    private static int CountPreviousFailures(
        BuilderOperatorDecisionRecord decision,
        int decisionIndex,
        IReadOnlyList<BuilderOperatorDecisionRecord> orderedDecisions)
        => orderedDecisions
            .Take(decisionIndex)
            .Count(entry =>
                !entry.SuccessFlag &&
                (string.Equals(entry.TargetRoute, decision.TargetRoute, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(entry.PlaybookId, decision.PlaybookId, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(entry.ResultState, "failed_same_pattern", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(entry.ResultState, "new_failure_pattern", StringComparison.OrdinalIgnoreCase)));

    private static string BuildExpectedOutcome(
        BuilderOperatorDecisionRecord decision,
        BuilderRecoverySimulationRecord? simulation,
        ReadinessContext readinessContext)
    {
        var predictedClass = FirstNonEmpty(decision.PredictedOutcomeClass, simulation?.PredictedOutcomeClass);
        var predictedOutcome = FirstNonEmpty(decision.PredictedOutcome, simulation?.PredictedOutcome);
        if (string.IsNullOrWhiteSpace(predictedClass) && string.IsNullOrWhiteSpace(predictedOutcome))
        {
            return $"No deterministic simulation snapshot was recorded. Readiness baseline: {FormatValue(readinessContext.State)}.";
        }

        var summary = string.IsNullOrWhiteSpace(predictedOutcome)
            ? $"Predicted {FormatValue(predictedClass)}."
            : $"Predicted {FormatValue(predictedClass)}. {predictedOutcome}";
        return $"{summary} Readiness baseline: {FormatValue(readinessContext.State)}.";
    }

    private static string BuildActualOutcomeSummary(BuilderOperatorDecisionRecord decision)
        => $"Observed {FormatValue(decision.ResultState)} after {FormatValue(decision.ActionTaken)}. Success flag: {decision.SuccessFlag}.";

    private static string BuildDriftReason(
        string driftType,
        string expectedOutcomeClass,
        string actualOutcome,
        ReadinessContext readinessContext,
        bool constraintDriftDetected,
        bool intentDriftDetected,
        int repeatedFailureCount)
    {
        var reasons = new List<string>();
        reasons.Add(driftType switch
        {
            "no_drift" => "Expected and actual behavior remained aligned.",
            "minor_drift" => "Expected and actual behavior stayed close, but the outcome shifted within the same recovery direction.",
            "unexpected_failure" => $"Expected {FormatValue(expectedOutcomeClass)} but observed {FormatValue(actualOutcome)}.",
            "unexpected_success" => $"Expected {FormatValue(expectedOutcomeClass)} but observed stronger progress as {FormatValue(actualOutcome)}.",
            _ => $"Expected {FormatValue(expectedOutcomeClass)} but observed materially different behavior as {FormatValue(actualOutcome)}."
        });

        reasons.Add(readinessContext.Summary);
        if (constraintDriftDetected)
        {
            reasons.Add("The selected path violated the active operator constraint profile.");
        }

        if (intentDriftDetected)
        {
            reasons.Add("The actual outcome drifted away from the recorded operator intent.");
        }

        if (repeatedFailureCount > 0)
        {
            reasons.Add($"Historical context already contained {repeatedFailureCount} earlier failure record(s) on the same route or playbook.");
        }

        return string.Join(" ", reasons.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static IReadOnlyList<BuilderExecutionAuditEvidenceStepRecord> BuildEvidenceChain(
        string repoRoot,
        BuilderOperatorDecisionRecord decision,
        BuilderRecoverySimulationRecord? simulation,
        BuilderSimulationAccuracyRecord? accuracy,
        ReadinessContext readinessContext,
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderOperatorConstraintProfileRecord? activeConstraintProfile,
        string driftType,
        string impactLevel,
        int repeatedFailureCount)
    {
        var steps = new List<BuilderExecutionAuditEvidenceStepRecord>
        {
            Step("01",
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                "decision_outcome_snapshot",
                $"{FormatValue(decision.ActionTaken)} ended as {FormatValue(decision.ResultState)} on route {FormatValue(decision.TargetRoute)}."),
            Step("02",
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                "expected_outcome_baseline",
                simulation is null
                    ? "No linked simulation snapshot was recorded for this decision."
                    : $"Simulation {simulation.SimulationId} predicted {FormatValue(simulation.PredictedOutcomeClass)} at {simulation.ConfidenceScore:P0} confidence.")
        };

        steps.Add(
            Step(
                "03",
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
                "prediction_vs_outcome_comparison",
                accuracy is null
                    ? "No simulation accuracy record is available, so drift was derived from decision and readiness evidence."
                    : $"Match type {FormatValue(accuracy.MatchType)} with error class {FormatValue(accuracy.ErrorClass)}."));
        steps.Add(
            Step(
                "04",
                BuilderExecutionReadinessService.ExecutionReadinessPathForRepo(repoRoot),
                "readiness_expectation_check",
                readinessContext.Summary));
        steps.Add(
            Step(
                "05",
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                "constraint_drift_evaluation",
                activeConstraintProfile is null
                    ? "No active operator constraint profile was recorded."
                    : $"{activeConstraintProfile.ProfileName} remained active while constraint compatibility was evaluated."));
        steps.Add(
            Step(
                "06",
                BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
                "intent_drift_evaluation",
                string.IsNullOrWhiteSpace(operatorIntent?.Intent)
                    ? "No explicit operator intent was recorded."
                    : $"Intent remained {BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent)} during audit evaluation."));
        steps.Add(
            Step(
                "07",
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                "impact_classification",
                repeatedFailureCount == 0
                    ? $"Drift classified as {FormatValue(driftType)} with {FormatValue(impactLevel)} impact."
                    : $"Drift classified as {FormatValue(driftType)} with {FormatValue(impactLevel)} impact after {repeatedFailureCount} earlier related failure(s)."));

        return steps
            .OrderBy(step => step.StepId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderExecutionAuditEvidenceStepRecord Step(
        string stepNumber,
        string inputSource,
        string appliedRule,
        string intermediateResult)
        => new(
            $"step-{stepNumber}",
            inputSource,
            appliedRule,
            intermediateResult);

    private static string BuildAuditSummary(
        BuilderOperatorDecisionRecord decision,
        string driftType,
        string impactLevel,
        string expectedOutcomeClass,
        string actualOutcome)
        => $"{FormatValue(decision.ActionTaken)} produced {FormatValue(actualOutcome)} after expecting {FormatValue(expectedOutcomeClass)}. Drift: {FormatValue(driftType)}. Impact: {FormatValue(impactLevel)}.";

    private static string BuildSummary(string workspaceId, IReadOnlyList<BuilderExecutionAuditRecord> audits)
        => audits.Count == 0
            ? $"No execution audits are currently recorded for {workspaceId}."
            : $"Audited {audits.Count} operator decision(s) for {workspaceId}. Drift detected in {audits.Count(entry => entry.DriftDetected)} decision(s). High-impact audits: {audits.Count(entry => string.Equals(entry.ImpactLevel, "high", StringComparison.OrdinalIgnoreCase))}.";

    private static string DetermineMatchType(
        string predictedOutcomeClass,
        string actualOutcome,
        string readinessState,
        bool successFlag)
    {
        if (string.IsNullOrWhiteSpace(predictedOutcomeClass) || string.IsNullOrWhiteSpace(actualOutcome))
        {
            return readinessState switch
            {
                "no_go" when !successFlag => "exact_match",
                "no_go" when successFlag => "partial_match",
                "go" when !successFlag => "mismatch_same_class",
                "caution" when !successFlag => "partial_match",
                _ => "exact_match"
            };
        }

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
        string matchType,
        double confidenceScore)
    {
        if (string.Equals(matchType, "exact_match", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(matchType, "partial_match", StringComparison.OrdinalIgnoreCase))
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

    private static IReadOnlyList<string> BuildArtifactLinks(params object?[] sources)
        => sources
            .SelectMany(source => source switch
            {
                null => Array.Empty<string>(),
                string value => new[] { value },
                IEnumerable<string> values => values,
                _ => Array.Empty<string>()
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"audit-{hash[..10]}";
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

    private readonly record struct ReadinessContext(
        string State,
        bool AppliesDirectly,
        string Summary,
        IReadOnlyList<string> LinkedArtifacts);
}
