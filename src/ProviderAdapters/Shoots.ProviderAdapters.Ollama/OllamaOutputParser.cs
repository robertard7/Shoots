using System;
using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Abstractions;

namespace Shoots.ProviderAdapters.Ollama;

public sealed class OllamaOutputParser
{
    private static readonly JsonSerializerOptions Options = new();

    public ToolSelectionDecision Parse(string response)
    {
        var payload = ProviderGuards.RequireOutput(response, nameof(response));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new FormatException("invalid JSON response", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("invalid JSON response");

            var toolId = ResolveToolId(root);
            if (string.IsNullOrWhiteSpace(toolId))
                throw new FormatException("toolId is required");

            var args = ResolveArgs(root);
            return new ToolSelectionDecision(new ToolId(toolId), args);
        }
    }

    private static string? ResolveToolId(JsonElement root)
    {
        if (TryReadString(root, "toolId", out var toolId) && !string.IsNullOrWhiteSpace(toolId))
            return toolId;

        if (TryReadString(root, "tool_id", out toolId) && !string.IsNullOrWhiteSpace(toolId))
            return toolId;

        if (TryReadString(root, "tool", out toolId) && !string.IsNullOrWhiteSpace(toolId))
            return toolId;

        return null;
    }

    private static Dictionary<string, object?> ResolveArgs(JsonElement root)
    {
        if (!root.TryGetProperty("args", out var argsElement))
            return new Dictionary<string, object?>();

        if (argsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new Dictionary<string, object?>();

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(argsElement.GetRawText(), Options)
            ?? new Dictionary<string, object?>();
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string? value)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }
}
