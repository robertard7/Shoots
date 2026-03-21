using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderSignalCalibrationWeightRecord(
    string SignalId,
    double BaseWeight,
    double AdjustedWeight,
    double AdjustmentDelta,
    string AdjustmentReason,
    double ProfileWeight = 0d,
    double ContextAdjustedWeight = 0d,
    double OverrideAdjustedWeight = 0d,
    double ContextAdjustmentDelta = 0d,
    double OverrideDelta = 0d);

public sealed record BuilderSignalContributionRecord(
    string SignalId,
    double Weight,
    double SignalScore,
    double WeightedContribution,
    string ContributionReason)
{
    public string Summary
        => $"{BuilderSignalCalibrationService.GetSignalLabel(SignalId)}: weight {Weight:P0}, signal {SignalScore:0.##}, contribution {WeightedContribution:0.##}. {ContributionReason}";
}

public sealed record BuilderSignalInputRecord(
    string SignalId,
    double SignalScore,
    string ContributionReason);

public sealed record BuilderSignalEvaluationRecord(
    double CompositeScore,
    string Summary,
    IReadOnlyList<BuilderSignalContributionRecord> Contributions);

public sealed record BuilderSignalCalibrationRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderSignalCalibrationWeightRecord> Weights,
    string CalibrationProfile,
    IReadOnlyList<string> EvidenceLinks,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc,
    string ActiveProfileId = "",
    string ActiveProfileName = "",
    string ProfileOverrideHash = "",
    string ContextShiftSummary = "",
    string OverrideSummary = "",
    string ProfileArtifactPath = "");

public static class BuilderSignalCalibrationService
{
    public const string SignalCalibrationFileName = "builder_signal_calibration.json";
    public const string RankingSignalId = "ranking";
    public const string IntentSignalId = "intent";
    public const string ConstraintSignalId = "constraint";
    public const string TrustSignalId = "trust";
    public const string GuardrailSignalId = "guardrail";
    public const string DriftSignalId = "drift";

