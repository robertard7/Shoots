using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public interface IAiProviderAdapter
{
    ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        string catalogHash,
        string routingTraceSummary);
}
