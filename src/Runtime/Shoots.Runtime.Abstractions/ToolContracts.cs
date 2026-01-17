namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Immutable tool identity.
/// </summary>
public readonly record struct ToolId(string Value);

/// <summary>
/// Deterministic tool input specification.
/// </summary>
public sealed record ToolInputSpec(
    string Name,
    string Type,
    bool Required,
    string Description
);

/// <summary>
/// Deterministic tool output specification.
/// </summary>
public sealed record ToolOutputSpec(
    string Name,
    string Type,
    string Description
);

/// <summary>
/// Deterministic tool contract (metadata only).
/// </summary>
public sealed record ToolSpec(
    ToolId ToolId,
    string Description,
    IReadOnlyList<ToolInputSpec> Inputs,
    IReadOnlyList<ToolOutputSpec> Outputs
);

/// <summary>
/// Tool contract boundary (no execution logic).
/// </summary>
public interface ITool
{
    ToolSpec Spec { get; }
}
