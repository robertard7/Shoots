namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic tool selection decision envelope.
/// </summary>
public sealed record ToolSelectionDecision(
    ToolId ToolId,
    IReadOnlyDictionary<string, object?> Bindings
);
