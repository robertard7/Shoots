using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public interface IAiProviderAdapter
{
    RouteDecision? RequestDecision(
        WorkOrder workOrder,
        string currentNodeId,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        ToolCatalogSnapshot catalog);
}
