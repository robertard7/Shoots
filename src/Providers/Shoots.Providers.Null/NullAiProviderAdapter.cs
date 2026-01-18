using System;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Null;

public sealed class NullAiProviderAdapter : IAiProviderAdapter
{
    public static readonly NullAiProviderAdapter Instance = new();

    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        string catalogHash,
        string routingTraceSummary)
    {
        throw new InvalidOperationException("Null provider invoked");
    }
}
