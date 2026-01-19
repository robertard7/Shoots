using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public interface IAiProviderAdapter
{
    ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        ToolCatalogSnapshot catalog);
}
