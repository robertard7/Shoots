using System;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ContractEnumFreezeTests
{
    [Fact]
    public void Decision_policy_enum_values_are_frozen()
    {
        var expected = new[]
        {
            nameof(DecisionPolicy.Hard),
            nameof(DecisionPolicy.Bypass),
            nameof(DecisionPolicy.Error)
        };

        var actual = Enum.GetNames(typeof(DecisionPolicy));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Routing_trace_event_kind_values_include_host_and_gate_markers()
    {
        var expected = new[]
        {
            "Plan",
            "Command",
            "Result",
            "Error",
            "Route",
            "WorkOrderReceived",
            "RouteEntered",
            "NodeEntered",
            "DecisionRequired",
            "DecisionAccepted",
            "DecisionGateWaiting",
            "DecisionGateBypassed",
            "DecisionGateRequiredError",
            "StepBudgetExceeded",
            "HostBlockedRerunWaiting",
            "HostResumeOverridePlanChange",
            "HostResumeDiscardWaiting",
            "NodeTransitionChosen",
            "NodeAdvanced",
            "NodeHalted",
            "DecisionRejected",
            "ToolExecuted",
            "ToolResult",
            "Halted",
            "Completed"
        };

        var actual = Enum.GetNames(typeof(RoutingTraceEventKind));
        Assert.Equal(expected, actual);
    }
}
