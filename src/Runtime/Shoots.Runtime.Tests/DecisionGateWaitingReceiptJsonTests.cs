using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class DecisionGateWaitingReceiptJsonTests
{
    [Fact]
    public void Waiting_receipt_round_trips_structurally()
    {
        var receipt = new DecisionGateWaitingInfo(
            new WorkOrderId("wo-receipt"),
            "select",
            "select",
            "token-hash",
            "plan-hash",
            DecisionPolicy.Bypass,
            true,
            "decision_required",
            new[] { "terminate" },
            "tool.selection",
            DecisionOwner.Ai,
            new FallbackToolSelection(new ToolId("tools.echo"), new Dictionary<string, object?> { ["name"] = "alpha" }),
            null);

        var json = JsonSerializer.Serialize(receipt);
        var roundTrip = JsonSerializer.Deserialize<DecisionGateWaitingInfo>(json);

        Assert.NotNull(roundTrip);
        StructuralAssert.Equal(receipt, roundTrip);
    }
}
