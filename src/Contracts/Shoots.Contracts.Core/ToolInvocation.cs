namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic tool invocation details.
/// </summary>
public sealed record ToolInvocation(
    ToolId ToolId,
    IReadOnlyDictionary<string, object?> Bindings,
    WorkOrderId WorkOrderId
);
