using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderKnowledgeGraphEntryRecord(
    string NodeId,
    string NodeType,
    string RelationshipType,
    string SourceWorkspace,
    string TargetWorkspace,
    string RouteUsed,
    string ModelTierUsed,
    string OutcomeStatus,
    DateTimeOffset Timestamp);

public sealed record BuilderKnowledgeGraphRecord(
    int RetentionCount,
    IReadOnlyList<BuilderKnowledgeGraphEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExecutionPatternRecord(
    string PatternId,
    IReadOnlyList<string> WorkspaceSequence,
    string OrchestrationRoute,
    string FileChangePattern,
    string ModelTier,
    string ReviewOutcome,
    string FinalizeResult,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExecutionPatternsRecord(
    int RetentionCount,
    IReadOnlyList<BuilderExecutionPatternRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFailurePatternRecord(
    string FailureId,
    string Workspace,
    string RouteAttempted,
    string ModelTier,
    string FailureReason,
    string RejectionState,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFailurePatternsRecord(
    int RetentionCount,
    IReadOnlyList<BuilderFailurePatternRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderKnowledgeDependencyRecord(
    string SourceWorkspace,
    string TargetWorkspace,
    int Occurrences,
    string LatestOutcomeStatus,
    string LatestRouteUsed)
{
    public string Summary => $"Occurrences: {Occurrences}. {SourceWorkspace} -> {TargetWorkspace}. Latest outcome: {FormatState(LatestOutcomeStatus)}. Latest route: {LatestRouteUsed}.";

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderKnowledgePatternInsightRecord(
    string WorkspaceSequence,
    string OrchestrationRoute,
    string ModelTier,
    int Occurrences,
    string LatestReviewOutcome,
    string LatestFinalizeResult)
{
    public string Summary => $"Occurrences: {Occurrences}. Workspaces: {WorkspaceSequence}. Route: {OrchestrationRoute}. Model tier: {ModelTier}. Review: {FormatState(LatestReviewOutcome)}. Finalize: {FormatState(LatestFinalizeResult)}.";

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderKnowledgeRouteInsightRecord(
    string RouteUsed,
    string ModelTierUsed,
    int Occurrences,
    string LatestOutcomeStatus,
    string Detail)
{
    public string Summary => $"Occurrences: {Occurrences}. Route: {RouteUsed}. Model tier: {ModelTierUsed}. Latest outcome: {FormatState(LatestOutcomeStatus)}. {Detail}";

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderKnowledgeGraphContext(
    BuilderKnowledgeGraphRecord KnowledgeGraph,
    BuilderExecutionPatternsRecord ExecutionPatterns,
    BuilderFailurePatternsRecord FailurePatterns,
    IReadOnlyList<BuilderKnowledgeDependencyRecord> WorkspaceDependencies,
    IReadOnlyList<BuilderKnowledgePatternInsightRecord> CommonPatterns,
    IReadOnlyList<BuilderKnowledgeRouteInsightRecord> PriorSuccessfulRoutes,
    IReadOnlyList<BuilderKnowledgeRouteInsightRecord> KnownFailureRoutes);

public static class BuilderKnowledgeGraphService
{
    public const string BuilderKnowledgeGraphFileName = "builder_knowledge_graph.json";
    public const string BuilderExecutionPatternsFileName = "builder_execution_patterns.json";
    public const string BuilderFailurePatternsFileName = "builder_failure_patterns.json";

    private const int DefaultGraphRetentionCount = 256;
    private const int DefaultPatternRetentionCount = 64;
    private const int DefaultFailureRetentionCount = 64;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string KnowledgeGraphPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), BuilderKnowledgeGraphFileName);

    public static string ExecutionPatternsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), BuilderExecutionPatternsFileName);

    public static string FailurePatternsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), BuilderFailurePatternsFileName);

    public static BuilderKnowledgeGraphRecord? LoadKnowledgeGraph(string repoRoot)
        => Load<BuilderKnowledgeGraphRecord>(KnowledgeGraphPathForRepo(repoRoot));

    public static BuilderExecutionPatternsRecord? LoadExecutionPatterns(string repoRoot)
        => Load<BuilderExecutionPatternsRecord>(ExecutionPatternsPathForRepo(repoRoot));

    public static BuilderFailurePatternsRecord? LoadFailurePatterns(string repoRoot)
        => Load<BuilderFailurePatternsRecord>(FailurePatternsPathForRepo(repoRoot));

    public static BuilderKnowledgeGraphContext? RefreshKnowledgeArtifacts(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        BuilderCrossRepoOrchestrationContext orchestration,
        string activeWorkspaceId,
        DateTimeOffset? observedUtc = null,
        int graphRetentionCount = DefaultGraphRetentionCount,
        int patternRetentionCount = DefaultPatternRetentionCount,
        int failureRetentionCount = DefaultFailureRetentionCount)
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
        var activeDescriptor = descriptors.FirstOrDefault(descriptor =>
                                   string.Equals(descriptor.WorkspaceId, activeWorkspaceId, StringComparison.OrdinalIgnoreCase))
                               ?? descriptors[0];
        var descriptorsByWorkspaceId = descriptors.ToDictionary(descriptor => descriptor.WorkspaceId, StringComparer.OrdinalIgnoreCase);

        var mergedGraphEntries = MergeGraphEntries(
            descriptors.SelectMany(descriptor => LoadKnowledgeGraph(descriptor.RepoRootPath)?.Entries ?? Array.Empty<BuilderKnowledgeGraphEntryRecord>()),
            BuildGraphEntries(orchestration, descriptorsByWorkspaceId, effectiveObservedUtc),
            graphRetentionCount);
        var graph = new BuilderKnowledgeGraphRecord(
            NormalizeRetentionCount(graphRetentionCount, DefaultGraphRetentionCount),
            mergedGraphEntries,
            $"Builder knowledge graph recorded {mergedGraphEntries.Count} relationship(s) across {descriptors.Length} workspace(s).",
            KnowledgeGraphPathForRepo(activeDescriptor.RepoRootPath),
            effectiveObservedUtc);

        var mergedPatterns = MergePatternEntries(
            descriptors.SelectMany(descriptor => LoadExecutionPatterns(descriptor.RepoRootPath)?.Entries ?? Array.Empty<BuilderExecutionPatternRecord>()),
            BuildExecutionPatternEntries(orchestration, descriptorsByWorkspaceId, effectiveObservedUtc),
            patternRetentionCount);
        var patterns = new BuilderExecutionPatternsRecord(
            NormalizeRetentionCount(patternRetentionCount, DefaultPatternRetentionCount),
            mergedPatterns,
            $"Builder execution memory retains {mergedPatterns.Count} finalized orchestration pattern(s).",
            ExecutionPatternsPathForRepo(activeDescriptor.RepoRootPath),
            effectiveObservedUtc);

        var mergedFailures = MergeFailureEntries(
            descriptors.SelectMany(descriptor => LoadFailurePatterns(descriptor.RepoRootPath)?.Entries ?? Array.Empty<BuilderFailurePatternRecord>()),
            BuildFailurePatternEntries(orchestration, descriptorsByWorkspaceId, effectiveObservedUtc),
            failureRetentionCount);
        var failures = new BuilderFailurePatternsRecord(
            NormalizeRetentionCount(failureRetentionCount, DefaultFailureRetentionCount),
            mergedFailures,
            $"Builder failure memory retains {mergedFailures.Count} deterministic failure pattern(s).",
            FailurePatternsPathForRepo(activeDescriptor.RepoRootPath),
            effectiveObservedUtc);

        foreach (var descriptor in descriptors)
        {
            Directory.CreateDirectory(BuilderWorkspaceService.WorkspaceRootForRepo(descriptor.RepoRootPath));
            Save(KnowledgeGraphPathForRepo(descriptor.RepoRootPath), graph with { ArtifactPath = KnowledgeGraphPathForRepo(descriptor.RepoRootPath) });
            Save(ExecutionPatternsPathForRepo(descriptor.RepoRootPath), patterns with { ArtifactPath = ExecutionPatternsPathForRepo(descriptor.RepoRootPath) });
            Save(FailurePatternsPathForRepo(descriptor.RepoRootPath), failures with { ArtifactPath = FailurePatternsPathForRepo(descriptor.RepoRootPath) });
        }

        return new BuilderKnowledgeGraphContext(
            graph with { ArtifactPath = KnowledgeGraphPathForRepo(activeDescriptor.RepoRootPath) },
            patterns with { ArtifactPath = ExecutionPatternsPathForRepo(activeDescriptor.RepoRootPath) },
            failures with { ArtifactPath = FailurePatternsPathForRepo(activeDescriptor.RepoRootPath) },
            QueryKnownWorkspaceDependencies(activeDescriptor.RepoRootPath),
            QueryCommonOrchestrationPatterns(activeDescriptor.RepoRootPath),
            QueryPriorSuccessfulRoutes(activeDescriptor.RepoRootPath),
            QueryKnownFailureRoutes(activeDescriptor.RepoRootPath));
    }

    public static IReadOnlyList<BuilderKnowledgeDependencyRecord> QueryKnownWorkspaceDependencies(string repoRoot, int maxCount = 6)
    {
        var graph = LoadKnowledgeGraph(repoRoot);
        if (graph?.Entries is null)
        {
            return Array.Empty<BuilderKnowledgeDependencyRecord>();
        }

        return graph.Entries
            .Where(entry =>
                string.Equals(entry.NodeType, "workspace", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.RelationshipType, "depends_on_workspace", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.SourceWorkspace) &&
                !string.IsNullOrWhiteSpace(entry.TargetWorkspace))
            .GroupBy(entry => $"{entry.SourceWorkspace}|{entry.TargetWorkspace}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(entry => entry.Timestamp)
                    .ThenBy(entry => entry.RouteUsed, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new BuilderKnowledgeDependencyRecord(
                    latest.SourceWorkspace,
                    latest.TargetWorkspace,
                    group.Count(),
                    latest.OutcomeStatus,
                    latest.RouteUsed);
            })
            .OrderByDescending(entry => entry.Occurrences)
            .ThenBy(entry => entry.SourceWorkspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetWorkspace, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxCount, 0))
            .ToArray();
    }

    public static IReadOnlyList<BuilderKnowledgePatternInsightRecord> QueryCommonOrchestrationPatterns(string repoRoot, int maxCount = 6)
    {
        var patterns = LoadExecutionPatterns(repoRoot);
        if (patterns?.Entries is null)
        {
            return Array.Empty<BuilderKnowledgePatternInsightRecord>();
        }

        return patterns.Entries
            .GroupBy(BuildPatternInsightKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(entry => entry.ObservedUtc)
                    .ThenBy(entry => entry.PatternId, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new BuilderKnowledgePatternInsightRecord(
                    string.Join(" -> ", latest.WorkspaceSequence),
                    latest.OrchestrationRoute,
                    latest.ModelTier,
                    group.Count(),
                    latest.ReviewOutcome,
                    latest.FinalizeResult);
            })
            .OrderByDescending(entry => entry.Occurrences)
            .ThenBy(entry => entry.WorkspaceSequence, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.OrchestrationRoute, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxCount, 0))
            .ToArray();
    }

    public static IReadOnlyList<BuilderKnowledgeRouteInsightRecord> QueryPriorSuccessfulRoutes(string repoRoot, int maxCount = 6)
    {
        var patterns = LoadExecutionPatterns(repoRoot);
        if (patterns?.Entries is null)
        {
            return Array.Empty<BuilderKnowledgeRouteInsightRecord>();
        }

        return patterns.Entries
            .Where(entry => string.Equals(entry.FinalizeResult, "finalized", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => $"{entry.OrchestrationRoute}|{entry.ModelTier}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(entry => entry.ObservedUtc)
                    .ThenBy(entry => entry.PatternId, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new BuilderKnowledgeRouteInsightRecord(
                    latest.OrchestrationRoute,
                    latest.ModelTier,
                    group.Count(),
                    latest.FinalizeResult,
                    $"Latest workspace sequence: {string.Join(" -> ", latest.WorkspaceSequence)}.");
            })
            .OrderByDescending(entry => entry.Occurrences)
            .ThenBy(entry => entry.RouteUsed, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ModelTierUsed, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxCount, 0))
            .ToArray();
    }

    public static IReadOnlyList<BuilderKnowledgeRouteInsightRecord> QueryKnownFailureRoutes(string repoRoot, int maxCount = 6)
    {
        var failures = LoadFailurePatterns(repoRoot);
        if (failures?.Entries is null)
        {
            return Array.Empty<BuilderKnowledgeRouteInsightRecord>();
        }

        return failures.Entries
            .GroupBy(entry => $"{entry.RouteAttempted}|{entry.ModelTier}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(entry => entry.ObservedUtc)
                    .ThenBy(entry => entry.FailureId, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new BuilderKnowledgeRouteInsightRecord(
                    latest.RouteAttempted,
                    latest.ModelTier,
                    group.Count(),
                    latest.RejectionState,
                    $"Latest failure: {latest.FailureReason}");
            })
            .OrderByDescending(entry => entry.Occurrences)
            .ThenBy(entry => entry.RouteUsed, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ModelTierUsed, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maxCount, 0))
            .ToArray();
    }

    private static IReadOnlyList<BuilderKnowledgeGraphEntryRecord> BuildGraphEntries(
        BuilderCrossRepoOrchestrationContext orchestration,
        IReadOnlyDictionary<string, BuilderWorkspaceDescriptor> descriptorsByWorkspaceId,
        DateTimeOffset observedUtc)
    {
        var entries = new List<BuilderKnowledgeGraphEntryRecord>();
        var statuses = orchestration.ExecutionState.WorkspaceStatusList
            .OrderBy(status => status.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(status => status.RepoRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var status in statuses)
        {
            if (!descriptorsByWorkspaceId.TryGetValue(status.WorkspaceId, out var descriptor))
            {
                continue;
            }

            var outcomeStatus = ResolveOutcomeStatus(status);
            entries.Add(new BuilderKnowledgeGraphEntryRecord(
                descriptor.RepoRootPath,
                "repository",
                "participates_in_orchestration",
                status.WorkspaceId,
                status.WorkspaceId,
                status.RouteDecision,
                status.ModelTier,
                outcomeStatus,
                observedUtc));
            entries.Add(new BuilderKnowledgeGraphEntryRecord(
                status.RouteDecision,
                "orchestration_pattern",
                "uses_route",
                status.WorkspaceId,
                status.WorkspaceId,
                status.RouteDecision,
                status.ModelTier,
                outcomeStatus,
                observedUtc));

            var capabilities = BuilderWorkspaceService.LoadCapabilities(descriptor.RepoRootPath);
            var buildSystems = capabilities?.BuildSystems?
                                   .Where(value => !string.IsNullOrWhiteSpace(value))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                   .ToArray()
                               ?? Array.Empty<string>();
            foreach (var buildSystem in buildSystems)
            {
                entries.Add(new BuilderKnowledgeGraphEntryRecord(
                    buildSystem,
                    "build_system",
                    "uses_build_system",
                    status.WorkspaceId,
                    status.WorkspaceId,
                    status.RouteDecision,
                    status.ModelTier,
                    outcomeStatus,
                    observedUtc));
            }
        }

        foreach (var segment in orchestration.Segments.Segments
                     .OrderBy(segment => segment.RepoName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(segment => segment.WorkspaceId, StringComparer.OrdinalIgnoreCase))
        {
            var status = statuses.FirstOrDefault(entry => string.Equals(entry.WorkspaceId, segment.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            var outcomeStatus = status is null ? "not_recorded" : ResolveOutcomeStatus(status);
            foreach (var path in segment.FilesAffected
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new BuilderKnowledgeGraphEntryRecord(
                    path,
                    "file",
                    "touches_file",
                    segment.WorkspaceId,
                    segment.WorkspaceId,
                    segment.RouteDecision,
                    segment.ModelTier,
                    outcomeStatus,
                    observedUtc));
            }
        }

        var workspaceSequence = orchestration.Plan.ParticipatingWorkspaceIds
            .Where(workspaceId => !string.IsNullOrWhiteSpace(workspaceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < workspaceSequence.Length - 1; index++)
        {
            var sourceWorkspace = workspaceSequence[index];
            var targetWorkspace = workspaceSequence[index + 1];
            var sourceStatus = statuses.FirstOrDefault(status => string.Equals(status.WorkspaceId, sourceWorkspace, StringComparison.OrdinalIgnoreCase));
            var routeUsed = sourceStatus?.RouteDecision
                            ?? orchestration.Plan.RoutingPolicyDecisions.FirstOrDefault(decision =>
                                string.Equals(decision.WorkspaceId, sourceWorkspace, StringComparison.OrdinalIgnoreCase))?.RouteDecision
                            ?? "workspace_route_not_recorded";
            var modelTier = sourceStatus?.ModelTier
                            ?? orchestration.Plan.RoutingPolicyDecisions.FirstOrDefault(decision =>
                                string.Equals(decision.WorkspaceId, sourceWorkspace, StringComparison.OrdinalIgnoreCase))?.ModelTier
                            ?? "not_recorded";
            var outcomeStatus = sourceStatus is null ? "not_recorded" : ResolveOutcomeStatus(sourceStatus);

            entries.Add(new BuilderKnowledgeGraphEntryRecord(
                $"{sourceWorkspace}->{targetWorkspace}",
                "workspace",
                "depends_on_workspace",
                sourceWorkspace,
                targetWorkspace,
                routeUsed,
                modelTier,
                outcomeStatus,
                observedUtc));
        }

        return entries;
    }

    private static IReadOnlyList<BuilderExecutionPatternRecord> BuildExecutionPatternEntries(
        BuilderCrossRepoOrchestrationContext orchestration,
        IReadOnlyDictionary<string, BuilderWorkspaceDescriptor> descriptorsByWorkspaceId,
        DateTimeOffset observedUtc)
    {
        if (!orchestration.ExecutionState.WorkspaceStatusList.All(status => status.Finalized))
        {
            return Array.Empty<BuilderExecutionPatternRecord>();
        }

        var workspaceSequence = orchestration.Plan.ParticipatingWorkspaceIds
            .Where(workspaceId => !string.IsNullOrWhiteSpace(workspaceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (workspaceSequence.Length == 0)
        {
            return Array.Empty<BuilderExecutionPatternRecord>();
        }

        var routeByWorkspace = orchestration.Plan.RoutingPolicyDecisions
            .OrderBy(decision => IndexOfWorkspace(workspaceSequence, decision.WorkspaceId))
            .ThenBy(decision => decision.RepoName, StringComparer.OrdinalIgnoreCase)
            .Select(decision => $"{decision.WorkspaceId}:{decision.RouteDecision}")
            .ToArray();
        var modelTierByWorkspace = orchestration.ExecutionState.WorkspaceStatusList
            .OrderBy(status => IndexOfWorkspace(workspaceSequence, status.WorkspaceId))
            .ThenBy(status => status.RepoName, StringComparer.OrdinalIgnoreCase)
            .Select(status => $"{status.WorkspaceId}:{status.ModelTier}")
            .ToArray();
        var fileChangePattern = workspaceSequence
            .Select(workspaceId => descriptorsByWorkspaceId.TryGetValue(workspaceId, out var descriptor)
                ? $"{workspaceId}[{BuildFileChangePattern(descriptor.RepoRootPath)}]"
                : $"{workspaceId}[not_recorded]")
            .ToArray();
        var reviewOutcome = orchestration.ExecutionState.WorkspaceStatusList.All(status =>
            string.Equals(status.ReviewState, "approved", StringComparison.OrdinalIgnoreCase))
            ? "approved"
            : "finalized";
        const string finalizeResult = "finalized";
        var orchestrationRoute = string.Join(" | ", routeByWorkspace);
        var modelTier = string.Join(" | ", modelTierByWorkspace);

        return new[]
        {
            new BuilderExecutionPatternRecord(
                BuildPatternId(workspaceSequence, orchestrationRoute, fileChangePattern, modelTier, reviewOutcome, finalizeResult),
                workspaceSequence,
                orchestrationRoute,
                string.Join(" | ", fileChangePattern),
                modelTier,
                reviewOutcome,
                finalizeResult,
                observedUtc)
        };
    }

    private static IReadOnlyList<BuilderFailurePatternRecord> BuildFailurePatternEntries(
        BuilderCrossRepoOrchestrationContext orchestration,
        IReadOnlyDictionary<string, BuilderWorkspaceDescriptor> descriptorsByWorkspaceId,
        DateTimeOffset observedUtc)
    {
        var entries = new List<BuilderFailurePatternRecord>();
        foreach (var status in orchestration.ExecutionState.WorkspaceStatusList
                     .Where(status =>
                         status.RejectedSegment ||
                         string.Equals(status.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(status => status.RepoName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(status => status.RepoRoot, StringComparer.OrdinalIgnoreCase))
        {
            var failureReason = status.Summary;
            if (descriptorsByWorkspaceId.TryGetValue(status.WorkspaceId, out var descriptor))
            {
                var reviewArtifacts = BuilderReviewWorkspaceService.LoadArtifacts(descriptor.RepoRootPath);
                failureReason = reviewArtifacts.PatchApplyDecision?.BlockReasons.FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
                                ?? FirstNonEmpty(reviewArtifacts.PatchReviewOutcome?.ReviewNote, status.Summary);
            }

            var rejectionState = FirstNonEmpty(status.FinalizeReadiness, status.ReviewState);
            entries.Add(new BuilderFailurePatternRecord(
                BuildFailureId(status.WorkspaceId, status.RouteDecision, status.ModelTier, failureReason, rejectionState),
                status.WorkspaceId,
                status.RouteDecision,
                status.ModelTier,
                failureReason,
                rejectionState,
                observedUtc));
        }

        return entries;
    }

    private static IReadOnlyList<BuilderKnowledgeGraphEntryRecord> MergeGraphEntries(
        IEnumerable<BuilderKnowledgeGraphEntryRecord> existingEntries,
        IEnumerable<BuilderKnowledgeGraphEntryRecord> newEntries,
        int retentionCount)
        => existingEntries
            .Concat(newEntries)
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.NodeId))
            .GroupBy(BuildGraphIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.Timestamp)
                .ThenBy(entry => entry.NodeType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.NodeId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.SourceWorkspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TargetWorkspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.NodeType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.NodeId, StringComparer.OrdinalIgnoreCase)
            .Take(NormalizeRetentionCount(retentionCount, DefaultGraphRetentionCount))
            .ToArray();

    private static IReadOnlyList<BuilderExecutionPatternRecord> MergePatternEntries(
        IEnumerable<BuilderExecutionPatternRecord> existingEntries,
        IEnumerable<BuilderExecutionPatternRecord> newEntries,
        int retentionCount)
        => existingEntries
            .Concat(newEntries)
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.PatternId))
            .GroupBy(entry => entry.PatternId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.ObservedUtc)
                .ThenBy(entry => entry.PatternId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => string.Join("|", entry.WorkspaceSequence), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.OrchestrationRoute, StringComparer.OrdinalIgnoreCase)
            .Take(NormalizeRetentionCount(retentionCount, DefaultPatternRetentionCount))
            .ToArray();

    private static IReadOnlyList<BuilderFailurePatternRecord> MergeFailureEntries(
        IEnumerable<BuilderFailurePatternRecord> existingEntries,
        IEnumerable<BuilderFailurePatternRecord> newEntries,
        int retentionCount)
        => existingEntries
            .Concat(newEntries)
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.FailureId))
            .GroupBy(entry => entry.FailureId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.ObservedUtc)
                .ThenBy(entry => entry.FailureId, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.Workspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RouteAttempted, StringComparer.OrdinalIgnoreCase)
            .Take(NormalizeRetentionCount(retentionCount, DefaultFailureRetentionCount))
            .ToArray();

    private static string BuildFileChangePattern(string repoRoot)
    {
        var patchReview = BuilderReviewWorkspaceService.LoadArtifacts(repoRoot).PatchReview;
        if (patchReview?.ChangedFiles is null || patchReview.ChangedFiles.Count == 0)
        {
            return "no_changed_files";
        }

        var changeKinds = patchReview.ChangedFiles
            .GroupBy(file => FirstNonEmpty(file.ChangeKind, "modified"), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();
        var extensions = patchReview.ChangedFiles
            .Select(file => Path.GetExtension(file.Path))
            .Select(extension => string.IsNullOrWhiteSpace(extension) ? "[no_extension]" : extension.ToLowerInvariant())
            .GroupBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();

        return $"changes[{string.Join(", ", changeKinds)}]; extensions[{string.Join(", ", extensions)}]";
    }

    private static string ResolveOutcomeStatus(BuilderCrossRepoWorkspaceStatusRecord status)
    {
        if (status.Finalized)
        {
            return "finalized";
        }

        if (string.Equals(status.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase))
        {
            return "blocked_by_rejection";
        }

        if (string.Equals(status.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase))
        {
            return "blocked_by_revision_request";
        }

        return FirstNonEmpty(status.ReviewState, status.ExecutionState);
    }

    private static int IndexOfWorkspace(IReadOnlyList<string> workspaceSequence, string workspaceId)
    {
        for (var index = 0; index < workspaceSequence.Count; index++)
        {
            if (string.Equals(workspaceSequence[index], workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static string BuildPatternInsightKey(BuilderExecutionPatternRecord entry)
        => string.Join("|", entry.WorkspaceSequence) + "|" + entry.OrchestrationRoute + "|" + entry.ModelTier;

    private static string BuildGraphIdentity(BuilderKnowledgeGraphEntryRecord entry)
        => string.Join("|",
            entry.NodeId,
            entry.NodeType,
            entry.RelationshipType,
            entry.SourceWorkspace,
            entry.TargetWorkspace,
            entry.RouteUsed,
            entry.ModelTierUsed,
            entry.OutcomeStatus);

    private static string BuildPatternId(
        IReadOnlyList<string> workspaceSequence,
        string orchestrationRoute,
        IReadOnlyList<string> fileChangePattern,
        string modelTier,
        string reviewOutcome,
        string finalizeResult)
        => BuildDeterministicId(
            string.Join("|", workspaceSequence),
            orchestrationRoute,
            string.Join("|", fileChangePattern),
            modelTier,
            reviewOutcome,
            finalizeResult);

    private static string BuildFailureId(
        string workspaceId,
        string routeAttempted,
        string modelTier,
        string failureReason,
        string rejectionState)
        => BuildDeterministicId(workspaceId, routeAttempted, modelTier, failureReason, rejectionState);

    private static string BuildDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var input = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return hash[..12];
    }

    private static int NormalizeRetentionCount(int requested, int fallback)
        => requested > 0 ? requested : fallback;

    private static string FirstNonEmpty(string? primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

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
