using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions;

namespace Shoots.Providers.Fake;

public sealed class FakeAiProviderAdapter : IAiProviderAdapter
{
    public RouteDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        RouteIntentToken intentToken,
        string catalogHash,
        string routingTraceSummary)
    {
        var nextNodeId = allowedNextNodes.Count == 0
            ? string.Empty
            : allowedNextNodes[0];
        ToolSelectionDecision? toolSelection = null;

        if (step.Intent == RouteIntent.SelectTool)
        {
            var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            toolSelection = new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
        }

        var decisionToken = RouteIntentTokenFactory.Create(workOrder, step);
        return new RouteDecision(nextNodeId, decisionToken, step.Intent, toolSelection);
    }
}
