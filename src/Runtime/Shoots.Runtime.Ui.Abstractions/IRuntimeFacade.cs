using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Ui.Abstractions;

/// <summary>
/// UI-facing runtime surface.
/// This project must not depend on runtime-internal types.
/// </summary>
public interface IRuntimeFacade
{
    Task<RuntimeExecutionResult> StartExecutionAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default);

    Task<RuntimeStatusSnapshot> QueryStatusAsync(CancellationToken ct = default);

    IAsyncEnumerable<RoutingTraceEntry> SubscribeTraceAsync(CancellationToken ct = default);

    Task CancelExecutionAsync(CancellationToken ct = default);
}

/// <summary>
/// UI-safe execution result contract.
/// </summary>
public sealed record RuntimeExecutionResult(
    RuntimeExecutionOutcome Outcome,
    string? WorkOrderId = null,
    string? PlanId = null,
    string? PlanHash = null,
    string? Message = null,
    string? ErrorCode = null);

public enum RuntimeExecutionOutcome
{
    Unknown = 0,
    Started = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4,
    Waiting = 5
}

/// <summary>
/// UI-safe status snapshot.
/// </summary>
public sealed record RuntimeStatusSnapshot(
    RuntimeVersionInfo Version,
    string PolicyHash,
    string? StateLabel = null);

/// <summary>
/// UI-safe runtime version info (do not bind to runtime assembly types).
/// </summary>
public sealed record RuntimeVersionInfo(
    int Major,
    int Minor,
    int Patch,
    string? Label = null);

/// <summary>
/// UI-safe trace entry contract.
/// </summary>
public sealed record RoutingTraceEntry(
    long Tick,
    string Event,
    string Detail);