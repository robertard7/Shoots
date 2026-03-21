using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderExecutionReadinessBlockingConditionRecord(
    string ConditionId,
    string Severity,
    string Reason,
    string EvidenceBasis,
    IReadOnlyList<string> LinkedArtifacts)
{
    public string Summary => $"{FormatToken(Severity)} blocker: {Reason}";

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderExecutionReadinessWarningRecord(
    string WarningId,
    string Severity,
    string Reason,
    string EvidenceBasis,
    IReadOnlyList<string> LinkedArtifacts)
{
    public string Summary => $"{FormatToken(Severity)} warning: {Reason}";

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderExecutionReadinessRecord(
    string WorkspaceId,
    string SchemaVersion,
    string ReadinessState,
    string SelectionTargetType,
    string SelectionTargetId,
    string SelectionSummary,
    IReadOnlyList<BuilderExecutionReadinessBlockingConditionRecord> BlockingConditions,
    IReadOnlyList<BuilderExecutionReadinessWarningRecord> Warnings,
    bool AlignedWithIntent,
    string IntentAlignmentSummary,
    IReadOnlyList<string> ConstraintViolations,
    string ConstraintSummary,
    string CalibrationProfile,
    double SignalBalanceScore,
    string SignalBalanceSummary,
    IReadOnlyList<BuilderSignalContributionRecord> SignalContributions,
    IReadOnlyList<string> LinkedArtifacts,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderExecutionReadinessService
{
    public const string ExecutionReadinessFileName = "builder_execution_readiness.json";

    private const string SchemaVersion = "builder_execution_readiness.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string ExecutionReadinessPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), ExecutionReadinessFileName);

    public static BuilderExecutionReadinessRecord? LoadExecutionReadiness(string repoRoot)
        => Load<BuilderExecutionReadinessRecord>(ExecutionReadinessPathForRepo(repoRoot));

    public static BuilderExecutionReadinessRecord? RefreshExecutionReadiness(
        string repoRoot,
        string selectedPlaybookId = "",
        string selectedSimulationId = "",
        string selectedComparisonId = "",
        BuilderRecoveryPlaybooksRecord? playbooks = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderPlaybookContextFiltersRecord? contextFilters = null,
        BuilderRecoveryComparisonsRecord? comparisons = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderRouteRiskWarningsRecord? routeWarnings = null,
        BuilderDecisionJustificationsRecord? justifications = null,
        DateTimeOffset? observedUtc = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderTrustIndexRecord? trust = null,
        BuilderPredictiveDriftReport? predictiveDrift = null,
        BuilderSignalCalibrationRecord? calibration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        playbooks ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        simulations ??= BuilderRecoverySimulationService.LoadRecoverySimulations(repoRoot);
        rankings ??= BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot);
        contextFilters ??= BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot);
        comparisons ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        routeWarnings ??= BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
        justifications ??= BuilderDecisionJustificationService.LoadDecisionJustifications(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        trust ??= BuilderTrustIndexService.LoadTrustIndex(repoRoot);
        predictiveDrift ??= BuilderPredictiveDriftService.LoadPredictiveDrift(repoRoot);

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        var operatorIntent = BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        var constraints = BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        var activeConstraintProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(repoRoot);
        var reviewArtifacts = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot);
        var highRiskFlags = BuilderReviewWorkspaceService.LoadHighRiskFileFlags(repoRoot);

        var selectedPlaybook = ResolvePlaybook(playbooks, rankings, selectedPlaybookId, selectedSimulationId, selectedComparisonId, simulations, comparisons);
        var selectedSimulation = ResolveSimulation(simulations, selectedSimulationId, selectedPlaybook, selectedComparisonId, comparisons);
        var selectedComparison = ResolveComparison(comparisons, selectedComparisonId, selectedSimulation, selectedPlaybook);
        if (selectedPlaybook is null && selectedSimulation is not null && playbooks is not null)
        {
            selectedPlaybook = playbooks.Playbooks.FirstOrDefault(entry =>
                string.Equals(entry.PlaybookId, selectedSimulation.PlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        var selectedRanking = rankings?.Rankings.FirstOrDefault(entry =>
            string.Equals(entry.PlaybookId, selectedPlaybook?.PlaybookId, StringComparison.OrdinalIgnoreCase));
        var selectedContextFilter = contextFilters?.RelevanceScores.FirstOrDefault(entry =>
            string.Equals(entry.PlaybookId, selectedPlaybook?.PlaybookId, StringComparison.OrdinalIgnoreCase));
        var selectedScenarioCalibration = accuracy?.SimulationTypeCalibration.FirstOrDefault(entry =>
            string.Equals(entry.Key, selectedSimulation?.Scenario, StringComparison.OrdinalIgnoreCase));
        var currentRoute = selectedSimulation?.TargetRoute
                           ?? selectedPlaybook?.AppliesToRoutes.FirstOrDefault()
                           ?? routeResolution?.RouteDecision
                           ?? contextFilters?.ContextSnapshot.ActiveRoute
                           ?? "not_recorded";
        var selectionTargetType = ResolveSelectionTargetType(selectedPlaybook, selectedSimulation, selectedComparison);
        var selectionTargetId = selectedSimulation?.SimulationId
                                ?? selectedComparison?.ComparisonId
                                ?? selectedPlaybook?.PlaybookId
                                ?? workspaceId;
        var selectionSummary = BuildSelectionSummary(selectedPlaybook, selectedSimulation, selectedComparison, currentRoute, reviewWorkspace);
        var repeatedFailures = ResolveRepeatedFailures(decisions, currentRoute, selectedPlaybook);
        var intentAlignment = EvaluateIntentAlignment(operatorIntent, selectedPlaybook, selectedSimulation, selectedComparison, selectedRanking, selectedContextFilter);
        var constraintViolations = ResolveConstraintViolations(activeConstraintProfile, selectedContextFilter, selectedSimulation);
        calibration ??= BuilderSignalCalibrationService.LoadSignalCalibration(repoRoot) ??
                        BuilderSignalCalibrationService.RefreshSignalCalibration(
                            repoRoot,
                            rankings,
                            contextFilters,
                            constraints,
                            accuracy,
                            decisions,
                            BuilderExecutionAuditService.LoadExecutionAudit(repoRoot),
                            guardrails,
                            operatorIntent,
                            observedUtc);
        var signalBalance = EvaluateSignalBalance(
            workspaceId,
            selectedPlaybook,
            selectedSimulation,
            selectedComparison,
            selectedRanking,
            intentAlignment,
            constraintViolations,
            currentRoute,
            repeatedFailures,
            trust,
            guardrails,
            predictiveDrift,
            calibration);

        var blockingConditions = BuildBlockingConditions(
            repoRoot,
            reviewWorkspace,
            reviewArtifacts.PatchApplyDecision,
            highRiskFlags,
            playbooks,
            selectedPlaybook,
            selectedSimulation,
            selectedComparison,
            repeatedFailures,
            constraintViolations);
        var warnings = BuildWarnings(
            repoRoot,
            reviewWorkspace,
            highRiskFlags,
            routeWarnings,
            currentRoute,
            operatorIntent,
            intentAlignment,
            selectedPlaybook,
            selectedSimulation,
            selectedRanking,
            selectedScenarioCalibration,
            constraintViolations,
            selectionTargetType);
        warnings = AppendSignalBalanceWarning(repoRoot, warnings, signalBalance);
        var readinessState = DetermineReadinessState(blockingConditions, warnings, signalBalance.CompositeScore);
        var linkedArtifacts = BuildArtifactLinks(
            BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot),
            BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot),
            BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot),
            BuilderWorkspaceService.RouteResolutionPathForRepo(repoRoot),
            BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
            BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
            BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot),
            BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
            BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
            BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
            BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
            BuilderRecoveryComparisonService.RecoveryComparisonsPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderDecisionJustificationService.DecisionJustificationsPathForRepo(repoRoot),
            BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
            BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
            BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
            BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot),
            BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot),
            blockingConditions.SelectMany(condition => condition.LinkedArtifacts),
            warnings.SelectMany(warning => warning.LinkedArtifacts),
            selectedPlaybook?.ArtifactLinks,
            selectedSimulation?.ArtifactLinks,
            selectedComparison?.ComparisonMetrics.SelectMany(metric => metric.EvidenceLinks),
            justifications?.Justifications.Where(entry =>
                    string.Equals(entry.TargetId, selectionTargetId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(entry => entry.EvidenceLinks));

        var artifact = new BuilderExecutionReadinessRecord(
            workspaceId,
            SchemaVersion,
            readinessState,
            selectionTargetType,
            selectionTargetId,
            selectionSummary,
            blockingConditions,
            warnings,
            intentAlignment.Aligned,
            intentAlignment.Summary,
            constraintViolations,
            BuildConstraintSummary(activeConstraintProfile, constraintViolations),
            calibration.CalibrationProfile,
            signalBalance.CompositeScore,
            signalBalance.Summary,
            signalBalance.Contributions,
            linkedArtifacts,
            true,
            BuildSummary(readinessState, selectionSummary, blockingConditions, warnings, intentAlignment, constraintViolations, signalBalance),
            ExecutionReadinessPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    private static BuilderRecoveryPlaybookRecord? ResolvePlaybook(
        BuilderRecoveryPlaybooksRecord? playbooks,
        BuilderPlaybookRankingsRecord? rankings,
        string selectedPlaybookId,
        string selectedSimulationId,
        string selectedComparisonId,
        BuilderRecoverySimulationsRecord? simulations,
        BuilderRecoveryComparisonsRecord? comparisons)
    {
        if (playbooks is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selectedPlaybookId))
        {
            return playbooks.Playbooks.FirstOrDefault(entry =>
                string.Equals(entry.PlaybookId, selectedPlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(selectedSimulationId) && simulations is not null)
        {
            var simulation = simulations.Simulations.FirstOrDefault(entry =>
                string.Equals(entry.SimulationId, selectedSimulationId, StringComparison.OrdinalIgnoreCase));
            if (simulation is not null)
            {
                return playbooks.Playbooks.FirstOrDefault(entry =>
                    string.Equals(entry.PlaybookId, simulation.PlaybookId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedComparisonId) && comparisons is not null)
        {
            var comparison = comparisons.ComparisonSets.FirstOrDefault(entry =>
                string.Equals(entry.ComparisonId, selectedComparisonId, StringComparison.OrdinalIgnoreCase));
            var metric = comparison?.ComparisonMetrics
                .OrderBy(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(metric => metric.ComparisonScore)
                .ThenBy(metric => metric.PlaybookId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (metric is not null)
            {
                return playbooks.Playbooks.FirstOrDefault(entry =>
                    string.Equals(entry.PlaybookId, metric.PlaybookId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (rankings is not null)
        {
            var rankedPlaybookId = rankings.Rankings
                .OrderBy(entry => entry.RankingPosition)
                .ThenBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.PlaybookId)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rankedPlaybookId))
            {
                return playbooks.Playbooks.FirstOrDefault(entry =>
                    string.Equals(entry.PlaybookId, rankedPlaybookId, StringComparison.OrdinalIgnoreCase));
            }
        }

        return playbooks.Playbooks
            .OrderBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static BuilderRecoverySimulationRecord? ResolveSimulation(
        BuilderRecoverySimulationsRecord? simulations,
        string selectedSimulationId,
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        string selectedComparisonId,
        BuilderRecoveryComparisonsRecord? comparisons)
    {
        if (simulations is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selectedSimulationId))
        {
            return simulations.Simulations.FirstOrDefault(entry =>
                string.Equals(entry.SimulationId, selectedSimulationId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(selectedComparisonId) && comparisons is not null)
        {
            var comparison = comparisons.ComparisonSets.FirstOrDefault(entry =>
                string.Equals(entry.ComparisonId, selectedComparisonId, StringComparison.OrdinalIgnoreCase));
            var metric = comparison?.ComparisonMetrics
                .OrderBy(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(metric => metric.ComparisonScore)
                .ThenBy(metric => metric.SimulationId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (metric is not null)
            {
                return simulations.Simulations.FirstOrDefault(entry =>
                    string.Equals(entry.SimulationId, metric.SimulationId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (selectedPlaybook is null)
        {
            return null;
        }

        return simulations.Simulations
            .Where(entry => string.Equals(entry.PlaybookId, selectedPlaybook.PlaybookId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => string.Equals(entry.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(entry => ScenarioRank(entry.Scenario))
            .ThenBy(entry => entry.SimulationId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static BuilderRecoveryComparisonSetRecord? ResolveComparison(
        BuilderRecoveryComparisonsRecord? comparisons,
        string selectedComparisonId,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryPlaybookRecord? selectedPlaybook)
    {
        if (comparisons is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selectedComparisonId))
        {
            return comparisons.ComparisonSets.FirstOrDefault(entry =>
                string.Equals(entry.ComparisonId, selectedComparisonId, StringComparison.OrdinalIgnoreCase));
        }

        if (selectedSimulation is not null)
        {
            return comparisons.ComparisonSets.FirstOrDefault(entry =>
                entry.SimulationIds.Contains(selectedSimulation.SimulationId, StringComparer.OrdinalIgnoreCase));
        }

        if (selectedPlaybook is not null)
        {
            return comparisons.ComparisonSets.FirstOrDefault(entry =>
                entry.PlaybookIds.Contains(selectedPlaybook.PlaybookId, StringComparer.OrdinalIgnoreCase));
        }

        return comparisons.ComparisonSets
            .OrderBy(entry => string.Equals(entry.BranchId, "all_candidates", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.BranchId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ComparisonId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<BuilderExecutionReadinessBlockingConditionRecord> BuildBlockingConditions(
        string repoRoot,
        BuilderReviewWorkspaceRecord? reviewWorkspace,
        BuilderPatchApplyDecisionRecord? patchApplyDecision,
        BuilderHighRiskFileFlagsRecord highRiskFlags,
        BuilderRecoveryPlaybooksRecord? playbooks,
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryComparisonSetRecord? selectedComparison,
        IReadOnlyList<BuilderOperatorDecisionRecord> repeatedFailures,
        IReadOnlyList<string> constraintViolations)
    {
        var conditions = new List<BuilderExecutionReadinessBlockingConditionRecord>();

        if (patchApplyDecision is not null &&
            (string.Equals(patchApplyDecision.ApplyEligibilityState, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(patchApplyDecision.ApplyEligibilityState, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(patchApplyDecision.FinalizationState, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(patchApplyDecision.FinalizationState, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase)))
        {
            conditions.Add(BuildBlockingCondition(
                "review_finalize_block",
                "critical",
                $"Review and finalize remain blocked: {FormatToken(patchApplyDecision.FinalizationState)}.",
                patchApplyDecision.BlockReasons.Count == 0
                    ? patchApplyDecision.Summary
                    : string.Join(" ", patchApplyDecision.BlockReasons),
                BuilderReviewWorkspaceService.PatchApplyDecisionPathForRepo(repoRoot),
                BuilderReviewWorkspaceService.FileReviewDecisionPathForRepo(repoRoot),
                patchApplyDecision.LinkedArtifactPaths));
        }

        if (constraintViolations.Count > 0)
        {
            conditions.Add(BuildBlockingCondition(
                "constraint_violation",
                "critical",
                "The selected recovery path violates the active operator constraint profile.",
                selectedSimulation?.ConstraintReason
                ?? $"Violated constraints: {string.Join(", ", constraintViolations)}.",
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot)));
        }

        if (playbooks?.CrossRepoCoordination.BlockingRepoIds.Count > 0 &&
            ((selectedPlaybook?.CrossRepoScope ?? false) || selectedComparison is not null))
        {
            conditions.Add(BuildBlockingCondition(
                "cross_repo_dependency_block",
                "high",
                "A cross-repo dependency block is still active for the selected recovery path.",
                $"Blocking repos: {string.Join(", ", playbooks.CrossRepoCoordination.BlockingRepoIds)}. Recommended recovery order: {string.Join(" -> ", playbooks.CrossRepoCoordination.RecommendedRecoveryOrder)}.",
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderCrossRepoOrchestrationService.CrossRepoExecutionStatePathForRepo(repoRoot)));
        }

        if (repeatedFailures.Count >= 2)
        {
            conditions.Add(BuildBlockingCondition(
                "repeated_failure_without_change",
                "high",
                "Recent operator history shows repeated failure on the same route without a recorded recovery win.",
                $"Recent failed decisions: {string.Join(", ", repeatedFailures.Select(entry => entry.DecisionId))}.",
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot)));
        }

        var blockedHighRiskFiles = reviewWorkspace?.FileGroups
            .SelectMany(group => group.Files)
            .Where(file => file.RequiresExplicitApproval &&
                           (string.Equals(file.ApprovalState, "rejected", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(file.ApprovalState, "needs_revision", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<BuilderReviewWorkspaceFileRecord>();
        if (blockedHighRiskFiles.Length > 0)
        {
            conditions.Add(BuildBlockingCondition(
                "high_risk_review_stall",
                "high",
                "High-risk files are still rejected or marked for revision.",
                $"Affected files: {string.Join(", ", blockedHighRiskFiles.Select(file => file.RelativePath))}.",
                BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot),
                BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot),
                highRiskFlags.Entries.Select(entry => entry.FilePath)));
        }

        return conditions
            .GroupBy(condition => condition.ConditionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(condition => SeverityRank(condition.Severity))
            .ThenBy(condition => condition.Reason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(condition => condition.ConditionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BuilderExecutionReadinessWarningRecord> BuildWarnings(
        string repoRoot,
        BuilderReviewWorkspaceRecord? reviewWorkspace,
        BuilderHighRiskFileFlagsRecord highRiskFlags,
        BuilderRouteRiskWarningsRecord? routeWarnings,
        string currentRoute,
        BuilderOperatorIntentRecord? operatorIntent,
        IntentAlignmentEvaluation intentAlignment,
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderPlaybookRankingRecord? selectedRanking,
        BuilderSimulationCalibrationRecord? selectedScenarioCalibration,
        IReadOnlyList<string> constraintViolations,
        string selectionTargetType)
    {
        var warnings = new List<BuilderExecutionReadinessWarningRecord>();

        var pendingFiles = reviewWorkspace?.ReviewCounts.PendingFiles ?? 0;
        if (pendingFiles > 0)
        {
            warnings.Add(BuildWarning(
                "pending_review",
                "medium",
                "Pending review items remain in the workspace.",
                $"Pending files: {pendingFiles}. Finalize readiness: {FormatToken(reviewWorkspace?.ReviewCounts.FinalizeEligibilityState ?? "not_recorded")}.",
                BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoRoot)));
        }

        var pendingHighRiskFiles = reviewWorkspace?.FileGroups
            .SelectMany(group => group.Files)
            .Where(file => file.RequiresExplicitApproval &&
                           string.Equals(file.ApprovalState, "pending_review", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<BuilderReviewWorkspaceFileRecord>();
        if (pendingHighRiskFiles.Length > 0)
        {
            warnings.Add(BuildWarning(
                "pending_high_risk_review",
                "high",
                "High-risk files still require explicit review attention.",
                $"Pending high-risk files: {string.Join(", ", pendingHighRiskFiles.Select(file => file.RelativePath))}.",
                BuilderReviewWorkspaceService.HighRiskFileFlagsPathForRepo(repoRoot),
                highRiskFlags.Entries.Select(entry => entry.FilePath)));
        }

        if (selectedSimulation is not null &&
            (string.Equals(selectedSimulation.ConfidenceLevel, "low", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selectedScenarioCalibration?.CalibratedConfidence, "low", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selectedScenarioCalibration?.AccuracyIndicator, "unstable_confidence", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(BuildWarning(
                "low_confidence_projection",
                "medium",
                "The selected simulation has low or unstable confidence.",
                selectedScenarioCalibration is null
                    ? $"{FormatToken(selectedSimulation.ConfidenceLevel)} confidence at {selectedSimulation.ConfidenceScore:P0} with no completed calibration history."
                    : $"{FormatToken(selectedSimulation.ConfidenceLevel)} confidence at {selectedSimulation.ConfidenceScore:P0}. Calibrated confidence: {FormatToken(selectedScenarioCalibration.CalibratedConfidence)} across {selectedScenarioCalibration.SampleSize} similar simulations.",
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot),
                BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot)));
        }

        if (selectedRanking is not null &&
            (selectedRanking.HistoricalAccuracyRate < 0.55d ||
             selectedRanking.OutcomeSuccessRate < 0.50d ||
             string.Equals(selectedRanking.ConfidenceIndicator, "low_confidence", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(BuildWarning(
                "weak_historical_success",
                "medium",
                "Historical evidence for the selected playbook is weak or mixed.",
                $"Historical accuracy {selectedRanking.HistoricalAccuracyRate:P0}, success rate {selectedRanking.OutcomeSuccessRate:P0}, confidence {FormatToken(selectedRanking.ConfidenceIndicator)}.",
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot)));
        }

        var matchingRouteWarnings = (routeWarnings?.Entries ?? Array.Empty<BuilderRouteRiskWarningEntryRecord>())
            .Where(entry => string.Equals(currentRoute, "not_recorded", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.RouteAttempted, currentRoute, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.RouteAttempted, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.WarningReason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RelatedKnowledgeGraphNode, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        foreach (var entry in matchingRouteWarnings)
        {
            warnings.Add(BuildWarning(
                $"route_warning_{entry.RelatedKnowledgeGraphNode}",
                entry.WarningReason.Contains("repeatedly failed", StringComparison.OrdinalIgnoreCase) ? "high" : "medium",
                $"Route warning for {entry.RouteAttempted}.",
                entry.WarningReason,
                BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot)));
        }

        if (!intentAlignment.Aligned && !string.IsNullOrWhiteSpace(operatorIntent?.Intent))
        {
            warnings.Add(BuildWarning(
                "intent_misalignment",
                "medium",
                "The selected recovery path is not strongly aligned with the current operator intent.",
                intentAlignment.Summary,
                BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot),
                BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
                BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot)));
        }

        if (selectedPlaybook is null &&
            selectedSimulation is null &&
            !string.Equals(selectionTargetType, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(BuildWarning(
                "missing_recovery_selection",
                "medium",
                "No recovery playbook or simulation is currently selected.",
                "Readiness is being evaluated from workspace-level review and route artifacts only.",
                BuilderRecoveryPlaybookService.RecoveryPlaybooksPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot)));
        }

        if (constraintViolations.Count == 0 &&
            selectedSimulation is not null &&
            string.Equals(selectedSimulation.ConstraintCompatibility, "compatible", StringComparison.OrdinalIgnoreCase) &&
            selectedSimulation.BlockedByConstraints.Count > 0)
        {
            warnings.Add(BuildWarning(
                "constraint_tradeoff_note",
                "low",
                "The selected simulation remains compatible, but the active constraint profile still narrows adjacent options.",
                $"Constraint notes: {selectedSimulation.ConstraintReason}",
                BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
                BuilderRecoverySimulationService.RecoverySimulationsPathForRepo(repoRoot)));
        }

        return warnings
            .GroupBy(warning => warning.WarningId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(warning => SeverityRank(warning.Severity))
            .ThenBy(warning => warning.Reason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(warning => warning.WarningId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BuilderOperatorDecisionRecord> ResolveRepeatedFailures(
        BuilderOperatorDecisionsRecord? decisions,
        string currentRoute,
        BuilderRecoveryPlaybookRecord? selectedPlaybook)
        => (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .Where(entry =>
                (!string.IsNullOrWhiteSpace(currentRoute) && string.Equals(entry.TargetRoute, currentRoute, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(selectedPlaybook?.PlaybookId) && string.Equals(entry.PlaybookId, selectedPlaybook.PlaybookId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Where(entry => !entry.SuccessFlag &&
                            (string.Equals(entry.ResultState, "failed_same_pattern", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entry.ResultState, "new_failure_pattern", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entry.ResultState, "failed", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private static IntentAlignmentEvaluation EvaluateIntentAlignment(
        BuilderOperatorIntentRecord? operatorIntent,
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryComparisonSetRecord? selectedComparison,
        BuilderPlaybookRankingRecord? selectedRanking,
        BuilderPlaybookContextFilterEntryRecord? selectedContextFilter)
    {
        if (string.IsNullOrWhiteSpace(operatorIntent?.Intent) || !BuilderOperatorIntentService.IsSupportedIntent(operatorIntent.Intent))
        {
            return new IntentAlignmentEvaluation(true, 100d, "No explicit operator intent is recorded, so readiness stays on base evidence.");
        }

        var baseScore = selectedRanking?.IntentAlignmentScore
                        ?? selectedContextFilter?.IntentAlignmentScore
                        ?? 0d;
        var scenarioScore = selectedSimulation is null ? 0d : operatorIntent.Intent switch
        {
            var intent when string.Equals(intent, BuilderOperatorIntentService.FastRecoveryIntent, StringComparison.OrdinalIgnoreCase) &&
                             (string.Equals(selectedSimulation.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(selectedSimulation.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase)) => 90d,
            var intent when string.Equals(intent, BuilderOperatorIntentService.SafeRecoveryIntent, StringComparison.OrdinalIgnoreCase) &&
                             (string.Equals(selectedSimulation.Scenario, "isolate_high_risk_files", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(selectedSimulation.Scenario, "switch_route_manual", StringComparison.OrdinalIgnoreCase)) => 90d,
            var intent when string.Equals(intent, BuilderOperatorIntentService.MinimalChangeIntent, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(selectedSimulation.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase) => 90d,
            var intent when string.Equals(intent, BuilderOperatorIntentService.FullResolutionIntent, StringComparison.OrdinalIgnoreCase) &&
                             (string.Equals(selectedSimulation.Scenario, "switch_route_manual", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(selectedSimulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase)) => 85d,
            var intent when string.Equals(intent, BuilderOperatorIntentService.UnblockOrchestrationIntent, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(selectedSimulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase) => 95d,
            _ => 30d
        };
        var comparisonScore = selectedComparison is null
            ? 0d
            : string.Equals(selectedComparison.BranchId, operatorIntent.Intent, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(selectedComparison.BranchId, "all_candidates", StringComparison.OrdinalIgnoreCase)
                ? 80d
                : 35d;
        var bestIntentScore = selectedRanking?.BestForIntents.Contains(operatorIntent.Intent, StringComparer.OrdinalIgnoreCase) == true
            ? 95d
            : 0d;
        var alignmentScore = new[] { baseScore, scenarioScore, comparisonScore, bestIntentScore }.Max();
        var aligned = alignmentScore >= 60d;
        var summary = aligned
            ? $"Selected path aligns with {BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent)} at score {alignmentScore:0.##}."
            : $"Selected path is weakly aligned with {BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent)} at score {alignmentScore:0.##}; consider the intent-adjusted ranking before proceeding.";
        return new IntentAlignmentEvaluation(aligned, alignmentScore, summary);
    }

    private static IReadOnlyList<string> ResolveConstraintViolations(
        BuilderOperatorConstraintProfileRecord? activeConstraintProfile,
        BuilderPlaybookContextFilterEntryRecord? selectedContextFilter,
        BuilderRecoverySimulationRecord? selectedSimulation)
    {
        var values = new List<string>();
        if (selectedContextFilter?.ViolatesConstraints == true)
        {
            values.AddRange(selectedContextFilter.ViolatedConstraints);
        }

        if (string.Equals(selectedSimulation?.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))
        {
            values.AddRange(selectedSimulation!.BlockedByConstraints);
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderSignalEvaluationRecord EvaluateSignalBalance(
        string workspaceId,
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryComparisonSetRecord? selectedComparison,
        BuilderPlaybookRankingRecord? selectedRanking,
        IntentAlignmentEvaluation intentAlignment,
        IReadOnlyList<string> constraintViolations,
        string currentRoute,
        IReadOnlyList<BuilderOperatorDecisionRecord> repeatedFailures,
        BuilderTrustIndexRecord? trust,
        BuilderPreventativeGuardrailsReport? guardrails,
        BuilderPredictiveDriftReport? predictiveDrift,
        BuilderSignalCalibrationRecord? calibration)
    {
        var trustProfiles = selectedComparison is null
            ? BuilderTrustIndexService.ResolveMatchingProfiles(
                trust,
                selectedPlaybook?.PlaybookId ?? string.Empty,
                selectedSimulation?.SimulationId ?? string.Empty).ToArray()
            : (trust?.TargetProfiles ?? Array.Empty<BuilderTrustTargetProfileRecord>())
                .Where(profile =>
                    string.Equals(profile.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                    selectedComparison.PlaybookIds.Contains(profile.TargetId, StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(profile.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                    selectedComparison.SimulationIds.Contains(profile.TargetId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        var guardrailMatches = selectedComparison is null
            ? BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
                guardrails,
                selectedPlaybook?.PlaybookId ?? string.Empty,
                selectedSimulation?.SimulationId ?? string.Empty,
                currentRoute,
                workspaceId).ToArray()
            : selectedComparison.ComparisonMetrics
                .SelectMany(metric => BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
                    guardrails,
                    metric.PlaybookId,
                    metric.SimulationId,
                    string.Empty,
                    workspaceId))
                .GroupBy(record => record.GuardrailId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        var predictiveMatches = predictiveDrift?.Predictions.Where(prediction =>
                string.Equals(prediction.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prediction.TargetId, selectedPlaybook?.PlaybookId ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prediction.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prediction.TargetId, selectedSimulation?.SimulationId ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prediction.TargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prediction.TargetId, selectedComparison?.ComparisonId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(prediction => prediction.TargetType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(prediction => prediction.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(prediction => prediction.PredictionId, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<BuilderPredictiveDriftRecord>();
        var rankingSignal = selectedRanking?.IntentAdjustedScore ?? selectedRanking?.RankingScore ?? 50d;
        var constraintSignal = BuilderSignalCalibrationService.ResolveConstraintSignal(constraintViolations.Count > 0);
        var trustSignal = trustProfiles.Length == 0
            ? trust?.TrustScore ?? 50d
            : Math.Round(trustProfiles.Average(profile => profile.TrustScore), 2);
        var guardrailSignal = BuilderSignalCalibrationService.ResolveGuardrailSafetySignal(
            guardrailMatches
                .OrderBy(record => RiskRank(record.RiskLevel))
                .ThenBy(record => record.TargetScope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
                .Select(record => record.RiskLevel)
                .FirstOrDefault() ?? string.Empty);
        var driftSignal = predictiveMatches.Length == 0
            ? repeatedFailures.Count == 0
                ? 62d
                : BuilderSignalCalibrationService.ResolveDriftSafetySignal(0.65d, "degrading")
            : Math.Round(predictiveMatches.Average(match =>
                BuilderSignalCalibrationService.ResolveDriftSafetySignal(match.FailureProbability, match.DriftTrend)), 2);
        return BuilderSignalCalibrationService.EvaluateCompositeScore(
            calibration,
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.RankingSignalId,
                rankingSignal,
                selectedRanking is null
                    ? "No selected ranking is recorded, so readiness uses a neutral ranking baseline."
                    : $"Selected ranking score {rankingSignal:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.IntentSignalId,
                intentAlignment.Score,
                intentAlignment.Summary),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.ConstraintSignalId,
                constraintSignal,
                constraintViolations.Count == 0
                    ? "No active constraint violation is recorded for the selected path."
                    : $"Constraint violations: {string.Join(", ", constraintViolations)}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.TrustSignalId,
                trustSignal,
                trustProfiles.Length == 0
                    ? $"Workspace trust baseline {trust?.TrustScore ?? 50d:0.##}."
                    : $"Matched {trustProfiles.Length} trust profile(s) averaging {trustSignal:0.##}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.GuardrailSignalId,
                guardrailSignal,
                guardrailMatches.Length == 0
                    ? "No matching preventative guardrail is recorded for the selected path."
                    : $"Matched {guardrailMatches.Length} preventative guardrail(s) with highest risk {guardrailMatches.OrderBy(record => RiskRank(record.RiskLevel)).First().RiskLevel}."),
            BuilderSignalCalibrationService.CreateInput(
                BuilderSignalCalibrationService.DriftSignalId,
                driftSignal,
                predictiveMatches.Length == 0
                    ? repeatedFailures.Count == 0
                        ? "No predictive drift forecast or repeated failure trail is recorded, so readiness uses a neutral drift baseline."
                        : $"Repeated failure trail contains {repeatedFailures.Count} recent unsuccessful decision(s)."
                    : $"Matched {predictiveMatches.Length} predictive drift forecast(s) averaging signal {driftSignal:0.##}."));
    }

    private static string ResolveSelectionTargetType(
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryComparisonSetRecord? selectedComparison)
    {
        if (selectedSimulation is not null)
        {
            return "simulation";
        }

        if (selectedComparison is not null)
        {
            return "comparison";
        }

        if (selectedPlaybook is not null)
        {
            return "playbook";
        }

        return "workspace";
    }

    private static string BuildSelectionSummary(
        BuilderRecoveryPlaybookRecord? selectedPlaybook,
        BuilderRecoverySimulationRecord? selectedSimulation,
        BuilderRecoveryComparisonSetRecord? selectedComparison,
        string currentRoute,
        BuilderReviewWorkspaceRecord? reviewWorkspace)
    {
        if (selectedSimulation is not null)
        {
            return selectedComparison is null
                ? $"Selected simulation {FormatToken(selectedSimulation.Scenario)} for {selectedPlaybook?.Title ?? "recovery playbook"} on route {currentRoute}."
                : $"Selected comparison {selectedComparison.BranchLabel} centered on simulation {FormatToken(selectedSimulation.Scenario)} for {selectedPlaybook?.Title ?? "recovery playbook"} on route {currentRoute}.";
        }

        if (selectedPlaybook is not null)
        {
            return $"Selected playbook {selectedPlaybook.Title} on route {currentRoute}.";
        }

        return reviewWorkspace is null
            ? $"Workspace baseline readiness for route {currentRoute} with no recovery-specific selection."
            : $"Workspace baseline readiness for route {currentRoute}. Review state: {FormatToken(reviewWorkspace.ReviewCounts.FinalizeEligibilityState)} across {reviewWorkspace.ReviewCounts.TotalChangedFiles} changed file(s).";
    }

    private static string BuildConstraintSummary(
        BuilderOperatorConstraintProfileRecord? activeConstraintProfile,
        IReadOnlyList<string> constraintViolations)
    {
        if (activeConstraintProfile is null)
        {
            return "No active operator constraint profile is recorded.";
        }

        return constraintViolations.Count == 0
            ? $"Constraint profile {activeConstraintProfile.ProfileName} is active and the selected path is compatible."
            : $"Constraint profile {activeConstraintProfile.ProfileName} is active and currently blocks: {string.Join(", ", constraintViolations)}.";
    }

    private static IReadOnlyList<BuilderExecutionReadinessWarningRecord> AppendSignalBalanceWarning(
        string repoRoot,
        IReadOnlyList<BuilderExecutionReadinessWarningRecord> warnings,
        BuilderSignalEvaluationRecord signalBalance)
    {
        var results = warnings.ToList();
        if (signalBalance.CompositeScore < 45d)
        {
            results.Add(BuildWarning(
                "signal_balance_critical",
                "high",
                "Signal balance indicates the current path is under-evidenced or overly risk-weighted.",
                signalBalance.Summary,
                BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot),
                BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot)));
        }
        else if (signalBalance.CompositeScore < 65d)
        {
            results.Add(BuildWarning(
                "signal_balance_caution",
                "medium",
                "Signal balance remains mixed for the current path.",
                signalBalance.Summary,
                BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderTrustIndexService.TrustIndexPathForRepo(repoRoot),
                BuilderPredictiveDriftService.PredictiveDriftPathForRepo(repoRoot)));
        }

        return results
            .GroupBy(warning => warning.WarningId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(warning => SeverityRank(warning.Severity))
            .ThenBy(warning => warning.Reason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(warning => warning.WarningId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DetermineReadinessState(
        IReadOnlyList<BuilderExecutionReadinessBlockingConditionRecord> blockingConditions,
        IReadOnlyList<BuilderExecutionReadinessWarningRecord> warnings,
        double signalBalanceScore)
        => blockingConditions.Count > 0
            ? "no_go"
            : signalBalanceScore < 45d
                ? "no_go"
            : warnings.Count > 0
                ? "caution"
                : signalBalanceScore < 70d
                    ? "caution"
                : "go";

    private static string BuildSummary(
        string readinessState,
        string selectionSummary,
        IReadOnlyList<BuilderExecutionReadinessBlockingConditionRecord> blockingConditions,
        IReadOnlyList<BuilderExecutionReadinessWarningRecord> warnings,
        IntentAlignmentEvaluation intentAlignment,
        IReadOnlyList<string> constraintViolations,
        BuilderSignalEvaluationRecord signalBalance)
    {
        var lead = readinessState switch
        {
            "go" => "GO",
            "no_go" => "NO-GO",
            _ => "CAUTION"
        };
        return $"{lead}: {selectionSummary} Blocking conditions={blockingConditions.Count}. Warnings={warnings.Count}. Intent alignment={intentAlignment.Aligned}. Constraint violations={constraintViolations.Count}. Signal balance={signalBalance.CompositeScore:0.##}. Advisory only.";
    }

    private static BuilderExecutionReadinessBlockingConditionRecord BuildBlockingCondition(
        string stem,
        string severity,
        string reason,
        string evidenceBasis,
        params object?[] artifactSources)
        => new(
            ComputeDeterministicId("blocker", stem, severity, reason),
            severity,
            reason,
            evidenceBasis,
            BuildArtifactLinks(artifactSources));

    private static BuilderExecutionReadinessWarningRecord BuildWarning(
        string stem,
        string severity,
        string reason,
        string evidenceBasis,
        params object?[] artifactSources)
        => new(
            ComputeDeterministicId("warning", stem, severity, reason),
            severity,
            reason,
            evidenceBasis,
            BuildArtifactLinks(artifactSources));

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

    private static int SeverityRank(string severity)
        => severity switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            "low" => 3,
            _ => 4
        };

    private static int RiskRank(string riskLevel)
        => riskLevel switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"readiness-{hash[..10]}";
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

    private readonly record struct IntentAlignmentEvaluation(bool Aligned, double Score, string Summary);
}
