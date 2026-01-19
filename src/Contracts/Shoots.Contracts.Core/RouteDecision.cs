namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic routing decision payload.
/// </summary>
public sealed record RouteDecision(
    string SuggestedNextNodeId,
    RouteIntentToken IntentToken,
    RouteIntent ObservedIntent,
    ToolSelectionDecision? ToolSelection = null
);
