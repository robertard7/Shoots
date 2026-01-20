using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public interface IAiProviderAdapter
{
    /// <summary>
    /// Request a deterministic tool selection. Providers do not control routing and returning
    /// a tool does not advance the graph.
    /// </summary>
    /// <remarks>
    /// Implementations must validate inputs and outputs and throw on contract violations.
    /// </remarks>
    ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        ToolCatalogSnapshot catalog);
}
