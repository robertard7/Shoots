using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanSerializationToolResultTests
{
    [Fact]
    public void Tool_result_round_trips_in_plan_json()
    {
        var workOrder = TestRequestFactory.CreateWorkOrder();
        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            TestRequestFactory.CreateRouteRules());

        var toolResult = new ToolResult(
            new ToolId("tools.result"),
            new Dictionary<string, object?> { ["value"] = "alpha" },
            true);

        var plan = BuildPlanTestFactory.CreatePlan(
            request,
            new BuildStep[] { new BuildStep("step-1", "Step") },
            toolResult: toolResult);

        var json = BuildPlanRenderer.RenderJson(plan);
        var roundTrip = JsonSerializer.Deserialize<BuildPlan>(json);

        Assert.NotNull(roundTrip);
        Assert.NotNull(roundTrip!.ToolResult);
        Assert.Equal(toolResult.ToolId, roundTrip.ToolResult!.ToolId);
        Assert.Equal(toolResult.Success, roundTrip.ToolResult.Success);
        Assert.Equal(toolResult.Outputs["value"], roundTrip.ToolResult.Outputs["value"]);
    }
}
