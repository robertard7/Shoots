namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable request for planning.
/// </summary>
public sealed record BuildRequest(
    WorkOrder WorkOrder,
    string CommandId,
    IReadOnlyDictionary<string, object?> Args,
    IReadOnlyList<RouteRule> RouteRules
);
