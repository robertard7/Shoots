using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
namespace Shoots.Providers.Fake;

public sealed class FakeAiProviderAdapter : IAiProviderAdapter
{
    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        IReadOnlyList<string> allowedNextNodeIds,
        ToolCatalogSnapshot catalog)
    {
        if (routeStep.Intent != RouteIntent.SelectTool)
            return null;

        var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
    }
}
