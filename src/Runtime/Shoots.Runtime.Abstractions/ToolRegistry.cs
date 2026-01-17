using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Declarative tool authority requirements.
/// </summary>
public sealed record ToolAuthorityRequirement(
    ProviderKind RequiredProviderKind,
    ProviderCapabilities RequiredCapabilities
);

/// <summary>
/// Tool registry entry (contracts only).
/// </summary>
public sealed record ToolRegistryEntry(
    ToolSpec Spec,
    ToolAuthorityRequirement AuthorityRequirement
);

/// <summary>
/// Registry of known tools (metadata only).
/// </summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolRegistryEntry> GetAllTools();

    ToolRegistryEntry? GetTool(ToolId toolId);
}
