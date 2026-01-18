using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Tool registry entry (contracts only).
/// </summary>
public sealed record ToolRegistryEntry(
    ToolSpec Spec
);

/// <summary>
/// Registry of known tools (metadata only).
/// </summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolRegistryEntry> GetAllTools();

    ToolRegistryEntry? GetTool(ToolId toolId);

    IReadOnlyList<ToolRegistryEntry> GetSnapshot();
}
