using System;
using System.Linq;
using System.Text;
using Shoots.Contracts.Core;

namespace Shoots.Providers.Ollama;

public sealed class OllamaPromptBuilder
{
    public string Build(
        WorkOrder workOrder,
        RouteStep routeStep,
        string graphHash,
        string catalogHash,
        ToolCatalogSnapshot catalog)
    {
        if (workOrder is null)
            throw new ArgumentNullException(nameof(workOrder));
        if (routeStep is null)
            throw new ArgumentNullException(nameof(routeStep));
        if (catalog is null)
            throw new ArgumentNullException(nameof(catalog));
        if (string.IsNullOrWhiteSpace(graphHash))
            throw new ArgumentException("graph hash is required", nameof(graphHash));
        if (string.IsNullOrWhiteSpace(catalogHash))
            throw new ArgumentException("catalog hash is required", nameof(catalogHash));

        var builder = new StringBuilder();
        builder.AppendLine("SYSTEM: You select a tool only. You do not control routing.");
        builder.AppendLine("Return strict JSON: { \"toolId\": \"...\", \"args\": { } }.");
        builder.AppendLine($"graph.hash={graphHash}");
        builder.AppendLine($"catalog.hash={catalogHash}");
        builder.AppendLine($"route.intent={routeStep.Intent}");
        builder.AppendLine($"workorder.goal={workOrder.Goal}");
        builder.AppendLine("tools:");

        foreach (var tool in catalog.Tools.OrderBy(tool => tool.ToolId.Value, StringComparer.Ordinal))
        {
            builder.Append("- ")
                .Append(tool.ToolId.Value)
                .Append(": ")
                .AppendLine(tool.Description);
        }

        return builder.ToString();
    }
}
