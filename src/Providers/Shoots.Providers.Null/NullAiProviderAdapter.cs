using System;
using System.Collections.Generic;
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
        IReadOnlyList<string> allowedNextNodeIds,
        ToolCatalogSnapshot catalog)
    {
        throw new InvalidOperationException("Null provider invoked");
    }
}
