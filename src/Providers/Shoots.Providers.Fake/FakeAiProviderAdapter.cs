using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Fake;

public sealed class FakeAiProviderAdapter : IAiProviderAdapter
{
    public RouteDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        string catalogHash,
        string routingTraceSummary)
    {
        if (allowedNextNodes.Count == 0)
            return null;

        var nextNodeId = allowedNextNodes[0];
        ToolSelectionDecision? toolSelection = null;

        if (step.Intent == RouteIntent.SelectTool)
        {
            var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            toolSelection = new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
        }

        return new RouteDecision(nextNodeId, toolSelection);
    }
}
