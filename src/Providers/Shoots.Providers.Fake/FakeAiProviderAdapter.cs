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
        ToolCatalogSnapshot catalog)
    {
        ProviderGuards.AgainstNull(workOrder, nameof(workOrder));
        ProviderGuards.AgainstNull(routeStep, nameof(routeStep));
        ProviderGuards.AgainstNullOrWhiteSpace(graphHash, nameof(graphHash));
        ProviderGuards.AgainstNullOrWhiteSpace(catalogHash, nameof(catalogHash));
        ProviderGuards.RequireCatalog(catalog);

        var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return new ToolSelectionDecision(new ToolId("filesystem.read"), bindings);
    }
}
