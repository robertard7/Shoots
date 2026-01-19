using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Read-only context for AI decision requests.
/// </summary>
public sealed record AiDecisionRequest(
    WorkOrder WorkOrder,
    RouteStep Step,
    RoutingState State,
    string CatalogHash,
    IReadOnlyList<RoutingTraceEventKind> TraceSummary,
    MermaidNodeKind NodeKind,
    IReadOnlyList<string> AllowedNextNodes
);
