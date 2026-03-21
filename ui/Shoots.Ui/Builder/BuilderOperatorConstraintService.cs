using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderOperatorConstraintRecord(
    string ConstraintId,
    string ConstraintType,
    string ConstraintValue,
    string Scope)
{
    public string Summary
        => string.IsNullOrWhiteSpace(ConstraintValue)
            ? $"{BuilderOperatorConstraintService.GetConstraintLabel(ConstraintType)} ({BuilderOperatorConstraintService.FormatScope(Scope)})"
            : $"{BuilderOperatorConstraintService.GetConstraintLabel(ConstraintType)} [{ConstraintValue}] ({BuilderOperatorConstraintService.FormatScope(Scope)})";
}

public sealed record BuilderOperatorConstraintProfileRecord(
    string ProfileId,
    string ProfileName,
    IReadOnlyList<BuilderOperatorConstraintRecord> Constraints,
    string Summary);

public sealed record BuilderOperatorConstraintsRecord(
    string WorkspaceId,
    string SchemaVersion,
    string ActiveProfileId,
    IReadOnlyList<BuilderOperatorConstraintProfileRecord> Profiles,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPlaybookConstraintEvaluationRecord(
    string ActiveProfileId,
    bool ViolatesConstraints,
    IReadOnlyList<string> ViolatedConstraints,
    string ConstraintReason);

public sealed record BuilderSimulationConstraintEvaluationRecord(
    string ActiveProfileId,
    string ConstraintCompatibility,
    IReadOnlyList<string> BlockedByConstraints,
    string ConstraintReason);

public static class BuilderOperatorConstraintService
{
    public const string OperatorConstraintsFileName = "builder_operator_constraints.json";
    public const string BlockHighRiskFilesConstraint = "block_high_risk_files";
    public const string BlockSpecificRouteConstraint = "block_specific_route";
    public const string BlockCrossRepoActionsConstraint = "block_cross_repo_actions";
    public const string LimitToSingleRepoConstraint = "limit_to_single_repo";
    public const string BlockFinalizeUntilReviewCleanConstraint = "block_finalize_until_review_clean";
    public const string BlockPartialOrchestrationConstraint = "block_partial_orchestration";

    private const string SchemaVersion = "builder_operator_constraints.v1";
    private static readonly string[] SupportedConstraintTypes =
    {
        BlockCrossRepoActionsConstraint,
        BlockFinalizeUntilReviewCleanConstraint,
        BlockHighRiskFilesConstraint,
        BlockPartialOrchestrationConstraint,
        BlockSpecificRouteConstraint,
        LimitToSingleRepoConstraint
    };
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string OperatorConstraintsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), OperatorConstraintsFileName);

    public static BuilderOperatorConstraintsRecord? LoadOperatorConstraints(string repoRoot)
        => Load<BuilderOperatorConstraintsRecord>(OperatorConstraintsPathForRepo(repoRoot));

    public static IReadOnlyList<string> GetSupportedConstraintTypes()
        => SupportedConstraintTypes.ToArray();

    public static bool IsSupportedConstraintType(string? constraintType)
        => SupportedConstraintTypes.Contains(Normalize(constraintType), StringComparer.OrdinalIgnoreCase);

    public static string GetConstraintLabel(string? constraintType)
        => Normalize(constraintType) switch
        {
            BlockHighRiskFilesConstraint => "Block High-Risk Files",
            BlockSpecificRouteConstraint => "Block Specific Route",
            BlockCrossRepoActionsConstraint => "Block Cross-Repo Actions",
            LimitToSingleRepoConstraint => "Limit To Single Repo",
            BlockFinalizeUntilReviewCleanConstraint => "Block Finalize Until Review Clean",
            BlockPartialOrchestrationConstraint => "Block Partial Orchestration",
            _ => "Unknown Constraint"
        };

    public static string FormatScope(string? scope)
        => string.IsNullOrWhiteSpace(scope) ? "global" : scope.Trim().Replace('_', ' ');

    public static BuilderOperatorConstraintRecord CreateConstraint(
        string constraintType,
        string? constraintValue = null,
        string? scope = null)
    {
        var normalizedType = Normalize(constraintType);
        if (!IsSupportedConstraintType(normalizedType))
        {
            throw new ArgumentOutOfRangeException(nameof(constraintType), constraintType, "Constraint type must match a supported deterministic operator constraint.");
        }

        var normalizedScope = string.IsNullOrWhiteSpace(scope)
            ? ResolveDefaultScope(normalizedType)
            : scope.Trim();
        var normalizedValue = NormalizeConstraintValue(normalizedType, constraintValue);
        return new BuilderOperatorConstraintRecord(
            BuildConstraintId(normalizedType, normalizedScope, normalizedValue),
            normalizedType,
            normalizedValue,
            normalizedScope);
    }

    public static BuilderOperatorConstraintsRecord SaveOperatorConstraints(
        string repoRoot,
        string activeProfileId,
        IEnumerable<BuilderOperatorConstraintProfileRecord> profiles,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(profiles);

        var orderedProfiles = profiles
            .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.ProfileId))
            .Select(NormalizeProfile)
            .GroupBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedActiveProfileId = orderedProfiles.Any(profile => string.Equals(profile.ProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase))
            ? activeProfileId
            : orderedProfiles.FirstOrDefault()?.ProfileId ?? string.Empty;
        var activeProfile = orderedProfiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, normalizedActiveProfileId, StringComparison.OrdinalIgnoreCase));
        var artifact = new BuilderOperatorConstraintsRecord(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            SchemaVersion,
            normalizedActiveProfileId,
            orderedProfiles,
            true,
            activeProfile is null
                ? "No active operator constraint profile is recorded. Constraint filtering remains advisory only and reversible."
                : $"Active operator constraint profile {activeProfile.ProfileName} records {activeProfile.Constraints.Count} explicit hard-bound constraint(s). Constraint filtering remains advisory only and reversible.",
            OperatorConstraintsPathForRepo(repoRoot),
            observedUtc ?? DateTimeOffset.UtcNow);
        Save(artifact.ArtifactPath, artifact);
        return artifact;
    }

    public static BuilderOperatorConstraintsRecord SetActiveProfile(
        string repoRoot,
        string profileId,
        DateTimeOffset? observedUtc = null)
    {
        var existing = LoadOperatorConstraints(repoRoot);
        if (existing is null)
        {
            throw new InvalidOperationException("No operator constraint artifact is recorded for this workspace.");
        }

        return SaveOperatorConstraints(repoRoot, profileId, existing.Profiles, observedUtc);
    }

    public static BuilderOperatorConstraintsRecord CreateOrUpdateProfile(
        string repoRoot,
        string profileName,
        IEnumerable<BuilderOperatorConstraintRecord> constraints,
        bool makeActive = true,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(constraints);

        var existing = LoadOperatorConstraints(repoRoot);
        var profileId = BuildProfileId(profileName);
        var normalizedProfile = NormalizeProfile(new BuilderOperatorConstraintProfileRecord(
            profileId,
            profileName.Trim(),
            constraints.ToArray(),
            string.Empty));
        var profiles = (existing?.Profiles ?? Array.Empty<BuilderOperatorConstraintProfileRecord>())
            .Where(profile => !string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            .Concat(new[] { normalizedProfile })
            .ToArray();
        return SaveOperatorConstraints(
            repoRoot,
            makeActive ? profileId : existing?.ActiveProfileId ?? profileId,
            profiles,
            observedUtc);
    }

    public static BuilderOperatorConstraintProfileRecord? ResolveActiveProfile(BuilderOperatorConstraintsRecord? constraints)
    {
        if (constraints is null)
        {
            return null;
        }

        return constraints.Profiles.FirstOrDefault(profile =>
                   string.Equals(profile.ProfileId, constraints.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
               ?? constraints.Profiles.FirstOrDefault();
    }

    public static BuilderPlaybookConstraintEvaluationRecord EvaluatePlaybookConstraints(
        BuilderRecoveryPlaybookRecord playbook,
        BuilderOperatorConstraintsRecord? constraints,
        string currentBlockingState,
        bool isHighRiskContext)
    {
        ArgumentNullException.ThrowIfNull(playbook);

        var activeProfile = ResolveActiveProfile(constraints);
        if (activeProfile is null || activeProfile.Constraints.Count == 0)
        {
            return new BuilderPlaybookConstraintEvaluationRecord(
                constraints?.ActiveProfileId ?? string.Empty,
                false,
                Array.Empty<string>(),
                "No explicit operator constraint blocks this playbook.");
        }

        var violations = activeProfile.Constraints
            .Select(constraint => EvaluatePlaybookConstraint(constraint, playbook, currentBlockingState, isHighRiskContext))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderPlaybookConstraintEvaluationRecord(
            activeProfile.ProfileId,
            violations.Length > 0,
            violations,
            violations.Length == 0
                ? $"Profile {activeProfile.ProfileName} does not block this playbook."
                : $"Profile {activeProfile.ProfileName} blocks this playbook because {string.Join(" ", violations)}");
    }

    public static BuilderSimulationConstraintEvaluationRecord EvaluateSimulationConstraints(
        BuilderRecoverySimulationRecord simulation,
        BuilderRecoveryPlaybookRecord? playbook,
        BuilderOperatorConstraintsRecord? constraints,
        string currentBlockingState,
        bool isHighRiskContext,
        bool isCrossRepoBlock)
    {
        ArgumentNullException.ThrowIfNull(simulation);

        var activeProfile = ResolveActiveProfile(constraints);
        if (activeProfile is null || activeProfile.Constraints.Count == 0)
        {
            return new BuilderSimulationConstraintEvaluationRecord(
                constraints?.ActiveProfileId ?? string.Empty,
                "compatible",
                Array.Empty<string>(),
                "No explicit operator constraint blocks this what-if scenario.");
        }

        var blockedBy = activeProfile.Constraints
            .Select(constraint => EvaluateSimulationConstraint(constraint, simulation, playbook, currentBlockingState, isHighRiskContext, isCrossRepoBlock))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderSimulationConstraintEvaluationRecord(
            activeProfile.ProfileId,
            blockedBy.Length == 0 ? "compatible" : "blocked_by_constraints",
            blockedBy,
            blockedBy.Length == 0
                ? $"Profile {activeProfile.ProfileName} allows this what-if scenario."
                : $"Profile {activeProfile.ProfileName} blocks this what-if scenario because {string.Join(" ", blockedBy)}");
    }

    private static BuilderOperatorConstraintProfileRecord NormalizeProfile(BuilderOperatorConstraintProfileRecord profile)
    {
        var constraints = profile.Constraints
            .Where(constraint => constraint is not null && IsSupportedConstraintType(constraint.ConstraintType))
            .Select(constraint => CreateConstraint(constraint.ConstraintType, constraint.ConstraintValue, constraint.Scope))
            .GroupBy(constraint => constraint.ConstraintId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(constraint => constraint.ConstraintType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(constraint => constraint.ConstraintValue, StringComparer.OrdinalIgnoreCase)
            .ThenBy(constraint => constraint.Scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return profile with
        {
            ProfileId = BuildProfileId(profile.ProfileName, profile.ProfileId),
            ProfileName = profile.ProfileName.Trim(),
            Constraints = constraints,
            Summary = constraints.Length == 0
                ? $"{profile.ProfileName.Trim()} has no explicit hard-bound constraints."
                : $"{profile.ProfileName.Trim()} enforces {constraints.Length} hard-bound constraint(s): {string.Join("; ", constraints.Select(constraint => constraint.Summary))}."
        };
    }

    private static string? EvaluatePlaybookConstraint(
        BuilderOperatorConstraintRecord constraint,
        BuilderRecoveryPlaybookRecord playbook,
        string currentBlockingState,
        bool isHighRiskContext)
    {
        switch (constraint.ConstraintType)
        {
            case BlockHighRiskFilesConstraint when isHighRiskContext &&
                                                  (playbook.RequiresApprovalGate ||
                                                   string.Equals(playbook.FailureClass, "high_risk_change_stalled", StringComparison.OrdinalIgnoreCase) ||
                                                   playbook.RecommendedSteps.Any(step =>
                                                       step.ActionType.Contains("high_risk", StringComparison.OrdinalIgnoreCase) ||
                                                       step.OperatorAction.Contains("high-risk", StringComparison.OrdinalIgnoreCase))):
                return $"{constraint.Summary} rejects high-risk recovery paths that still require explicit approval.";

            case BlockSpecificRouteConstraint when playbook.AppliesToRoutes.Contains(constraint.ConstraintValue, StringComparer.OrdinalIgnoreCase):
                return $"{constraint.Summary} blocks route {constraint.ConstraintValue} for this playbook.";

            case BlockCrossRepoActionsConstraint when playbook.CrossRepoScope:
                return $"{constraint.Summary} blocks cross-repo recovery steps.";

            case LimitToSingleRepoConstraint when playbook.CrossRepoScope:
                return $"{constraint.Summary} limits recovery to the active workspace only.";

            case BlockFinalizeUntilReviewCleanConstraint when playbook.RequiresFinalizeGate && !IsReviewClean(currentBlockingState):
                return $"{constraint.Summary} rejects finalize-oriented playbooks while review is not clean.";

            case BlockPartialOrchestrationConstraint when playbook.CrossRepoScope &&
                                                          (string.Equals(playbook.FailureClass, "orchestration_blocked", StringComparison.OrdinalIgnoreCase) ||
                                                           string.Equals(playbook.FailureClass, "cross_repo_dependency_block", StringComparison.OrdinalIgnoreCase) ||
                                                           playbook.RecommendedSteps.Any(step =>
                                                               step.ActionType.Contains("stage", StringComparison.OrdinalIgnoreCase) ||
                                                               step.OperatorAction.Contains("stage", StringComparison.OrdinalIgnoreCase) ||
                                                               step.ActionType.Contains("recover_upstream", StringComparison.OrdinalIgnoreCase))):
                return $"{constraint.Summary} blocks staged or partially recovered orchestration plans.";

            default:
                return null;
        }
    }

    private static string? EvaluateSimulationConstraint(
        BuilderOperatorConstraintRecord constraint,
        BuilderRecoverySimulationRecord simulation,
        BuilderRecoveryPlaybookRecord? playbook,
        string currentBlockingState,
        bool isHighRiskContext,
        bool isCrossRepoBlock)
    {
        var playbookIsCrossRepo = playbook?.CrossRepoScope ?? false;
        switch (constraint.ConstraintType)
        {
            case BlockHighRiskFilesConstraint when isHighRiskContext &&
                                                  (string.Equals(simulation.Scenario, "isolate_high_risk_files", StringComparison.OrdinalIgnoreCase) ||
                                                   simulation.RiskFlags.Any(flag => flag.Contains("high_risk", StringComparison.OrdinalIgnoreCase)) ||
                                                   simulation.BlockingConditions.Any(condition => condition.Contains("explicit_approval", StringComparison.OrdinalIgnoreCase)) ||
                                                   playbook?.RequiresApprovalGate == true):
                return $"{constraint.Summary} blocks scenarios that keep high-risk files in scope.";

            case BlockSpecificRouteConstraint when string.Equals(simulation.TargetRoute, constraint.ConstraintValue, StringComparison.OrdinalIgnoreCase):
                return $"{constraint.Summary} blocks route {constraint.ConstraintValue} for this scenario.";

            case BlockCrossRepoActionsConstraint when playbookIsCrossRepo ||
                                                     string.Equals(simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase) ||
                                                     isCrossRepoBlock:
                return $"{constraint.Summary} blocks cross-repo orchestration recovery scenarios.";

            case LimitToSingleRepoConstraint when playbookIsCrossRepo ||
                                                  string.Equals(simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase):
                return $"{constraint.Summary} restricts this scenario to a single-repo recovery path.";

            case BlockFinalizeUntilReviewCleanConstraint when string.Equals(simulation.ExpectedNextBlockingGate, "finalize_gate", StringComparison.OrdinalIgnoreCase) &&
                                                              !IsReviewClean(currentBlockingState):
                return $"{constraint.Summary} blocks scenarios that would lead directly to finalize while review remains open.";

            case BlockPartialOrchestrationConstraint when string.Equals(simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(simulation.PredictedOutcomeClass, "partial_success", StringComparison.OrdinalIgnoreCase) && playbookIsCrossRepo:
                return $"{constraint.Summary} blocks staged or partial orchestration recovery scenarios.";

            default:
                return null;
        }
    }

    private static bool IsReviewClean(string? blockingState)
        => string.Equals(blockingState, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(blockingState, "no_changed_files", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(blockingState, "finalized", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(blockingState, "applied", StringComparison.OrdinalIgnoreCase);

    private static string ResolveDefaultScope(string normalizedType)
        => normalizedType switch
        {
            BlockSpecificRouteConstraint => "route",
            LimitToSingleRepoConstraint => "repo",
            _ => "global"
        };

    private static string NormalizeConstraintValue(string normalizedType, string? value)
    {
        var normalizedValue = value?.Trim() ?? string.Empty;
        return normalizedType switch
        {
            BlockSpecificRouteConstraint => normalizedValue,
            _ => normalizedValue
        };
    }

    private static string BuildConstraintId(string constraintType, string scope, string value)
        => $"{Normalize(constraintType)}|{Normalize(scope)}|{Normalize(value)}";

    private static string BuildProfileId(string profileName, string? fallbackId = null)
    {
        if (!string.IsNullOrWhiteSpace(fallbackId))
        {
            return fallbackId.Trim();
        }

        var builder = new StringBuilder("profile");
        foreach (var character in profileName.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var profileId = builder.ToString().TrimEnd('-');
        return string.IsNullOrWhiteSpace(profileId) ? "profile-active-constraints" : profileId;
    }

    private static string Normalize(string? value)
        => value?.Trim() ?? string.Empty;

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
