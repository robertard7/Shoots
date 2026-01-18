using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Read-only context for AI decision requests.
/// </summary>
public sealed record AiDecisionRequestContext(
    WorkOrder WorkOrder,
    RouteStep Step,
    RoutingState State,
    ExecutionEnvelope Envelope,
    IReadOnlyList<ToolRegistryEntry> ToolSnapshot
);
