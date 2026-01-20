using System;
using System.Text.Json;
using Shoots.Contracts.Core;

namespace Shoots.Providers.Ollama;

public sealed class OllamaOutputParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ToolSelectionDecision Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new ArgumentException("response is required", nameof(response));

        OllamaToolDecision? decision;
        try
        {
            decision = JsonSerializer.Deserialize<OllamaToolDecision>(response, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("invalid JSON response", ex);
        }

        if (decision is null || string.IsNullOrWhiteSpace(decision.ToolId))
            throw new InvalidOperationException("toolId is required");

        var args = decision.Args ?? new System.Collections.Generic.Dictionary<string, object?>();
        return new ToolSelectionDecision(new ToolId(decision.ToolId), args);
    }
}
