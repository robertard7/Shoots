using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderRouteRecommendationEntryRecord(
    string Route,
    int SuccessCount,
    int FailureCount,
    double HistoricalSuccessRate,
    double HistoricalFailureRate,
    string ModelTierSuggestion,
    string ReasoningSummary)
{
    public string Summary => $"{Route}: success {HistoricalSuccessRate:0.##}% ({SuccessCount}), failure {HistoricalFailureRate:0.##}% ({FailureCount}), suggested tier {ModelTierSuggestion}. {ReasoningSummary}";
}

public sealed record BuilderRouteRecommendationsRecord(
    string RequestId,
    string WorkspaceId,
    IReadOnlyList<BuilderRouteRecommendationEntryRecord> RecommendedRoutes,
    double HistoricalSuccessRate,
    double HistoricalFailureRate,
    IReadOnlyList<string> ModelTierSuggestions,
    string ReasoningSummary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteRiskWarningEntryRecord(
    string Workspace,
    string RouteAttempted,
    string WarningReason,
    string RelatedKnowledgeGraphNode,
    DateTimeOffset ObservedUtc)
{
    public string Summary => $"{Workspace}: {RouteAttempted}. {WarningReason} (node: {RelatedKnowledgeGraphNode}).";
}

public sealed record BuilderRouteRiskWarningsRecord(
    string RequestId,
    string WorkspaceId,
    IReadOnlyList<BuilderRouteRiskWarningEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderOrchestrationRecommendationsRecord(
    string RequestId,
    IReadOnlyList<string> ParticipatingWorkspaceIds,
    IReadOnlyList<string> RecommendedOrchestrationSequence,
    IReadOnlyList<string> HistoricallySuccessfulWorkspaceOrdering,
    IReadOnlyList<string> OrderingWarnings,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc)
{
    public string RecommendedSequenceSummary => RecommendedOrchestrationSequence.Count == 0
        ? "No recommended orchestration sequence recorded."
        : string.Join(" -> ", RecommendedOrchestrationSequence);

    public string HistoricalOrderingSummary => HistoricallySuccessfulWorkspaceOrdering.Count == 0
        ? "No historical workspace ordering recorded."
        : string.Join(" -> ", HistoricallySuccessfulWorkspaceOrdering);
}

public sealed record BuilderRouteIntelligenceContext(
    BuilderRouteRecommendationsRecord RouteRecommendations,
    BuilderRouteRiskWarningsRecord RiskWarnings,
    BuilderOrchestrationRecommendationsRecord OrchestrationRecommendations);

public static class BuilderRouteIntelligenceService
{
    public const string RouteRecommendationsFileName = "builder_route_recommendations.json";
    public const string RouteRiskWarningsFileName = "builder_route_risk_warnings.json";
    public const string OrchestrationRecommendationsFileName = "builder_orchestration_recommendations.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string RouteRecommendationsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), RouteRecommendationsFileName);

    public static string RouteRiskWarningsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), RouteRiskWarningsFileName);

    public static string OrchestrationRecommendationsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), OrchestrationRecommendationsFileName);

    public static BuilderRouteRecommendationsRecord? LoadRouteRecommendations(string repoRoot)
        => Load<BuilderRouteRecommendationsRecord>(RouteRecommendationsPathForRepo(repoRoot));

    public static BuilderRouteRiskWarningsRecord? LoadRouteRiskWarnings(string repoRoot)
        => Load<BuilderRouteRiskWarningsRecord>(RouteRiskWarningsPathForRepo(repoRoot));

    public static BuilderOrchestrationRecommendationsRecord? LoadOrchestrationRecommendations(string repoRoot)
        => Load<BuilderOrchestrationRecommendationsRecord>(OrchestrationRecommendationsPathForRepo(repoRoot));

    public static BuilderRouteIntelligenceContext? RefreshRouteIntelligenceArtifacts(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        BuilderCrossRepoOrchestrationContext orchestration,
        string activeWorkspaceId,
        string requestId,
        DateTimeOffset? observedUtc = null,
        int maxRecommendations = 5,
        int maxWarnings = 8)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(orchestration);

        var descriptors = workspaces
            .Where(descriptor => descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.RepoRootPath))
            .Select(descriptor => BuilderWorkspaceService.CreateDescriptor(descriptor.RepoRootPath, descriptor.RepoName))
            .Where(descriptor => Directory.Exists(descriptor.RepoRootPath))
            .GroupBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (descriptors.Length == 0)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? "builder_route_intelligence" : requestId.Trim();
        var activeDescriptor = descriptors.FirstOrDefault(descriptor =>
                                   string.Equals(descriptor.WorkspaceId, activeWorkspaceId, StringComparison.OrdinalIgnoreCase))
                               ?? descriptors[0];
        var routeObservations = BuildRouteObservations(activeDescriptor.RepoRootPath);
        var workspaceStatuses = orchestration.ExecutionState.WorkspaceStatusList
            .ToDictionary(status => status.WorkspaceId, StringComparer.OrdinalIgnoreCase);

        BuilderRouteRecommendationsRecord? activeRecommendations = null;
        BuilderRouteRiskWarningsRecord? activeWarnings = null;
        foreach (var descriptor in descriptors)
        {
            workspaceStatuses.TryGetValue(descriptor.WorkspaceId, out var status);
            var currentRoute = status?.RouteDecision
                               ?? BuilderWorkspaceService.LoadRouteResolution(descriptor.RepoRootPath)?.RouteDecision
                               ?? "not_recorded";
            var currentModelTier = status?.ModelTier ?? "not_recorded";
            var recommendations = BuildRouteRecommendations(
                normalizedRequestId,
                descriptor,
                currentRoute,
                currentModelTier,
                routeObservations,
                effectiveObservedUtc,
                maxRecommendations);
            var warnings = BuildRiskWarnings(
                normalizedRequestId,
                descriptor,
                currentRoute,
                currentModelTier,
                routeObservations,
                effectiveObservedUtc,
                maxWarnings);

            Save(RouteRecommendationsPathForRepo(descriptor.RepoRootPath), recommendations);
            Save(RouteRiskWarningsPathForRepo(descriptor.RepoRootPath), warnings);

            if (string.Equals(descriptor.WorkspaceId, activeDescriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                activeRecommendations = recommendations;
                activeWarnings = warnings;
            }
        }

        var orchestrationRecommendations = BuildOrchestrationRecommendations(
            normalizedRequestId,
            activeDescriptor.RepoRootPath,
            orchestration,
            effectiveObservedUtc);
        foreach (var descriptor in descriptors)
        {
            Save(OrchestrationRecommendationsPathForRepo(descriptor.RepoRootPath), orchestrationRecommendations with
            {
                ArtifactPath = OrchestrationRecommendationsPathForRepo(descriptor.RepoRootPath)
            });
        }

        return new BuilderRouteIntelligenceContext(
            activeRecommendations ?? LoadRouteRecommendations(activeDescriptor.RepoRootPath)!,
            activeWarnings ?? LoadRouteRiskWarnings(activeDescriptor.RepoRootPath)!,
            orchestrationRecommendations with { ArtifactPath = OrchestrationRecommendationsPathForRepo(activeDescriptor.RepoRootPath) });
    }

    private static IReadOnlyList<RouteObservation> BuildRouteObservations(string repoRoot)
    {
        var patterns = BuilderKnowledgeGraphService.LoadExecutionPatterns(repoRoot);
        var failures = BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
        var observations = new List<RouteObservation>();

        foreach (var pattern in patterns?.Entries ?? Array.Empty<BuilderExecutionPatternRecord>())
        {
            var routeMap = ParseTaggedMap(pattern.OrchestrationRoute);
            var modelTierMap = ParseTaggedMap(pattern.ModelTier);
            foreach (var pair in routeMap.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                observations.Add(new RouteObservation(
                    pair.Key,
                    pair.Value,
                    modelTierMap.TryGetValue(pair.Key, out var modelTier) ? modelTier : "not_recorded",
                    true,
                    pattern.FinalizeResult,
                    pattern.ReviewOutcome,
                    pattern.ObservedUtc));
            }
        }

        foreach (var failure in failures?.Entries ?? Array.Empty<BuilderFailurePatternRecord>())
        {
            observations.Add(new RouteObservation(
                failure.Workspace,
                failure.RouteAttempted,
                failure.ModelTier,
                false,
                failure.RejectionState,
                failure.FailureReason,
                failure.ObservedUtc));
        }

        return observations
            .OrderBy(observation => observation.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.ModelTier, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(observation => observation.ObservedUtc)
            .ToArray();
    }

    private static BuilderRouteRecommendationsRecord BuildRouteRecommendations(
        string requestId,
        BuilderWorkspaceDescriptor descriptor,
        string currentRoute,
        string currentModelTier,
        IReadOnlyList<RouteObservation> observations,
        DateTimeOffset observedUtc,
        int maxRecommendations)
    {
        var workspaceObservations = observations
            .Where(observation => string.Equals(observation.WorkspaceId, descriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var candidateRoutes = workspaceObservations
            .Select(observation => observation.Route)
            .Append(currentRoute)
            .Where(route => !string.IsNullOrWhiteSpace(route))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = candidateRoutes
            .Select(route => BuildRecommendationEntry(route, currentModelTier, workspaceObservations))
            .OrderByDescending(entry => entry.HistoricalSuccessRate)
            .ThenByDescending(entry => entry.SuccessCount)
            .ThenBy(entry => entry.FailureCount)
            .ThenBy(entry => entry.Route, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxRecommendations, 0))
            .ToArray();

        var totalSuccess = workspaceObservations.Count(observation => observation.IsSuccess);
        var totalFailure = workspaceObservations.Count(observation => !observation.IsSuccess);
        var totalObservations = totalSuccess + totalFailure;
        var overallSuccessRate = totalObservations == 0 ? 0d : Math.Round(totalSuccess * 100d / totalObservations, 2);
        var overallFailureRate = totalObservations == 0 ? 0d : Math.Round(totalFailure * 100d / totalObservations, 2);
        var reasoningSummary = entries.Length == 0
            ? $"No historical route observations are recorded for {descriptor.WorkspaceId}. Current route remains advisory-only."
            : $"Top advisory route for {descriptor.WorkspaceId}: {entries[0].Route}. Current route: {currentRoute}. Recommendations do not override routing policy.";

        return new BuilderRouteRecommendationsRecord(
            requestId,
            descriptor.WorkspaceId,
            entries,
            overallSuccessRate,
            overallFailureRate,
            entries.Select(entry => $"{entry.Route}:{entry.ModelTierSuggestion}").ToArray(),
            reasoningSummary,
            RouteRecommendationsPathForRepo(descriptor.RepoRootPath),
            observedUtc);
    }

    private static BuilderRouteRecommendationEntryRecord BuildRecommendationEntry(
        string route,
        string currentModelTier,
        IReadOnlyList<RouteObservation> observations)
    {
        var routeObservations = observations
            .Where(observation => string.Equals(observation.Route, route, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var successCount = routeObservations.Count(observation => observation.IsSuccess);
        var failureCount = routeObservations.Count(observation => !observation.IsSuccess);
        var total = successCount + failureCount;
        var successRate = total == 0 ? 0d : Math.Round(successCount * 100d / total, 2);
        var failureRate = total == 0 ? 0d : Math.Round(failureCount * 100d / total, 2);
        var modelTierSuggestion = routeObservations
            .Where(observation => observation.IsSuccess)
            .GroupBy(observation => observation.ModelTier, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault()
            ?? (string.IsNullOrWhiteSpace(currentModelTier) ? "not_recorded" : currentModelTier);

        var reasons = new List<string>();
        if (successCount > 0)
        {
            reasons.Add($"Finalized successfully {successCount} time(s).");
        }

        if (failureCount > 0)
        {
            reasons.Add($"Blocked or rejected {failureCount} time(s).");
        }

        if (successCount > failureCount && successCount > 0)
        {
            reasons.Add("Historical outcomes favor this route.");
        }
        else if (failureCount >= successCount && failureCount > 0)
        {
            reasons.Add("Historical risk meets or exceeds finalized evidence.");
        }

        if (!string.IsNullOrWhiteSpace(modelTierSuggestion))
        {
            reasons.Add($"Suggested tier: {modelTierSuggestion}.");
        }

        return new BuilderRouteRecommendationEntryRecord(
            route,
            successCount,
            failureCount,
            successRate,
            failureRate,
            modelTierSuggestion,
            reasons.Count == 0 ? "No historical evidence recorded." : string.Join(" ", reasons));
    }

    private static BuilderRouteRiskWarningsRecord BuildRiskWarnings(
        string requestId,
        BuilderWorkspaceDescriptor descriptor,
        string currentRoute,
        string currentModelTier,
        IReadOnlyList<RouteObservation> observations,
        DateTimeOffset observedUtc,
        int maxWarnings)
    {
        var workspaceObservations = observations
            .Where(observation => string.Equals(observation.WorkspaceId, descriptor.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var currentRouteObservations = workspaceObservations
            .Where(observation => string.Equals(observation.Route, currentRoute, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var warnings = new List<BuilderRouteRiskWarningEntryRecord>();
        var failureCount = currentRouteObservations.Count(observation => !observation.IsSuccess);
        var successCount = currentRouteObservations.Count(observation => observation.IsSuccess);
        if (failureCount >= 2)
        {
            warnings.Add(new BuilderRouteRiskWarningEntryRecord(
                descriptor.WorkspaceId,
                currentRoute,
                $"Route repeatedly failed in this workspace ({failureCount} blocked outcomes).",
                currentRoute,
                observedUtc));
        }
        else if (failureCount == 1)
        {
            warnings.Add(new BuilderRouteRiskWarningEntryRecord(
                descriptor.WorkspaceId,
                currentRoute,
                "Route has a prior blocked or rejected outcome in this workspace.",
                currentRoute,
                observedUtc));
        }

        if (currentRouteObservations.Any(observation =>
                !observation.IsSuccess &&
                string.Equals(observation.ModelTier, currentModelTier, StringComparison.OrdinalIgnoreCase)) &&
            currentRouteObservations.All(observation =>
                !observation.IsSuccess ||
                !string.Equals(observation.ModelTier, currentModelTier, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new BuilderRouteRiskWarningEntryRecord(
                descriptor.WorkspaceId,
                currentRoute,
                $"Model tier {currentModelTier} is historically associated with blocked outcomes on this route.",
                $"{currentRoute}|{currentModelTier}",
                observedUtc));
        }

        if (currentRouteObservations.Any(observation =>
                !observation.IsSuccess &&
                string.Equals(observation.OutcomeState, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new BuilderRouteRiskWarningEntryRecord(
                descriptor.WorkspaceId,
                currentRoute,
                "This route previously produced revision-request blockers.",
                currentRoute,
                observedUtc));
        }

        if (failureCount > successCount && failureCount > 0)
        {
            warnings.Add(new BuilderRouteRiskWarningEntryRecord(
                descriptor.WorkspaceId,
                currentRoute,
                "Historical failure rate exceeds finalized success evidence for the current route.",
                currentRoute,
                observedUtc));
        }

        var orderedWarnings = warnings
            .GroupBy(warning => $"{warning.Workspace}|{warning.RouteAttempted}|{warning.WarningReason}|{warning.RelatedKnowledgeGraphNode}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(warning => warning.Workspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(warning => warning.RouteAttempted, StringComparer.OrdinalIgnoreCase)
            .ThenBy(warning => warning.WarningReason, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxWarnings, 0))
            .ToArray();

        return new BuilderRouteRiskWarningsRecord(
            requestId,
            descriptor.WorkspaceId,
            orderedWarnings,
            orderedWarnings.Length == 0
                ? $"No historical route risk warnings were raised for {descriptor.WorkspaceId}."
                : $"Generated {orderedWarnings.Length} route risk warning(s) for {descriptor.WorkspaceId}.",
            RouteRiskWarningsPathForRepo(descriptor.RepoRootPath),
            observedUtc);
    }

    private static BuilderOrchestrationRecommendationsRecord BuildOrchestrationRecommendations(
        string requestId,
        string repoRoot,
        BuilderCrossRepoOrchestrationContext orchestration,
        DateTimeOffset observedUtc)
    {
        var currentSequence = orchestration.Plan.ParticipatingWorkspaceIds
            .Where(workspaceId => !string.IsNullOrWhiteSpace(workspaceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var patterns = BuilderKnowledgeGraphService.LoadExecutionPatterns(repoRoot);
        var matchingPatterns = (patterns?.Entries ?? Array.Empty<BuilderExecutionPatternRecord>())
            .Where(pattern => MatchesWorkspaceSet(pattern.WorkspaceSequence, currentSequence))
            .ToArray();
        var historicalOrdering = matchingPatterns
            .GroupBy(pattern => string.Join("|", pattern.WorkspaceSequence), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().WorkspaceSequence.ToArray())
            .FirstOrDefault()
            ?? currentSequence;
        var recommendedSequence = historicalOrdering;
        var warnings = BuildOrchestrationWarnings(repoRoot, currentSequence, historicalOrdering);
        if (!currentSequence.SequenceEqual(historicalOrdering, StringComparer.OrdinalIgnoreCase) && matchingPatterns.Length > 0)
        {
            warnings.Insert(0, $"Historical finalized runs prefer {string.Join(" -> ", historicalOrdering)} over {string.Join(" -> ", currentSequence)}.");
        }

        return new BuilderOrchestrationRecommendationsRecord(
            requestId,
            currentSequence,
            recommendedSequence,
            historicalOrdering,
            warnings.ToArray(),
            $"Recommended orchestration sequence: {string.Join(" -> ", recommendedSequence)}. Historical warnings: {warnings.Count}. Guidance is advisory only.",
            OrchestrationRecommendationsPathForRepo(repoRoot),
            observedUtc);
    }

    private static List<string> BuildOrchestrationWarnings(
        string repoRoot,
        IReadOnlyList<string> currentSequence,
        IReadOnlyList<string> historicalOrdering)
    {
        var warnings = new List<string>();
        var graph = BuilderKnowledgeGraphService.LoadKnowledgeGraph(repoRoot);
        if (graph?.Entries is null)
        {
            return warnings;
        }

        for (var index = 0; index < currentSequence.Count - 1; index++)
        {
            var sourceWorkspace = currentSequence[index];
            var targetWorkspace = currentSequence[index + 1];
            var pairEntries = graph.Entries
                .Where(entry =>
                    string.Equals(entry.NodeType, "workspace", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.RelationshipType, "depends_on_workspace", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.SourceWorkspace, sourceWorkspace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.TargetWorkspace, targetWorkspace, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var successCount = pairEntries.Count(entry => string.Equals(entry.OutcomeStatus, "finalized", StringComparison.OrdinalIgnoreCase));
            var failureCount = pairEntries.Count(entry =>
                string.Equals(entry.OutcomeStatus, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.OutcomeStatus, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase));
            if (failureCount > 0 && failureCount >= successCount)
            {
                warnings.Add($"{sourceWorkspace} -> {targetWorkspace} has {failureCount} blocked historical outcome(s) and {successCount} finalized outcome(s).");
            }
        }

        if (!currentSequence.SequenceEqual(historicalOrdering, StringComparer.OrdinalIgnoreCase) && warnings.Count == 0)
        {
            warnings.Add("Current orchestration order differs from the strongest historical finalized ordering.");
        }

        return warnings
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesWorkspaceSet(IReadOnlyList<string> candidate, IReadOnlyList<string> target)
        => candidate.Count == target.Count &&
           candidate.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(target.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseTaggedMap(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = part.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == part.Length - 1)
            {
                continue;
            }

            var key = part[..separatorIndex].Trim();
            var mappedValue = part[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(mappedValue))
            {
                map[key] = mappedValue;
            }
        }

        return map;
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

    private sealed record RouteObservation(
        string WorkspaceId,
        string Route,
        string ModelTier,
        bool IsSuccess,
        string OutcomeState,
        string Detail,
        DateTimeOffset ObservedUtc);
}
