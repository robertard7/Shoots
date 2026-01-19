using System.Collections.Generic;

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
/// Deterministic Mermaid node kind.
/// </summary>
public enum MermaidNodeKind
{
    Start,
    Gate,
    Linear,
    Terminate
}

/// <summary>
/// Deterministic routing rule.
/// </summary>
public sealed record RouteRule(
    string NodeId,
    RouteIntent Intent,
    DecisionOwner Owner,
    string AllowedOutputKind,
    MermaidNodeKind NodeKind,
    IReadOnlyList<string> AllowedNextNodes
);
