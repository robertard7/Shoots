using System;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Ollama;

public sealed class OllamaOutputParser
{
    private static readonly JsonSerializerOptions Options = new();

    public ToolSelectionDecision Parse(string response)
    {
        var payload = ProviderGuards.RequireOutput(response, nameof(response));

        OllamaToolDecision? decision;
        try
        {
            decision = JsonSerializer.Deserialize<OllamaToolDecision>(payload, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException("invalid JSON response", ex);
        }

        if (decision is null || string.IsNullOrWhiteSpace(decision.ToolId))
            throw new FormatException("toolId is required");

        var args = decision.Args ?? new System.Collections.Generic.Dictionary<string, object?>();
        return new ToolSelectionDecision(new ToolId(decision.ToolId), args);
    }
}
