using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
namespace Shoots.Providers.Fake;

public sealed class FakeAiProviderAdapter : IAiProviderAdapter
{
    public RouteDecision? RequestDecision(
        WorkOrder workOrder,
        string currentNodeId,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        ToolCatalogSnapshot catalog)
    {
        ToolSelectionDecision? toolSelection = null;

        if (nodeKind is MermaidNodeKind.Tool or MermaidNodeKind.Start)
        {
            var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            toolSelection = new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
            return new RouteDecision(null, toolSelection);
        }

        if (allowedNextNodes.Count == 0)
            return null;

        return new RouteDecision(allowedNextNodes[0], null);
    }
}
