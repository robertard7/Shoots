using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Ui.Abstractions;

/// <summary>
/// UI-facing execution surface.
/// This abstraction must not depend on Shoots.Runtime.* assemblies.
/// </summary>
public interface IHostExecutionService
{
    WorkOrder CreateWorkOrder(
        string originalRequest,
        string intent,
        IReadOnlyList<string> constraints,
        IReadOnlyList<string> requestedArtifacts);

    BuildPlan PreviewPlan(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts);

    Task<HostExecutionResult> RunAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default);

    Task<HostExecutionResult> ResumeAsync(
        BuildPlan plan,
        DecisionInjectionRequest request,
        HostResumeIntent intent,
        CancellationToken ct = default);

    ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan);
}

/// <summary>
/// UI-safe decision injection request.
/// NOTE: This is intentionally UI-side and must be translated by the Host/Runtime layer.
/// </summary>
public sealed record DecisionInjectionRequest(
    string? RouteGateId = null,
    string? NodeId = null,
    string? SelectedToolId = null,
    string? BindingsJson = null);

/// <summary>
/// UI-safe resume intent.
/// </summary>
public enum HostResumeIntent
{
    Unknown = 0,
    UseFallback = 1,
    UseSelection = 2
}

/// <summary>
/// UI-safe execution result contract.
/// </summary>
public sealed record HostExecutionResult(
    HostExecutionOutcome Outcome,
    string? WorkOrderId = null,
    string? PlanId = null,
    string? PlanHash = null,
    string? Message = null,
    string? ErrorCode = null);

public enum HostExecutionOutcome
{
    Unknown = 0,
    Started = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4,
    Waiting = 5
}

/// <summary>
/// UI-safe execution options contract.
/// </summary>
public sealed record HostRunOptions(
    HostRunMode Mode = HostRunMode.Normal,
    int? MaxTicks = null,
    bool RecordTrace = true,
    bool Deterministic = true);

public enum HostRunMode
{
    Normal = 0,
    Replay = 1
}