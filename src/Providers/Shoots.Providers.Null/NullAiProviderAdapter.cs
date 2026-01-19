using System;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Null;

public sealed class NullAiProviderAdapter : IAiProviderAdapter
{
    public static readonly NullAiProviderAdapter Instance = new();

    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        ToolCatalogSnapshot catalog)
    {
        throw new InvalidOperationException("Null provider invoked");
    }
}
