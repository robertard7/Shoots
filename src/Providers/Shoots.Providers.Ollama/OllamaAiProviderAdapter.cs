using System;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Ollama;

public sealed class OllamaAiProviderAdapter : IAiProviderAdapter
{
    private readonly OllamaProviderSettings _settings;
    private readonly OllamaPromptBuilder _promptBuilder;
    private readonly OllamaOutputParser _parser;
    private readonly OllamaStubClient _client;

    public OllamaAiProviderAdapter(
        OllamaProviderSettings settings,
        OllamaPromptBuilder? promptBuilder = null,
        OllamaOutputParser? parser = null,
        OllamaStubClient? client = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(_settings.Endpoint))
            throw new ArgumentException("endpoint is required", nameof(settings));
        if (string.IsNullOrWhiteSpace(_settings.Model))
            throw new ArgumentException("model is required", nameof(settings));

        _promptBuilder = promptBuilder ?? new OllamaPromptBuilder();
        _parser = parser ?? new OllamaOutputParser();
        _client = client ?? new OllamaStubClient("{\"toolId\":\"\",\"args\":{}}");
    }

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

        if (routeStep.Intent != RouteIntent.SelectTool || routeStep.Owner != DecisionOwner.Ai)
            throw new InvalidOperationException("Ollama provider invoked outside SelectTool.");

        if (catalog.Tools.Count == 0)
            return null;

        var prompt = _promptBuilder.Build(workOrder, routeStep, graphHash, catalogHash, catalog);
        var response = _client.SendPrompt(prompt);
        var decision = _parser.Parse(response);

        var tool = catalog.Tools.FirstOrDefault(candidate => candidate.ToolId == decision.ToolId);
        if (tool is null)
            throw new InvalidOperationException("unknown tool selected");

        return decision;
    }
}
