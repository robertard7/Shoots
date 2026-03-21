using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderCrossRepoPlanStepRecord(
    int SequenceNumber,
    string WorkspaceId,
    string RepoName,
    string StepType,
    string StepLabel,
    string RouteDecision);

public sealed record BuilderCrossRepoRoutingPolicyDecisionRecord(
    string WorkspaceId,
    string RepoName,
    string RouteDecision,
    string ModelTier);

public sealed record BuilderCrossRepoPlanRecord(
    string OrchestrationId,
    string RequestId,
    IReadOnlyList<string> ParticipatingWorkspaceIds,
    IReadOnlyList<string> RepoOrder,
    IReadOnlyList<BuilderCrossRepoPlanStepRecord> StepSequence,
    IReadOnlyList<BuilderCrossRepoRoutingPolicyDecisionRecord> RoutingPolicyDecisions,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceTaskSegmentRecord(
    string OrchestrationId,
    string WorkspaceId,
    string RepoName,
    string TaskDescription,
    IReadOnlyList<string> FilesAffected,
    string RouteDecision,
    string ModelTier,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceTaskSegmentsRecord(
    string OrchestrationId,
    IReadOnlyList<BuilderWorkspaceTaskSegmentRecord> Segments,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderCrossRepoWorkspaceStatusRecord(
    string WorkspaceId,
    string RepoName,
    string RepoRoot,
    string ExecutionState,
    string ReviewState,
    string FinalizeReadiness,
    int PendingReviews,
    bool RejectedSegment,
    bool Finalized,
    int ChangedFiles,
    string RouteDecision,
    string ModelTier,
    string Summary);

public sealed record BuilderCrossRepoExecutionStateRecord(
    string OrchestrationId,
    IReadOnlyList<BuilderCrossRepoWorkspaceStatusRecord> WorkspaceStatusList,
    int PendingReviews,
    IReadOnlyList<string> RejectedSegments,
    string FinalizeReadiness,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderCrossRepoOrchestrationContext(
    BuilderCrossRepoPlanRecord Plan,
    BuilderWorkspaceTaskSegmentsRecord Segments,
    BuilderCrossRepoExecutionStateRecord ExecutionState);

public static class BuilderCrossRepoOrchestrationService
{
    public const string CrossRepoPlanFileName = "builder_cross_repo_plan.json";
    public const string WorkspaceTaskSegmentsFileName = "builder_workspace_task_segments.json";
    public const string CrossRepoExecutionStateFileName = "builder_cross_repo_execution_state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string CrossRepoPlanPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), CrossRepoPlanFileName);

    public static string WorkspaceTaskSegmentsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), WorkspaceTaskSegmentsFileName);

    public static string CrossRepoExecutionStatePathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), CrossRepoExecutionStateFileName);

    public static BuilderCrossRepoPlanRecord? LoadPlan(string repoRoot)
        => Load<BuilderCrossRepoPlanRecord>(CrossRepoPlanPathForRepo(repoRoot));

    public static BuilderWorkspaceTaskSegmentsRecord? LoadSegments(string repoRoot)
        => Load<BuilderWorkspaceTaskSegmentsRecord>(WorkspaceTaskSegmentsPathForRepo(repoRoot));

    public static BuilderCrossRepoExecutionStateRecord? LoadExecutionState(string repoRoot)
        => Load<BuilderCrossRepoExecutionStateRecord>(CrossRepoExecutionStatePathForRepo(repoRoot));

    public static BuilderCrossRepoOrchestrationContext? RefreshOrchestrationArtifacts(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        string activeWorkspaceId,
        string requestId,
        DateTimeOffset? observedUtc = null)
    {
        if (workspaces is null)
        {
            throw new ArgumentNullException(nameof(workspaces));
        }

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
        var orchestrationId = BuildOrchestrationId(requestId, descriptors.Select(descriptor => descriptor.WorkspaceId));
        var snapshots = descriptors
            .Select(descriptor => BuildWorkspaceSnapshot(descriptor))
            .OrderBy(snapshot => RepoPriority(snapshot.ExecutionState, snapshot.FinalizeReadiness))
            .ThenBy(snapshot => snapshot.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.RepoRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var plan = BuildPlan(orchestrationId, requestId, snapshots, activeDescriptor.RepoRootPath, effectiveObservedUtc);
        var segments = BuildSegments(orchestrationId, snapshots, activeDescriptor.RepoRootPath, effectiveObservedUtc);
        var executionState = BuildExecutionState(orchestrationId, snapshots, activeDescriptor.RepoRootPath, effectiveObservedUtc);

        foreach (var snapshot in snapshots)
        {
            Directory.CreateDirectory(BuilderWorkspaceService.WorkspaceRootForRepo(snapshot.RepoRoot));
            Save(CrossRepoPlanPathForRepo(snapshot.RepoRoot), plan with { ArtifactPath = CrossRepoPlanPathForRepo(snapshot.RepoRoot) });
            Save(WorkspaceTaskSegmentsPathForRepo(snapshot.RepoRoot), segments with { ArtifactPath = WorkspaceTaskSegmentsPathForRepo(snapshot.RepoRoot) });
            Save(CrossRepoExecutionStatePathForRepo(snapshot.RepoRoot), executionState with { ArtifactPath = CrossRepoExecutionStatePathForRepo(snapshot.RepoRoot) });
        }

        return new BuilderCrossRepoOrchestrationContext(
            plan with { ArtifactPath = CrossRepoPlanPathForRepo(activeDescriptor.RepoRootPath) },
            segments with { ArtifactPath = WorkspaceTaskSegmentsPathForRepo(activeDescriptor.RepoRootPath) },
            executionState with { ArtifactPath = CrossRepoExecutionStatePathForRepo(activeDescriptor.RepoRootPath) });
    }

    private static BuilderCrossRepoPlanRecord BuildPlan(
        string orchestrationId,
        string requestId,
        IReadOnlyList<WorkspaceSnapshot> snapshots,
        string artifactRepoRoot,
        DateTimeOffset observedUtc)
    {
        var repoOrder = snapshots.Select(snapshot => snapshot.RepoRoot).ToArray();
        var routingDecisions = snapshots
            .Select(snapshot => new BuilderCrossRepoRoutingPolicyDecisionRecord(
                snapshot.WorkspaceId,
                snapshot.RepoName,
                snapshot.RouteDecision,
                snapshot.ModelTier))
            .ToArray();

        var steps = new List<BuilderCrossRepoPlanStepRecord>();
        var sequence = 1;
        foreach (var snapshot in snapshots)
        {
            steps.Add(new BuilderCrossRepoPlanStepRecord(sequence++, snapshot.WorkspaceId, snapshot.RepoName, "segment_workspace", "Segment workspace task", snapshot.RouteDecision));
            steps.Add(new BuilderCrossRepoPlanStepRecord(sequence++, snapshot.WorkspaceId, snapshot.RepoName, "execute_builder_flow", "Execute repo-scoped builder flow", snapshot.RouteDecision));
            steps.Add(new BuilderCrossRepoPlanStepRecord(sequence++, snapshot.WorkspaceId, snapshot.RepoName, "collect_review_queue", "Collect repo review queue", snapshot.RouteDecision));
            steps.Add(new BuilderCrossRepoPlanStepRecord(sequence++, snapshot.WorkspaceId, snapshot.RepoName, "operator_review", "Operator approves repo segment", snapshot.RouteDecision));
            steps.Add(new BuilderCrossRepoPlanStepRecord(sequence++, snapshot.WorkspaceId, snapshot.RepoName, "finalize_workspace", "Finalize repo independently after approval", snapshot.RouteDecision));
        }

        return new BuilderCrossRepoPlanRecord(
            orchestrationId,
            requestId,
            snapshots.Select(snapshot => snapshot.WorkspaceId).ToArray(),
            repoOrder,
            steps,
            routingDecisions,
            $"Cross-repo orchestration spans {snapshots.Count} workspace(s). Execution stays repo-scoped and finalization remains independent per workspace.",
            CrossRepoPlanPathForRepo(artifactRepoRoot),
            observedUtc);
    }

    private static BuilderWorkspaceTaskSegmentsRecord BuildSegments(
        string orchestrationId,
        IReadOnlyList<WorkspaceSnapshot> snapshots,
        string artifactRepoRoot,
        DateTimeOffset observedUtc)
    {
        var segments = snapshots
            .Select(snapshot => new BuilderWorkspaceTaskSegmentRecord(
                orchestrationId,
                snapshot.WorkspaceId,
                snapshot.RepoName,
                BuildTaskDescription(snapshot),
                snapshot.FilesAffected,
                snapshot.RouteDecision,
                snapshot.ModelTier,
                WorkspaceTaskSegmentsPathForRepo(snapshot.RepoRoot),
                observedUtc))
            .ToArray();

        return new BuilderWorkspaceTaskSegmentsRecord(
            orchestrationId,
            segments,
            $"Generated {segments.Length} repo-scoped segment(s) for orchestration {orchestrationId}.",
            WorkspaceTaskSegmentsPathForRepo(artifactRepoRoot),
            observedUtc);
    }

    private static BuilderCrossRepoExecutionStateRecord BuildExecutionState(
        string orchestrationId,
        IReadOnlyList<WorkspaceSnapshot> snapshots,
        string artifactRepoRoot,
        DateTimeOffset observedUtc)
    {
        var workspaceStatuses = snapshots
            .Select(snapshot => new BuilderCrossRepoWorkspaceStatusRecord(
                snapshot.WorkspaceId,
                snapshot.RepoName,
                snapshot.RepoRoot,
                snapshot.ExecutionState,
                snapshot.ReviewState,
                snapshot.FinalizeReadiness,
                snapshot.PendingReviews,
                snapshot.RejectedSegment,
                snapshot.Finalized,
                snapshot.FilesAffected.Count,
                snapshot.RouteDecision,
                snapshot.ModelTier,
                snapshot.StatusSummary))
            .ToArray();

        var pendingReviews = workspaceStatuses.Sum(status => status.PendingReviews);
        var rejectedSegments = workspaceStatuses
            .Where(status => status.RejectedSegment)
            .Select(status => status.WorkspaceId)
            .ToArray();
        var finalizeReadiness = DetermineGlobalFinalizeReadiness(workspaceStatuses);

        return new BuilderCrossRepoExecutionStateRecord(
            orchestrationId,
            workspaceStatuses,
            pendingReviews,
            rejectedSegments,
            finalizeReadiness,
            BuildExecutionSummary(workspaceStatuses, pendingReviews, rejectedSegments, finalizeReadiness),
            CrossRepoExecutionStatePathForRepo(artifactRepoRoot),
            observedUtc);
    }

    private static WorkspaceSnapshot BuildWorkspaceSnapshot(BuilderWorkspaceDescriptor descriptor)
    {
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(descriptor.RepoRootPath);
        var proofRun = BuilderExecutionService.LoadLatestBuilderProofRun(descriptor.RepoRootPath);
        var reviewWorkspace = BuilderReviewWorkspaceService.LoadWorkspace(descriptor.RepoRootPath);
        var reviewArtifacts = BuilderReviewWorkspaceService.LoadArtifacts(descriptor.RepoRootPath);
        var patchApply = reviewArtifacts.PatchApplyDecision;
        var finalizeReadiness = FirstNonEmpty(
            reviewWorkspace?.ReviewCounts.FinalizeEligibilityState,
            patchApply?.ApplyEligibilityState,
            reviewArtifacts.PatchReview is null ? "pending" : "pending_review");
        var filesAffected = (reviewWorkspace?.NavigationOrder
                             ?? reviewArtifacts.PatchReview?.ChangedFiles.Select(file => file.Path).ToArray()
                             ?? Array.Empty<string>())
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pendingReviews = reviewWorkspace?.ReviewCounts.PendingFiles
                             ?? reviewArtifacts.PatchReview?.ChangedFiles.Count
                             ?? 0;
        var reviewState = finalizeReadiness switch
        {
            "blocked_by_rejection" => "rejected",
            "blocked_by_revision_request" => "needs_revision",
            "ready_to_finalize" => "approved",
            "no_changed_files" => "approved",
            _ => reviewArtifacts.PatchReview is null ? "pending" : "pending_review"
        };
        var finalized = string.Equals(patchApply?.FinalizationState, "finalized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchApply?.FinalizationState, "applied", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchApply?.ApplyEligibilityState, "finalized", StringComparison.OrdinalIgnoreCase);
        var executionState = finalized
            ? "finalized"
            : finalizeReadiness switch
            {
                "blocked_by_rejection" => "rejected",
                "blocked_by_revision_request" => "rejected",
                _ when reviewArtifacts.PatchReview is not null || reviewWorkspace is not null => "in_review",
                _ => "pending"
            };
        var rejectedSegment = string.Equals(executionState, "rejected", StringComparison.OrdinalIgnoreCase);
        var routeDecision = routeResolution?.RouteDecision ?? "workspace_isolated_review";
        var modelId = proofRun?.ModelId ?? BuilderExecutionService.BuilderProofFloorModelId;
        var modelTier = ResolveModelTier(routeDecision, modelId);

        return new WorkspaceSnapshot(
            descriptor.WorkspaceId,
            descriptor.RepoName,
            descriptor.RepoRootPath,
            routeDecision,
            modelId,
            modelTier,
            executionState,
            reviewState,
            finalizeReadiness,
            pendingReviews,
            rejectedSegment,
            finalized,
            filesAffected,
            BuildStatusSummary(descriptor.RepoName, executionState, reviewState, finalizeReadiness, pendingReviews, filesAffected.Length));
    }

    private static string BuildTaskDescription(WorkspaceSnapshot snapshot)
        => $"{snapshot.RepoName}: route={snapshot.RouteDecision}; model tier={snapshot.ModelTier}; files affected={snapshot.FilesAffected.Count}; finalize readiness={snapshot.FinalizeReadiness}.";

    private static string BuildStatusSummary(
        string repoName,
        string executionState,
        string reviewState,
        string finalizeReadiness,
        int pendingReviews,
        int changedFiles)
        => $"{repoName}: execution={executionState}; review={reviewState}; finalize={finalizeReadiness}; pending reviews={pendingReviews}; changed files={changedFiles}.";

    private static string BuildExecutionSummary(
        IReadOnlyList<BuilderCrossRepoWorkspaceStatusRecord> workspaceStatuses,
        int pendingReviews,
        IReadOnlyList<string> rejectedSegments,
        string finalizeReadiness)
        => $"Workspaces={workspaceStatuses.Count}. Pending reviews={pendingReviews}. Rejected segments={rejectedSegments.Count}. Finalize readiness={finalizeReadiness}. Cross-repo orchestration never auto-finalizes another workspace.";

    private static string DetermineGlobalFinalizeReadiness(IReadOnlyList<BuilderCrossRepoWorkspaceStatusRecord> statuses)
    {
        if (statuses.Any(status => string.Equals(status.FinalizeReadiness, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase)))
        {
            return "blocked_by_rejection";
        }

        if (statuses.Any(status => string.Equals(status.FinalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase)))
        {
            return "blocked_by_revision_request";
        }

        if (statuses.All(status =>
                string.Equals(status.ExecutionState, "finalized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.FinalizeReadiness, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.FinalizeReadiness, "no_changed_files", StringComparison.OrdinalIgnoreCase)))
        {
            return "ready_for_independent_finalize";
        }

        return "pending_review";
    }

    private static int RepoPriority(string executionState, string finalizeReadiness)
    {
        if (string.Equals(executionState, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(finalizeReadiness, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(executionState, "in_review", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(executionState, "finalized", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return 3;
    }

    private static string ResolveModelTier(string routeDecision, string modelId)
    {
        if (routeDecision.Contains("stronger", StringComparison.OrdinalIgnoreCase))
        {
            return "stronger_tier";
        }

        if (string.Equals(modelId, BuilderExecutionService.BuilderProofFloorModelId, StringComparison.Ordinal))
        {
            return "floor";
        }

        return "current_model";
    }

    private static string BuildOrchestrationId(string requestId, IEnumerable<string> workspaceIds)
    {
        using var sha = SHA256.Create();
        var joined = string.Join("|", new[] { requestId.Trim() }.Concat(workspaceIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return $"cross-repo-{hash[..10]}";
    }

    private static string FirstNonEmpty(string? primary, string? secondary, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary.Trim();
        }

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            return secondary.Trim();
        }

        return fallback;
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

    private sealed record WorkspaceSnapshot(
        string WorkspaceId,
        string RepoName,
        string RepoRoot,
        string RouteDecision,
        string ModelId,
        string ModelTier,
        string ExecutionState,
        string ReviewState,
        string FinalizeReadiness,
        int PendingReviews,
        bool RejectedSegment,
        bool Finalized,
        IReadOnlyList<string> FilesAffected,
        string StatusSummary);
}
