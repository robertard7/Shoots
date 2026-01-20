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

    [Fact]
    public void Deterministic_decision_replays_for_same_inputs()
    {
        var stub = new OllamaStubClient("{\"toolId\":\"tools.sample\",\"args\":{}}");
        var adapter = new OllamaAiProviderAdapter(
            new OllamaProviderSettings("http://localhost:11434", "stub"),
            client: stub);
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-ollama"),
            "Original request.",
            "Ollama provider.",
            new List<string>(),
            new List<string>());
        var routeStep = new RouteStep(
            "select",
            "Select tool.",
            "select",
            RouteIntent.SelectTool,
            DecisionOwner.Ai,
            workOrder.Id);
        var catalog = new ToolCatalogSnapshot(
            "catalog",
            new[]
            {
                new ToolSpec(
                    new ToolId("tools.sample"),
                    "Sample.",
                    new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
                    new List<ToolInputSpec>(),
                    new List<ToolOutputSpec>(),
                    Array.Empty<string>())
            });

        var first = adapter.RequestDecision(workOrder, routeStep, "graph", "catalog", catalog);
        var second = adapter.RequestDecision(workOrder, routeStep, "graph", "catalog", catalog);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(new ToolId("tools.sample"), first!.ToolId);
        Assert.Equal(first!.ToolId, second!.ToolId);
        Assert.Equal(first.Bindings.Count, second.Bindings.Count);
    }
}
