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
    string? FromNodeId = null,
    string? ToNodeId = null,
    RoutingDecisionSource? DecisionSource = null,
    RoutingState? State = null,
    RouteStep? Step = null,
    RuntimeError? Error = null
);

public enum RoutingDecisionSource
{
    Mermaid,
    Provider,
    Rule
}

public enum RoutingTraceEventKind
{
    Plan,
    Command,
    Result,
    Error,
    Route,
    WorkOrderReceived,
    RouteEntered,
    NodeEntered,
    DecisionRequired,
    DecisionAccepted,
    NodeTransitionChosen,
    NodeAdvanced,
    NodeHalted,
    DecisionRejected,
    ToolExecuted,
    ToolResult,
    Halted,
    Completed
}
