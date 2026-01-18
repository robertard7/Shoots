namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic routing intent.
/// </summary>
public enum RouteIntent
{
    SelectTool,
    Validate,
    Review,
    Terminate
}

/// <summary>
/// Deterministic decision ownership.
/// </summary>
public enum DecisionOwner
{
    Human,
    Ai,
    Runtime,
    Rule
}

/// <summary>
/// Deterministic routing rule.
/// </summary>
public sealed record RouteRule(
    string NodeId,
    RouteIntent Intent,
    DecisionOwner Owner,
    string AllowedOutputKind
);
