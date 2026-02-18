using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class DecisionPolicyDefaultsTests
{
    [Fact]
    public void Route_rule_missing_policy_fields_defaults_to_hard_and_null_fallbacks()
    {
        const string json = """
        {
          "NodeId": "select",
          "Intent": 0,
          "Owner": 1,
          "AllowedOutputKind": "tool.selection",
          "NodeKind": 0,
          "AllowedNextNodes": ["terminate"]
        }
        """;

        var rule = JsonSerializer.Deserialize<RouteRule>(json);

        Assert.NotNull(rule);
        Assert.Equal(DecisionPolicy.Hard, rule!.DecisionPolicy);
        Assert.Null(rule.FallbackToolSelection);
        Assert.Null(rule.FallbackNextNodeId);
    }

    [Fact]
    public void Route_rule_round_trips_with_explicit_policy_fields()
    {
        var rule = new RouteRule(
            "select",
            RouteIntent.SelectTool,
            DecisionOwner.Ai,
            "tool.selection",
            MermaidNodeKind.Start,
            new[] { "terminate" },
            DecisionPolicy.Bypass,
            new FallbackToolSelection(
                new ToolId("tools.echo"),
                new Dictionary<string, object?> { ["name"] = "alpha" }),
            "terminate");

        var json = JsonSerializer.Serialize(rule);
        var roundTrip = JsonSerializer.Deserialize<RouteRule>(json);

        Assert.NotNull(roundTrip);
        StructuralAssert.Equal(rule, roundTrip);
    }
}
