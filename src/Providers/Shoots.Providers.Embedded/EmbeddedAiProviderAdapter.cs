using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Embedded;

public sealed class EmbeddedAiProviderAdapter : IAiProviderAdapter
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

		var selectedTool = SelectToolId(catalog);
		if (selectedTool is null)
			return null;

		var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		return new ToolSelectionDecision(selectedTool.Value, bindings);
    }

    private static ToolId? SelectToolId(ToolCatalogSnapshot catalog)
    {
        if (catalog.Tools.Count == 0)
            return null;

        var deterministic = catalog.Tools
            .Select(tool => tool.ToolId)
            .OrderBy(toolId => toolId.Value, StringComparer.Ordinal)
            .FirstOrDefault();

        return deterministic;
    }
}
