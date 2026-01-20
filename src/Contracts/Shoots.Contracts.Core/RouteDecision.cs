namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic routing decision payload.
/// </summary>
public sealed record RouteDecision(
    string? NextNodeId,
    ToolSelectionDecision? ToolSelection = null
);
