namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic tool result envelope.
/// </summary>
public sealed record ToolResult(
    ToolId ToolId,
    IReadOnlyDictionary<string, object?> Outputs,
    bool Success
);
