using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Tests;

internal static class TestRequestFactory
{
    internal static BuildRequest CreateBuildRequest(string commandId, IReadOnlyDictionary<string, object?>? args = null)
    {
        return new BuildRequest(
            WorkOrder: CreateWorkOrder(),
            CommandId: commandId,
            Args: args ?? new Dictionary<string, object?>(),
            RouteRules: CreateRouteRules());
    }

    internal static WorkOrder CreateWorkOrder()
    {
        return new WorkOrder(
            Id: new WorkOrderId("wo-test"),
            OriginalRequest: "Original test request.",
            Goal: "Validate deterministic planning.",
            Constraints: Array.Empty<string>(),
            SuccessCriteria: Array.Empty<string>());
    }

    internal static IReadOnlyList<RouteRule> CreateRouteRules()
    {
        return new[]
        {
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
            new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
            new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review"),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination")
        };
    }
}
