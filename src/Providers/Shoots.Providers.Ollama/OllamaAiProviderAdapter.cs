using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Ollama;

public sealed class OllamaAiProviderAdapter : IAiProviderAdapter
{
    private readonly OllamaProviderSettings _settings;

    public OllamaAiProviderAdapter(OllamaProviderSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(_settings.Endpoint))
            throw new ArgumentException("endpoint is required", nameof(settings));
        if (string.IsNullOrWhiteSpace(_settings.Model))
            throw new ArgumentException("model is required", nameof(settings));
    }

    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        ToolCatalogSnapshot catalog)
    {
        if (routeStep is null)
            throw new ArgumentNullException(nameof(routeStep));
        if (catalog is null)
            throw new ArgumentNullException(nameof(catalog));

        if (routeStep.Intent != RouteIntent.SelectTool || routeStep.Owner != DecisionOwner.Ai)
            throw new InvalidOperationException("Ollama provider invoked outside SelectTool.");

        if (catalog.Tools.Count == 0)
            return null;

        var selected = catalog.Tools
            .OrderBy(tool => tool.ToolId.Value, StringComparer.Ordinal)
            .First();

        return new ToolSelectionDecision(selected.ToolId, new Dictionary<string, object?>());
    }
}
