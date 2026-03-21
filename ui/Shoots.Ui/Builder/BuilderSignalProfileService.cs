using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderSignalProfileWeightRecord(
    string SignalId,
    double Weight);

public sealed record BuilderSignalProfileAdjustmentRangeRecord(
    string SignalId,
    double MinimumDelta,
    double MaximumDelta,
    double StepDelta);

public sealed record BuilderSignalProfileRecord(
    string ProfileId,
    string ProfileName,
    string ProfileType,
    IReadOnlyList<BuilderSignalProfileWeightRecord> BaseWeights,
    IReadOnlyList<BuilderSignalProfileAdjustmentRangeRecord> AllowedAdjustmentRange,
    string Description,
    IReadOnlyList<string> ArtifactLinks);

public sealed record BuilderSignalProfileWeightLimitRecord(
    string SignalId,
    double Weight);

public sealed record BuilderSignalProfileOverrideRecord(
    string SignalId,
    double RequestedDelta,
    double AppliedDelta,
    double EffectiveWeight,
    string OverrideSummary);

public sealed record BuilderSignalOverridePolicyRecord(
    bool OverrideEnabled,
    IReadOnlyList<BuilderSignalProfileWeightLimitRecord> MinWeightPerSignal,
    IReadOnlyList<BuilderSignalProfileWeightLimitRecord> MaxWeightPerSignal,
    string NormalizationRule,
    string OverrideSource);

