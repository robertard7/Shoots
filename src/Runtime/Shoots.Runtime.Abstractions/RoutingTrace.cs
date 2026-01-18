using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Immutable log of routing events with logical timestamps.
/// </summary>
public sealed record RoutingTrace(
    BuildPlan Plan,
    string CatalogHash,
    IReadOnlyList<RoutingTraceEntry> Entries
);

public sealed record RoutingTraceEntry(
    int Tick,
    RoutingTraceEventKind Event,
    string? Detail = null,
    RoutingState? State = null,
    RouteStep? Step = null,
    RuntimeError? Error = null
);

public enum RoutingTraceEventKind
{
    Plan,
    Command,
    Result,
    Error,
    Route,
    WorkOrderReceived,
    RouteEntered,
    DecisionRequired,
    DecisionAccepted,
    DecisionRejected,
    ToolExecuted,
    ToolResult,
    Halted,
    Completed
}
