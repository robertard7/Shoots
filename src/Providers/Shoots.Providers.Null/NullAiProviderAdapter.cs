using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Null;

public sealed class NullAiProviderAdapter : IAiProviderAdapter
{
    public static readonly NullAiProviderAdapter Instance = new();

    public RouteDecision? RequestDecision(
        WorkOrder workOrder,
        string currentNodeId,
        MermaidNodeKind nodeKind,
        IReadOnlyList<string> allowedNextNodes,
        ToolCatalogSnapshot catalog)
    {
        throw new InvalidOperationException("Null provider invoked");
    }
}
