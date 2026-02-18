using System;
using System.Linq;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RouteRuleContractShapeTests
{
    [Fact]
    public void Route_rule_contract_shape_is_frozen()
    {
        var names = typeof(RouteRule)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        var expected = new[]
        {
            "NodeId",
            "Intent",
            "Owner",
            "AllowedOutputKind",
            "NodeKind",
            "AllowedNextNodes",
            "DecisionPolicy",
            "FallbackToolSelection",
            "FallbackNextNodeId"
        };

        Assert.Equal(expected, names);
    }

    [Fact]
    public void Routing_loop_default_step_budget_is_frozen()
    {
        var field = Type.GetType("Shoots.Runtime.Core.RoutingLoop, Shoots.Runtime.Core")!
            .GetField("DefaultStepBudget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(256, (int)field!.GetRawConstantValue()!);
    }
}