    private const string SchemaVersion = "builder_signal_calibration.v2";
    private const double RankingBaseWeight = 0.24d;
    private const double IntentBaseWeight = 0.12d;
    private const double ConstraintBaseWeight = 0.16d;
    private const double TrustBaseWeight = 0.16d;
    private const double GuardrailBaseWeight = 0.16d;
    private const double DriftBaseWeight = 0.16d;
    private const double LowTrustThreshold = 55d;
    private const double HighStabilityThreshold = 72d;
    private const double ConstraintHeavyThreshold = 45d;
    private const double RiskEscalationThreshold = 60d;
    private static readonly string[] SignalOrder =
    {
        RankingSignalId,
        IntentSignalId,
        ConstraintSignalId,
        TrustSignalId,
        GuardrailSignalId,
        DriftSignalId
    };

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string SignalCalibrationPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), SignalCalibrationFileName);

    public static BuilderSignalCalibrationRecord? LoadSignalCalibration(string repoRoot)
        => Load<BuilderSignalCalibrationRecord>(SignalCalibrationPathForRepo(repoRoot));

    public static BuilderSignalCalibrationRecord RefreshSignalCalibration(
        string repoRoot,
        BuilderPlaybookRankingsRecord? rankings = null,
        BuilderPlaybookContextFiltersRecord? contextFilters = null,
        BuilderOperatorConstraintsRecord? constraints = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderExecutionAuditReport? audit = null,
        BuilderPreventativeGuardrailsReport? guardrails = null,
        BuilderOperatorIntentRecord? operatorIntent = null,
        DateTimeOffset? observedUtc = null,
        BuilderSignalProfilesRecord? profiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        rankings ??= BuilderPlaybookRankingService.LoadPlaybookRankings(repoRoot);
        contextFilters ??= BuilderPlaybookContextFilterService.LoadContextFilters(repoRoot);
        constraints ??= BuilderOperatorConstraintService.LoadOperatorConstraints(repoRoot);
        accuracy ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(repoRoot);
        decisions ??= BuilderOperatorDecisionService.LoadOperatorDecisions(repoRoot);
        audit ??= BuilderExecutionAuditService.LoadExecutionAudit(repoRoot);
        guardrails ??= BuilderPreventativeGuardrailService.LoadPreventativeGuardrails(repoRoot);
        operatorIntent ??= BuilderOperatorIntentService.LoadOperatorIntent(repoRoot);
        profiles ??= BuilderSignalProfileService.LoadSignalProfiles(repoRoot) ??
                     BuilderSignalProfileService.RefreshSignalProfiles(repoRoot, observedUtc: observedUtc);

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var activeProfile = BuilderSignalProfileService.ResolveActiveProfile(profiles);
        var trustBaseline = ComputeTrustBaselineScore(accuracy, decisions, audit);
        var stabilityScore = ComputeStabilityScore(audit, guardrails);
        var constraintPressure = ComputeConstraintPressure(contextFilters, constraints);
        var guardrailPressure = ComputeGuardrailPressure(guardrails);
        var driftPressure = ComputeDriftPressure(audit, decisions);
        var weights = CreateBaseWeights(activeProfile);
        var reasons = SignalOrder.ToDictionary(signal => signal, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var signal in SignalOrder)
        {
            var profileWeight = activeProfile.BaseWeights.FirstOrDefault(entry => string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase))?.Weight ?? BaseWeightForSignal(signal);
            reasons[signal].Add($"Active signal profile {activeProfile.ProfileName} sets deterministic base weight {profileWeight:P0}.");
        }

        var profileTokens = new List<string> { activeProfile.ProfileType };
        var contextReasons = new List<string>();

        if (trustBaseline < LowTrustThreshold)
        {
            profileTokens.Add("risk_amplified");
            contextReasons.Add($"Trust baseline {trustBaseline:0.##} is below the low-trust threshold {LowTrustThreshold:0.##}, so protective weighting is amplified.");
            ApplyAdjustment(weights, reasons, GuardrailSignalId, 0.06d, "Low trust baseline increases preventative guardrail emphasis.");
            ApplyAdjustment(weights, reasons, DriftSignalId, 0.06d, "Low trust baseline increases predictive drift emphasis.");
            ApplyAdjustment(weights, reasons, RankingSignalId, -0.05d, "Low trust reduces reliance on static ranking evidence.");
            ApplyAdjustment(weights, reasons, IntentSignalId, -0.03d, "Low trust reduces intent-led preference weighting until evidence stabilizes.");
            ApplyAdjustment(weights, reasons, TrustSignalId, -0.04d, "Low trust shifts balance toward protective signals instead of historical confidence.");
        }

        if (stabilityScore >= HighStabilityThreshold &&
            driftPressure < RiskEscalationThreshold &&
            guardrailPressure < RiskEscalationThreshold)
        {
            profileTokens.Add("stability_favored");
            contextReasons.Add($"Stability score {stabilityScore:0.##} is above the high-stability threshold {HighStabilityThreshold:0.##}, so ranking and trust are allowed more influence.");
            ApplyAdjustment(weights, reasons, RankingSignalId, 0.08d, "High stability allows stronger ranking influence.");
            ApplyAdjustment(weights, reasons, TrustSignalId, 0.02d, "Stable history increases confidence in trust-backed evidence.");
            ApplyAdjustment(weights, reasons, GuardrailSignalId, -0.05d, "Stable history reduces protective guardrail amplification.");
            ApplyAdjustment(weights, reasons, DriftSignalId, -0.05d, "Stable history reduces drift pressure weighting.");
        }

        if (constraintPressure >= ConstraintHeavyThreshold)
        {
            profileTokens.Add("constraint_heavy");
            contextReasons.Add($"Constraint pressure {constraintPressure:0.##} exceeds the constraint-heavy threshold {ConstraintHeavyThreshold:0.##}, so explicit limits gain extra influence.");
            ApplyAdjustment(weights, reasons, ConstraintSignalId, 0.10d, "Constraint-heavy context gives hard boundaries more authority.");
            ApplyAdjustment(weights, reasons, RankingSignalId, -0.04d, "Constraint-heavy context trims ranking influence.");
            ApplyAdjustment(weights, reasons, IntentSignalId, -0.03d, "Constraint-heavy context trims preference-driven intent influence.");
            ApplyAdjustment(weights, reasons, TrustSignalId, -0.03d, "Constraint-heavy context shifts balance away from historical trust and toward explicit limits.");
        }

        if (guardrailPressure >= RiskEscalationThreshold || driftPressure >= RiskEscalationThreshold)
        {
            profileTokens.Add("protective_bias");
            contextReasons.Add($"Guardrail pressure {guardrailPressure:0.##} or drift pressure {driftPressure:0.##} crosses the protective threshold {RiskEscalationThreshold:0.##}, so risk-sensitive signals are amplified.");
            ApplyAdjustment(weights, reasons, GuardrailSignalId, 0.04d, "Elevated guardrail pressure strengthens preventative weighting.");
            ApplyAdjustment(weights, reasons, DriftSignalId, 0.04d, "Elevated drift pressure strengthens forecast weighting.");
            ApplyAdjustment(weights, reasons, RankingSignalId, -0.04d, "Protective bias reduces ranking dominance when risk signals escalate.");
            ApplyAdjustment(weights, reasons, IntentSignalId, -0.02d, "Protective bias trims intent emphasis when risk signals escalate.");
            ApplyAdjustment(weights, reasons, TrustSignalId, -0.02d, "Protective bias trims trust emphasis when risk signals escalate.");
        }

        var contextAdjustedWeights = SignalOrder.ToDictionary(signal => signal, signal => weights[signal], StringComparer.OrdinalIgnoreCase);
        var overrideWeights = ApplyProfileOverrides(weights, activeProfile, profiles, reasons);
        var normalizedWeights = NormalizeWeights(weights);
        var weightRecords = SignalOrder
            .Select((signal, index) =>
            {
                var baseWeight = BaseWeightForSignal(signal);
                var profileWeight = activeProfile.BaseWeights.FirstOrDefault(entry => string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase))?.Weight ?? baseWeight;
                var contextAdjustedWeight = contextAdjustedWeights[signal];
                var overrideAdjustedWeight = overrideWeights[signal];
                var adjustedWeight = normalizedWeights[index];
                var delta = Math.Round(adjustedWeight - baseWeight, 4);
                var reason = reasons[signal].Count == 0
                    ? "No contextual shift applied. Base deterministic weight retained."
                    : string.Join(" ", reasons[signal]
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                return new BuilderSignalCalibrationWeightRecord(
                    signal,
                    baseWeight,
                    adjustedWeight,
                    delta,
                    reason,
                    profileWeight,
                    contextAdjustedWeight,
                    overrideAdjustedWeight,
                    Math.Round(contextAdjustedWeight - profileWeight, 4),
                    Math.Round(overrideAdjustedWeight - contextAdjustedWeight, 4));
            })
            .ToArray();
        var profile = string.Join(" + ", profileTokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => ProfileRank(value))
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase));
        var evidenceLinks = BuildEvidenceLinks(
            BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot),
            BuilderPlaybookRankingService.PlaybookRankingsPathForRepo(repoRoot),
            BuilderPlaybookContextFilterService.PlaybookContextFiltersPathForRepo(repoRoot),
            BuilderOperatorConstraintService.OperatorConstraintsPathForRepo(repoRoot),
            BuilderSimulationAccuracyService.SimulationAccuracyPathForRepo(repoRoot),
            BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot),
            BuilderExecutionAuditService.ExecutionAuditPathForRepo(repoRoot),
            BuilderPreventativeGuardrailService.PreventativeGuardrailsPathForRepo(repoRoot),
            BuilderOperatorIntentService.OperatorIntentPathForRepo(repoRoot));
        var contextShiftSummary = BuildContextShiftSummary(contextReasons);
        var overrideSummary = BuildOverrideSummary(activeProfile, profiles);
        var record = new BuilderSignalCalibrationRecord(
            workspaceId,
            SchemaVersion,
            weightRecords,
            profile,
            evidenceLinks,
            true,
            BuildSummary(workspaceId, profile, activeProfile, trustBaseline, stabilityScore, constraintPressure, guardrailPressure, driftPressure, weightRecords, operatorIntent, contextShiftSummary, overrideSummary),
            SignalCalibrationPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow,
            activeProfile.ProfileId,
            activeProfile.ProfileName,
            BuilderSignalProfileService.ResolveOverrideHash(profiles),
            contextShiftSummary,
            overrideSummary,
            profiles?.ArtifactPath ?? BuilderSignalProfileService.SignalProfilesPathForRepo(repoRoot));
        Save(record.ArtifactPath, record);
        return record;
    }

    public static BuilderSignalEvaluationRecord EvaluateCompositeScore(
        BuilderSignalCalibrationRecord? calibration,
        params BuilderSignalInputRecord[] inputs)
    {
        var inputMap = inputs
            .Where(input => input is not null && !string.IsNullOrWhiteSpace(input.SignalId))
            .GroupBy(input => input.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var weights = ResolveWeightMap(calibration);
        var contributions = SignalOrder
            .Select(signal =>
            {
                var input = inputMap.TryGetValue(signal, out var value)
                    ? value
                    : new BuilderSignalInputRecord(signal, 50d, "No explicit signal was provided, so a deterministic neutral baseline is applied.");
                var score = ClampScore(input.SignalScore);
                var weight = weights[signal];
                var contribution = Math.Round(weight * score, 2);
                return new BuilderSignalContributionRecord(
                    signal,
                    weight,
                    score,
                    contribution,
                    input.ContributionReason);
            })
            .OrderByDescending(entry => entry.WeightedContribution)
            .ThenBy(entry => SignalRank(entry.SignalId))
            .ToArray();
        var compositeScore = Math.Round(contributions.Sum(entry => entry.WeightedContribution), 2);
        var strongest = contributions.First();
        var summary = $"Composite score {compositeScore:0.##}. Strongest signal: {GetSignalLabel(strongest.SignalId)} at {strongest.WeightedContribution:0.##}. Calibration profile: {calibration?.CalibrationProfile ?? "balanced default"}. Active signal profile: {calibration?.ActiveProfileName ?? "Balanced Default"}.";
        return new BuilderSignalEvaluationRecord(compositeScore, summary, contributions);
    }

    public static BuilderSignalInputRecord CreateInput(string signalId, double signalScore, string reason)
        => new(signalId, ClampScore(signalScore), reason);

    public static string GetSignalLabel(string signalId)
        => signalId switch
        {
            RankingSignalId => "Ranking",
            IntentSignalId => "Intent",
            ConstraintSignalId => "Constraint",
            TrustSignalId => "Trust",
            GuardrailSignalId => "Guardrail",
            DriftSignalId => "Drift",
            _ => signalId
        };

    public static double ResolveConstraintSignal(bool blocked)
        => blocked ? 0d : 100d;

    public static double ResolveGuardrailSafetySignal(string riskLevel)
        => riskLevel switch
        {
            "critical" => 5d,
            "high" => 28d,
            "moderate" => 58d,
            _ => 92d
        };

    public static double ResolveDriftSafetySignal(double failureProbability, string driftTrend = "")
    {
        var baseline = ClampScore((1d - Clamp01(failureProbability)) * 100d);
        var adjustment = driftTrend switch
        {
            "critical_trajectory" => -20d,
            "degrading" => -12d,
            "unstable" => -8d,
            _ => 6d
        };
        return ClampScore(baseline + adjustment);
    }

    public static double ResolveReadinessModifier(string readinessState)
        => readinessState switch
        {
            "go" => 4d,
            "no_go" => -12d,
            _ => -3d
        };

    private static Dictionary<string, double> CreateBaseWeights(BuilderSignalProfileRecord activeProfile)
        => SignalOrder.ToDictionary(
            signal => signal,
            signal => activeProfile.BaseWeights.FirstOrDefault(entry => string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase))?.Weight ?? BaseWeightForSignal(signal),
            StringComparer.OrdinalIgnoreCase);

    private static void ApplyAdjustment(
        IDictionary<string, double> weights,
        IDictionary<string, List<string>> reasons,
        string signalId,
        double delta,
        string reason)
    {
        weights[signalId] = Math.Max(0.04d, weights[signalId] + delta);
        reasons[signalId].Add(reason);
    }

    private static Dictionary<string, double> ApplyProfileOverrides(
        IDictionary<string, double> weights,
        BuilderSignalProfileRecord activeProfile,
        BuilderSignalProfilesRecord? profiles,
        IDictionary<string, List<string>> reasons)
    {
        var overrideMap = (profiles?.ActiveOverrides ?? Array.Empty<BuilderSignalProfileOverrideRecord>())
            .GroupBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var adjusted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in SignalOrder)
        {
            var range = activeProfile.AllowedAdjustmentRange.FirstOrDefault(entry =>
                string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase));
            var minWeight = range is null
                ? weights[signal]
                : Math.Max(0.04d, activeProfile.BaseWeights.First(entry => string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase)).Weight + range.MinimumDelta);
            var maxWeight = range is null
                ? weights[signal]
                : Math.Max(minWeight, activeProfile.BaseWeights.First(entry => string.Equals(entry.SignalId, signal, StringComparison.OrdinalIgnoreCase)).Weight + range.MaximumDelta);
            var requestedDelta = overrideMap.TryGetValue(signal, out var overrideRecord) ? overrideRecord.AppliedDelta : 0d;
            var rawWeight = weights[signal] + requestedDelta;
            var clampedWeight = Math.Max(minWeight, Math.Min(maxWeight, rawWeight));
            var appliedDelta = Math.Round(clampedWeight - weights[signal], 4);
            if (Math.Abs(appliedDelta) > 0.0001d)
            {
                reasons[signal].Add($"Bounded operator override contributes {appliedDelta:+0.0%;-0.0%;0.0%} after contextual shifts.");
            }
            else if (overrideMap.ContainsKey(signal))
            {
                reasons[signal].Add("Operator override is active but clamped to the deterministic range for this signal.");
            }

            weights[signal] = clampedWeight;
            adjusted[signal] = clampedWeight;
        }

        return adjusted;
    }

    private static double[] NormalizeWeights(IReadOnlyDictionary<string, double> weights)
    {
        var ordered = SignalOrder
            .Select(signal => Math.Max(0.04d, weights.TryGetValue(signal, out var value) ? value : BaseWeightForSignal(signal)))
            .ToArray();
        var total = ordered.Sum();
        if (total <= 0d)
        {
            return SignalOrder.Select(BaseWeightForSignal).ToArray();
        }

        var normalized = new double[ordered.Length];
        double running = 0d;
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i == ordered.Length - 1)
            {
                normalized[i] = Math.Round(Math.Max(0d, 1d - running), 4);
                break;
            }

            normalized[i] = Math.Round(ordered[i] / total, 4);
            running += normalized[i];
        }

        if (normalized.Sum() != 1d)
        {
            normalized[^1] = Math.Round(1d - normalized.Take(normalized.Length - 1).Sum(), 4);
        }

        return normalized;
    }

    private static Dictionary<string, double> ResolveWeightMap(BuilderSignalCalibrationRecord? calibration)
    {
        var values = calibration?.Weights
            .GroupBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().AdjustedWeight, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in SignalOrder)
        {
            if (!values.ContainsKey(signal))
            {
                values[signal] = BaseWeightForSignal(signal);
            }
        }

        return values;
    }

    private static double BaseWeightForSignal(string signalId)
        => signalId switch
        {
            RankingSignalId => RankingBaseWeight,
            IntentSignalId => IntentBaseWeight,
            ConstraintSignalId => ConstraintBaseWeight,
            TrustSignalId => TrustBaseWeight,
            GuardrailSignalId => GuardrailBaseWeight,
            DriftSignalId => DriftBaseWeight,
            _ => 0d
        };

    private static double ComputeTrustBaselineScore(
        BuilderSimulationAccuracyReport? accuracy,
        BuilderOperatorDecisionsRecord? decisions,
        BuilderExecutionAuditReport? audit)
    {
        var accuracyRecords = accuracy?.AccuracyRecords ?? Array.Empty<BuilderSimulationAccuracyRecord>();
        var accuracyRate = accuracyRecords.Count > 0
            ? accuracyRecords.Count(record => record.AccuracyFlag) / (double)accuracyRecords.Count
            : 0.50d;
        var decisionRecords = decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>();
        var operatorSuccessRate = decisionRecords.Count > 0
            ? decisionRecords.Count(record => record.SuccessFlag) / (double)decisionRecords.Count
            : 0.50d;
        var auditRecords = audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>();
        var driftConsistency = auditRecords.Count > 0
            ? auditRecords.Average(record => record.DriftType switch
            {
                "no_drift" => 1.00d,
                "minor_drift" => 0.72d,
                "unexpected_success" => 0.55d,
                "major_drift" => 0.30d,
                "unexpected_failure" => 0.10d,
                _ => 0.50d
            })
            : 0.50d;
        return Math.Round((((0.45d * accuracyRate) + (0.30d * operatorSuccessRate) + (0.25d * driftConsistency)) * 100d), 2);
    }

    private static double ComputeStabilityScore(
        BuilderExecutionAuditReport? audit,
        BuilderPreventativeGuardrailsReport? guardrails)
    {
        var auditRecords = audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>();
        var guardrailRecords = guardrails?.Guardrails ?? Array.Empty<BuilderPreventativeGuardrailRecord>();
        if (auditRecords.Count == 0 && guardrailRecords.Count == 0)
        {
            return 50d;
        }

        var majorDriftCount = auditRecords.Count(record =>
            string.Equals(record.DriftType, "major_drift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.DriftType, "unexpected_failure", StringComparison.OrdinalIgnoreCase));
        var elevatedGuardrails = guardrailRecords.Count(record =>
            string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
        var denominator = Math.Max(1d, auditRecords.Count + guardrailRecords.Count);
        var pressure = ((majorDriftCount * 1.0d) + (elevatedGuardrails * 0.75d)) / denominator;
        return Math.Round(ClampScore((1d - Clamp01(pressure)) * 100d), 2);
    }

    private static double ComputeConstraintPressure(
        BuilderPlaybookContextFiltersRecord? contextFilters,
        BuilderOperatorConstraintsRecord? constraints)
    {
        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraints);
        var activeConstraintCount = activeProfile?.Constraints.Count ?? 0;
        var violatingPlaybooks = contextFilters?.RelevanceScores.Count(entry => entry.ViolatesConstraints) ?? 0;
        if (activeConstraintCount == 0 && violatingPlaybooks == 0)
        {
            return 0d;
        }

        return Math.Round(Math.Min(100d, (activeConstraintCount * 18d) + (violatingPlaybooks * 7d)), 2);
    }

    private static double ComputeGuardrailPressure(BuilderPreventativeGuardrailsReport? guardrails)
    {
        var records = guardrails?.Guardrails ?? Array.Empty<BuilderPreventativeGuardrailRecord>();
        if (records.Count == 0)
        {
            return 25d;
        }

        var average = records.Average(record => record.RiskLevel switch
        {
            "critical" => 95d,
            "high" => 75d,
            "moderate" => 50d,
            _ => 20d
        });
        return Math.Round(average, 2);
    }

    private static double ComputeDriftPressure(
        BuilderExecutionAuditReport? audit,
        BuilderOperatorDecisionsRecord? decisions)
    {
        var auditRecords = audit?.AuditRecords ?? Array.Empty<BuilderExecutionAuditRecord>();
        var decisionRecords = decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>();
        if (auditRecords.Count == 0 && decisionRecords.Count == 0)
        {
            return 25d;
        }

        var auditPressure = auditRecords.Count == 0
            ? 25d
            : auditRecords.Average(record => record.DriftType switch
            {
                "unexpected_failure" => 95d,
                "major_drift" => 80d,
                "minor_drift" => 55d,
                "unexpected_success" => 35d,
                _ => 15d
            });
        var decisionPressure = decisionRecords.Count == 0
            ? 25d
            : ((decisionRecords.Count(record => !record.SuccessFlag) / (double)decisionRecords.Count) * 100d);
        return Math.Round(((0.65d * auditPressure) + (0.35d * decisionPressure)), 2);
    }

    private static string BuildSummary(
        string workspaceId,
        string profile,
        BuilderSignalProfileRecord activeProfile,
        double trustBaseline,
        double stabilityScore,
        double constraintPressure,
        double guardrailPressure,
        double driftPressure,
        IReadOnlyList<BuilderSignalCalibrationWeightRecord> weights,
        BuilderOperatorIntentRecord? operatorIntent,
        string contextShiftSummary,
        string overrideSummary)
    {
        var dominant = weights
            .OrderByDescending(entry => entry.AdjustedWeight)
            .ThenBy(entry => SignalRank(entry.SignalId))
            .First();
        var intentSummary = string.IsNullOrWhiteSpace(operatorIntent?.Intent)
            ? "No explicit operator intent is currently recorded."
            : $"Operator intent: {BuilderOperatorIntentService.GetIntentLabel(operatorIntent.Intent)}.";
        return $"Signal calibration for {workspaceId} uses profile {profile} from {activeProfile.ProfileName}. Dominant weight: {GetSignalLabel(dominant.SignalId)} at {dominant.AdjustedWeight:P0}. Trust baseline {trustBaseline:0.##}, stability {stabilityScore:0.##}, constraint pressure {constraintPressure:0.##}, guardrail pressure {guardrailPressure:0.##}, drift pressure {driftPressure:0.##}. {contextShiftSummary} {overrideSummary} {intentSummary}";
    }

    private static string BuildContextShiftSummary(IReadOnlyList<string> contextReasons)
        => contextReasons.Count == 0
            ? "No Phase 74 contextual shift moved the active profile weights."
            : string.Join(" ", contextReasons
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static string BuildOverrideSummary(
        BuilderSignalProfileRecord activeProfile,
        BuilderSignalProfilesRecord? profiles)
    {
        var activeOverrides = (profiles?.ActiveOverrides ?? Array.Empty<BuilderSignalProfileOverrideRecord>())
            .Where(entry => Math.Abs(entry.AppliedDelta) > 0.0001d)
            .OrderBy(entry => SignalRank(entry.SignalId))
            .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return activeOverrides.Length == 0
            ? $"No bounded overrides are active for {activeProfile.ProfileName}."
            : $"Bounded overrides are active for {activeOverrides.Length} signal(s) under {activeProfile.ProfileName} ({BuilderSignalProfileService.ResolveOverrideHash(profiles)}).";
    }

    private static IReadOnlyList<string> BuildEvidenceLinks(params string[] paths)
        => paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static double ClampScore(double value)
        => Math.Max(0d, Math.Min(100d, value));

    private static double Clamp01(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static int ProfileRank(string value)
        => value switch
        {
            "balanced_default" => -5,
            "risk_averse" => -4,
            "trust_weighted" => -3,
            "constraint_first" => -2,
            "historical_outcome_first" => -1,
            "balanced" => 0,
            "stability_favored" => 1,
            "constraint_heavy" => 2,
            "risk_amplified" => 3,
            "protective_bias" => 4,
            _ => 5
        };

    private static int SignalRank(string signalId)
        => Array.IndexOf(SignalOrder, signalId) switch
        {
            < 0 => int.MaxValue,
            var value => value
        };

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
