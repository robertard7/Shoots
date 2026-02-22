using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions.Provider;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderExecutionContractsJsonTests
{
    [Fact]
    public void Tool_execution_envelope_round_trips()
    {
        var envelope = new ProviderExecutionEnvelope(
            "req-1",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("tools.echo"),
            new Dictionary<string, object?>
            {
                ["name"] = "alpha"
            },
            "hello",
            "gate-1",
            new Dictionary<string, object?>
            {
                ["plan.id"] = "plan-1"
            });

        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<ProviderExecutionEnvelope>(json);

        Assert.NotNull(roundTrip);
<<<<<<< HEAD
        StructuralAssert.Equal(envelope, roundTrip);
=======
        Assert.Equal(envelope, roundTrip);
>>>>>>> origin/main
    }

    [Fact]
    public void Decision_execution_envelope_round_trips()
    {
        var envelope = new ProviderExecutionEnvelope(
            "req-2",
            ProviderExecutionEnvelopeKind.Decision,
            null,
            new Dictionary<string, object?>(),
            null,
            "gate-2",
            new Dictionary<string, object?>
            {
                ["intent.token"] = "tok"
            });

        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<ProviderExecutionEnvelope>(json);

        Assert.NotNull(roundTrip);
<<<<<<< HEAD
        StructuralAssert.Equal(envelope, roundTrip);
=======
        Assert.Equal(envelope, roundTrip);
>>>>>>> origin/main
    }

    [Fact]
    public void Tool_executed_result_round_trips()
    {
        var result = new ProviderExecutionResult(
            "req-3",
            ProviderExecutionResultKind.ToolExecuted,
            new ToolResult(
                new ToolId("tools.echo"),
                new Dictionary<string, object?> { ["value"] = "alpha" },
                true),
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<ProviderExecutionResult>(json);

        Assert.NotNull(roundTrip);
<<<<<<< HEAD
        StructuralAssert.Equal(result, roundTrip);
=======
        Assert.Equal(result, roundTrip);
>>>>>>> origin/main
    }

    [Fact]
    public void Decision_required_result_round_trips()
    {
        var result = new ProviderExecutionResult(
            "req-4",
            ProviderExecutionResultKind.DecisionRequired,
            null,
            new ProviderDecisionRequest(
                "req-4",
                "gate-4",
                new Dictionary<string, object?> { ["intent.token"] = "tok" }),
            null,
            null);

        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<ProviderExecutionResult>(json);

        Assert.NotNull(roundTrip);
<<<<<<< HEAD
        StructuralAssert.Equal(result, roundTrip);
=======
        Assert.Equal(result, roundTrip);
>>>>>>> origin/main
    }

    [Fact]
    public void Failed_result_round_trips()
    {
        var result = new ProviderExecutionResult(
            "req-5",
            ProviderExecutionResultKind.Failed,
            null,
            null,
            "tool.not_available",
            "not available");

        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<ProviderExecutionResult>(json);

        Assert.NotNull(roundTrip);
<<<<<<< HEAD
        StructuralAssert.Equal(result, roundTrip);
=======
        Assert.Equal(result, roundTrip);
>>>>>>> origin/main
    }
}
