using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public interface IAiProviderAdapter
{
    RouteDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        string catalogHash,
        string routingTraceSummary);
}
