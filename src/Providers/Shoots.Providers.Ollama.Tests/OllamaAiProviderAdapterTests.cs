using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Providers.Ollama.Tests;

public sealed class OllamaAiProviderAdapterTests
{
    [Fact]
    public void Throws_when_invoked_outside_select_tool()
    {
        var adapter = new OllamaAiProviderAdapter(new OllamaProviderSettings("http://localhost:11434", "stub"));
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-ollama"),
            "Original request.",
            "Ollama provider.",
            new List<string>(),
            new List<string>());
        var routeStep = new RouteStep(
            "validate",
            "Validate work.",
            "validate",
            RouteIntent.Validate,
            DecisionOwner.Runtime,
            workOrder.Id);
        var catalog = new ToolCatalogSnapshot("catalog", Array.Empty<ToolSpec>());

        Assert.Throws<InvalidOperationException>(() => adapter.RequestDecision(
            workOrder,
            routeStep,
            "graph",
            "catalog",
            catalog));
    }
}