public sealed record BuilderSignalProfilesRecord(
    string WorkspaceId,
    string SchemaVersion,
    string ActiveProfileId,
    IReadOnlyList<BuilderSignalProfileRecord> Profiles,
    BuilderSignalOverridePolicyRecord OverridePolicy,
    IReadOnlyList<BuilderSignalProfileOverrideRecord> ActiveOverrides,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderSignalProfileService
{
    public const string SignalProfilesFileName = "builder_signal_profiles.json";
    public const string BalancedDefaultProfileId = "balanced_default";
    public const string RiskAverseProfileId = "risk_averse";
    public const string TrustWeightedProfileId = "trust_weighted";
    public const string ConstraintFirstProfileId = "constraint_first";
    public const string HistoricalOutcomeFirstProfileId = "historical_outcome_first";

    private const string SchemaVersion = "builder_signal_profiles.v1";
    private const double GlobalMinimumWeight = 0.05d;
    private const double GlobalMaximumWeight = 0.35d;
    private const double DefaultStepDelta = 0.03d;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SignalOrder =
    {
        BuilderSignalCalibrationService.RankingSignalId,
        BuilderSignalCalibrationService.IntentSignalId,
        BuilderSignalCalibrationService.ConstraintSignalId,
        BuilderSignalCalibrationService.TrustSignalId,
        BuilderSignalCalibrationService.GuardrailSignalId,
        BuilderSignalCalibrationService.DriftSignalId
    };

    public static string SignalProfilesPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), SignalProfilesFileName);

    public static BuilderSignalProfilesRecord? LoadSignalProfiles(string repoRoot)
        => Load<BuilderSignalProfilesRecord>(SignalProfilesPathForRepo(repoRoot));

    public static BuilderSignalProfilesRecord RefreshSignalProfiles(
        string repoRoot,
        string? activeProfileId = null,
        IReadOnlyDictionary<string, double>? overrideDeltas = null,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var profiles = BuildBuiltInProfiles(repoRoot);
        var existing = LoadSignalProfiles(repoRoot);
        var selectedProfileId = ResolveActiveProfileId(activeProfileId, existing?.ActiveProfileId, profiles);
        var activeProfile = ResolveProfileById(profiles, selectedProfileId);
        var effectiveOverrides = ResolveOverrideMap(existing, overrideDeltas);
        var appliedOverrides = BuildAppliedOverrides(activeProfile, effectiveOverrides);
        var overridePolicy = BuildOverridePolicy(activeProfile, appliedOverrides);
        var artifact = new BuilderSignalProfilesRecord(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            SchemaVersion,
            activeProfile.ProfileId,
            profiles,
            overridePolicy,
            appliedOverrides,
            true,
            BuildSummary(activeProfile, appliedOverrides, overridePolicy),
            SignalProfilesPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    public static BuilderSignalProfilesRecord SetActiveProfile(
        string repoRoot,
        string profileId,
        DateTimeOffset? observedUtc = null)
    {
        var existing = LoadSignalProfiles(repoRoot);
        var overrides = existing?.ActiveOverrides.ToDictionary(entry => entry.SignalId, entry => entry.RequestedDelta, StringComparer.OrdinalIgnoreCase);
        return RefreshSignalProfiles(repoRoot, profileId, overrides, observedUtc);
    }

    public static BuilderSignalProfilesRecord SaveOverrides(
        string repoRoot,
        IReadOnlyDictionary<string, double> overrideDeltas,
        DateTimeOffset? observedUtc = null)
    {
        var existing = LoadSignalProfiles(repoRoot);
        return RefreshSignalProfiles(repoRoot, existing?.ActiveProfileId, overrideDeltas, observedUtc);
    }

    public static BuilderSignalProfilesRecord ResetOverrides(
        string repoRoot,
        DateTimeOffset? observedUtc = null)
    {
        var existing = LoadSignalProfiles(repoRoot);
        return RefreshSignalProfiles(repoRoot, existing?.ActiveProfileId, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase), observedUtc);
    }

    public static BuilderSignalProfileRecord ResolveActiveProfile(BuilderSignalProfilesRecord? record)
        => ResolveProfileById(BuildBuiltInProfiles(string.Empty), record?.ActiveProfileId);

    public static string ResolveOverrideHash(BuilderSignalProfilesRecord? record)
    {
        if (record is null)
        {
            return "profile-default";
        }

        var payload = string.Join("|",
            new[] { record.ActiveProfileId.Trim() }
                .Concat(record.ActiveOverrides
                    .OrderBy(entry => SignalRank(entry.SignalId))
                    .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => $"{entry.SignalId}:{entry.AppliedDelta:0.0000}")));
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"profile-{hash[..10]}";
    }

    public static IReadOnlyList<double> GetAllowedOverrideSteps(BuilderSignalProfilesRecord? record, string signalId)
    {
        var activeProfile = ResolveActiveProfile(record);
        var range = activeProfile.AllowedAdjustmentRange.FirstOrDefault(entry =>
            string.Equals(entry.SignalId, signalId, StringComparison.OrdinalIgnoreCase));
        if (range is null)
        {
            return new[] { 0d };
        }

        var steps = new List<double>();
        var step = Math.Abs(range.StepDelta) < 0.0001d ? DefaultStepDelta : Math.Abs(range.StepDelta);
        for (var value = range.MinimumDelta; value <= range.MaximumDelta + 0.0001d; value += step)
        {
            steps.Add(Math.Round(value, 4));
        }

        if (!steps.Contains(0d))
        {
            steps.Add(0d);
        }

        return steps
            .Distinct()
            .OrderBy(entry => entry)
            .ToArray();
    }

    public static double ResolveOverrideDelta(BuilderSignalProfilesRecord? record, string signalId)
        => (record?.ActiveOverrides ?? Array.Empty<BuilderSignalProfileOverrideRecord>())
            .FirstOrDefault(entry => string.Equals(entry.SignalId, signalId, StringComparison.OrdinalIgnoreCase))
            ?.AppliedDelta ?? 0d;

    public static string GetProfileLabel(string? profileId)
        => Normalize(profileId) switch
        {
            BalancedDefaultProfileId => "Balanced Default",
            RiskAverseProfileId => "Risk Averse",
            TrustWeightedProfileId => "Trust Weighted",
            ConstraintFirstProfileId => "Constraint First",
            HistoricalOutcomeFirstProfileId => "Historical Outcome First",
            _ => "Balanced Default"
        };

    public static string FormatOverrideLabel(double value)
        => Math.Abs(value) < 0.0001d
            ? "Default"
            : value > 0d
                ? $"+{value:P0}"
                : $"{value:P0}";

    private static BuilderSignalProfileRecord[] BuildBuiltInProfiles(string repoRoot)
        => new[]
        {
            CreateProfile(
                repoRoot,
                BalancedDefaultProfileId,
                "Balanced Default",
                "balanced_default",
                new[]
                {
                    Weight(BuilderSignalCalibrationService.RankingSignalId, 0.24d),
                    Weight(BuilderSignalCalibrationService.IntentSignalId, 0.12d),
                    Weight(BuilderSignalCalibrationService.ConstraintSignalId, 0.16d),
                    Weight(BuilderSignalCalibrationService.TrustSignalId, 0.16d),
                    Weight(BuilderSignalCalibrationService.GuardrailSignalId, 0.16d),
                    Weight(BuilderSignalCalibrationService.DriftSignalId, 0.16d)
                },
                "Balanced profile keeps Phase 74 baseline weighting across ranking, trust, constraint, guardrail, and drift signals."),
            CreateProfile(
                repoRoot,
                RiskAverseProfileId,
                "Risk Averse",
                "risk_averse",
                new[]
                {
                    Weight(BuilderSignalCalibrationService.RankingSignalId, 0.14d),
                    Weight(BuilderSignalCalibrationService.IntentSignalId, 0.08d),
                    Weight(BuilderSignalCalibrationService.ConstraintSignalId, 0.20d),
                    Weight(BuilderSignalCalibrationService.TrustSignalId, 0.14d),
                    Weight(BuilderSignalCalibrationService.GuardrailSignalId, 0.22d),
                    Weight(BuilderSignalCalibrationService.DriftSignalId, 0.22d)
                },
                "Risk-averse profile amplifies guardrails, drift, and hard constraints before preference-led ranking signals."),
            CreateProfile(
                repoRoot,
                TrustWeightedProfileId,
                "Trust Weighted",
                "trust_weighted",
                new[]
                {
                    Weight(BuilderSignalCalibrationService.RankingSignalId, 0.22d),
                    Weight(BuilderSignalCalibrationService.IntentSignalId, 0.10d),
                    Weight(BuilderSignalCalibrationService.ConstraintSignalId, 0.12d),
                    Weight(BuilderSignalCalibrationService.TrustSignalId, 0.24d),
                    Weight(BuilderSignalCalibrationService.GuardrailSignalId, 0.16d),
                    Weight(BuilderSignalCalibrationService.DriftSignalId, 0.16d)
                },
                "Trust-weighted profile leans harder on historically reliable guidance when stability is strong."),
            CreateProfile(
                repoRoot,
                ConstraintFirstProfileId,
                "Constraint First",
                "constraint_first",
                new[]
                {
                    Weight(BuilderSignalCalibrationService.RankingSignalId, 0.12d),
                    Weight(BuilderSignalCalibrationService.IntentSignalId, 0.08d),
                    Weight(BuilderSignalCalibrationService.ConstraintSignalId, 0.30d),
                    Weight(BuilderSignalCalibrationService.TrustSignalId, 0.12d),
                    Weight(BuilderSignalCalibrationService.GuardrailSignalId, 0.20d),
                    Weight(BuilderSignalCalibrationService.DriftSignalId, 0.18d)
                },
                "Constraint-first profile gives explicit operator boundaries the strongest initial influence."),
            CreateProfile(
                repoRoot,
                HistoricalOutcomeFirstProfileId,
                "Historical Outcome First",
                "historical_outcome_first",
                new[]
                {
                    Weight(BuilderSignalCalibrationService.RankingSignalId, 0.28d),
                    Weight(BuilderSignalCalibrationService.IntentSignalId, 0.08d),
                    Weight(BuilderSignalCalibrationService.ConstraintSignalId, 0.12d),
                    Weight(BuilderSignalCalibrationService.TrustSignalId, 0.22d),
                    Weight(BuilderSignalCalibrationService.GuardrailSignalId, 0.14d),
                    Weight(BuilderSignalCalibrationService.DriftSignalId, 0.16d)
                },
                "Historical-outcome-first profile leans harder on ranking and trust signals derived from prior successful results.")
        };

    private static BuilderSignalProfileRecord CreateProfile(
        string repoRoot,
        string profileId,
        string profileName,
        string profileType,
        IReadOnlyList<BuilderSignalProfileWeightRecord> weights,
        string description)
    {
        var ranges = weights
            .Select(weight => new BuilderSignalProfileAdjustmentRangeRecord(
                weight.SignalId,
                RoundDelta(ResolveMinimumWeight(weight.Weight) - weight.Weight),
                RoundDelta(ResolveMaximumWeight(weight.Weight) - weight.Weight),
                DefaultStepDelta))
            .OrderBy(range => SignalRank(range.SignalId))
            .ThenBy(range => range.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BuilderSignalProfileRecord(
            profileId,
            profileName,
            profileType,
            weights.OrderBy(weight => SignalRank(weight.SignalId)).ThenBy(weight => weight.SignalId, StringComparer.OrdinalIgnoreCase).ToArray(),
            ranges,
            description,
            BuildArtifactLinks(repoRoot));
    }

    private static BuilderSignalProfileWeightRecord Weight(string signalId, double weight)
        => new(signalId, weight);

    private static BuilderSignalProfileRecord ResolveProfileById(IReadOnlyList<BuilderSignalProfileRecord> profiles, string? profileId)
        => profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, Normalize(profileId), StringComparison.OrdinalIgnoreCase))
           ?? profiles.First(profile => string.Equals(profile.ProfileId, BalancedDefaultProfileId, StringComparison.OrdinalIgnoreCase));

    private static string ResolveActiveProfileId(
        string? requestedProfileId,
        string? existingProfileId,
        IReadOnlyList<BuilderSignalProfileRecord> profiles)
    {
        var requested = Normalize(requestedProfileId);
        if (profiles.Any(profile => string.Equals(profile.ProfileId, requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        var existing = Normalize(existingProfileId);
        if (profiles.Any(profile => string.Equals(profile.ProfileId, existing, StringComparison.OrdinalIgnoreCase)))
        {
            return existing;
        }

        return BalancedDefaultProfileId;
    }

    private static IReadOnlyDictionary<string, double> ResolveOverrideMap(
        BuilderSignalProfilesRecord? existing,
        IReadOnlyDictionary<string, double>? overrideDeltas)
    {
        if (overrideDeltas is not null)
        {
            return new Dictionary<string, double>(overrideDeltas, StringComparer.OrdinalIgnoreCase);
        }

        return (existing?.ActiveOverrides ?? Array.Empty<BuilderSignalProfileOverrideRecord>())
            .GroupBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().RequestedDelta, StringComparer.OrdinalIgnoreCase);
    }

    private static BuilderSignalProfileOverrideRecord[] BuildAppliedOverrides(
        BuilderSignalProfileRecord activeProfile,
        IReadOnlyDictionary<string, double> requestedOverrides)
    {
        return SignalOrder
            .Select(signalId =>
            {
                var baseWeight = activeProfile.BaseWeights.First(weight => string.Equals(weight.SignalId, signalId, StringComparison.OrdinalIgnoreCase)).Weight;
                var range = activeProfile.AllowedAdjustmentRange.First(range => string.Equals(range.SignalId, signalId, StringComparison.OrdinalIgnoreCase));
                var requestedDelta = requestedOverrides.TryGetValue(signalId, out var delta) ? delta : 0d;
                var appliedDelta = SanitizeDelta(requestedDelta, range);
                var effectiveWeight = Math.Round(ClampWeight(baseWeight + appliedDelta), 4);
                return new BuilderSignalProfileOverrideRecord(
                    signalId,
                    Math.Round(requestedDelta, 4),
                    appliedDelta,
                    effectiveWeight,
                    Math.Abs(appliedDelta) < 0.0001d
                        ? "No operator override applied. Profile default weight remains active before contextual calibration."
                        : $"Operator override requests {FormatOverrideLabel(appliedDelta)} for {BuilderSignalCalibrationService.GetSignalLabel(signalId)}. Effective pre-context weight becomes {effectiveWeight:P0} before Phase 74 contextual shifts.");
            })
            .OrderBy(entry => SignalRank(entry.SignalId))
            .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BuilderSignalOverridePolicyRecord BuildOverridePolicy(
        BuilderSignalProfileRecord activeProfile,
        IReadOnlyList<BuilderSignalProfileOverrideRecord> appliedOverrides)
    {
        var minWeights = activeProfile.BaseWeights
            .Select(weight =>
            {
                var range = activeProfile.AllowedAdjustmentRange.First(entry => string.Equals(entry.SignalId, weight.SignalId, StringComparison.OrdinalIgnoreCase));
                return new BuilderSignalProfileWeightLimitRecord(weight.SignalId, ClampWeight(weight.Weight + range.MinimumDelta));
            })
            .OrderBy(entry => SignalRank(entry.SignalId))
            .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maxWeights = activeProfile.BaseWeights
            .Select(weight =>
            {
                var range = activeProfile.AllowedAdjustmentRange.First(entry => string.Equals(entry.SignalId, weight.SignalId, StringComparison.OrdinalIgnoreCase));
                return new BuilderSignalProfileWeightLimitRecord(weight.SignalId, ClampWeight(weight.Weight + range.MaximumDelta));
            })
            .OrderBy(entry => SignalRank(entry.SignalId))
            .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BuilderSignalOverridePolicyRecord(
            true,
            minWeights,
            maxWeights,
            "profile_base -> phase74_context_shift -> bounded_operator_override -> normalize_to_one",
            appliedOverrides.Any(entry => Math.Abs(entry.AppliedDelta) > 0.0001d) ? "operator_selected" : "profile_default");
    }

    private static IReadOnlyList<string> BuildArtifactLinks(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return Array.Empty<string>();
        }

        return new[]
            {
                BuilderSignalCalibrationService.SignalCalibrationPathForRepo(repoRoot),
                BuilderOperatorDecisionService.OperatorDecisionsPathForRepo(repoRoot)
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildSummary(
        BuilderSignalProfileRecord activeProfile,
        IReadOnlyList<BuilderSignalProfileOverrideRecord> appliedOverrides,
        BuilderSignalOverridePolicyRecord overridePolicy)
    {
        var activeOverrideCount = appliedOverrides.Count(entry => Math.Abs(entry.AppliedDelta) > 0.0001d);
        return activeOverrideCount == 0
            ? $"Active signal profile {activeProfile.ProfileName} applies deterministic base weights with no bounded overrides. Override policy remains {overridePolicy.OverrideSource.Replace('_', ' ')}."
            : $"Active signal profile {activeProfile.ProfileName} applies {activeOverrideCount} bounded override(s) under deterministic normalization. Override source: {overridePolicy.OverrideSource.Replace('_', ' ')}.";
    }

    private static double ResolveMinimumWeight(double baseWeight)
        => ClampWeight(baseWeight - 0.06d);

    private static double ResolveMaximumWeight(double baseWeight)
        => ClampWeight(baseWeight + 0.06d);

    private static double SanitizeDelta(double requestedDelta, BuilderSignalProfileAdjustmentRangeRecord range)
    {
        var clamped = Math.Max(range.MinimumDelta, Math.Min(range.MaximumDelta, requestedDelta));
        var step = Math.Abs(range.StepDelta) < 0.0001d ? DefaultStepDelta : Math.Abs(range.StepDelta);
        var stepped = Math.Round(Math.Round(clamped / step, MidpointRounding.AwayFromZero) * step, 4);
        return Math.Max(range.MinimumDelta, Math.Min(range.MaximumDelta, RoundDelta(stepped)));
    }

    private static double ClampWeight(double value)
        => Math.Round(Math.Max(GlobalMinimumWeight, Math.Min(GlobalMaximumWeight, value)), 4);

    private static double RoundDelta(double value)
        => Math.Round(value, 4);

    private static string Normalize(string? value)
        => value?.Trim() ?? string.Empty;

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
