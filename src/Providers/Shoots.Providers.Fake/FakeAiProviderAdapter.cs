using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Fake;

public sealed class FakeAiProviderAdapter : IAiProviderAdapter
{
    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        string catalogHash,
        string routingTraceSummary)
    {
        var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
    }
}
