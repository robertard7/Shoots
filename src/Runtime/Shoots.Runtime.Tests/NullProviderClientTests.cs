using System.Collections.Generic;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Providers.Null;
using Shoots.Runtime.Abstractions.Execution;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class NullProviderClientTests
{
    [Fact]
    public void Tool_requests_fail_deterministically()
    {
        var client = new NullProviderClient();
        var envelope = new ExecutionEnvelope(
            "req-null-tool",
            ExecutionEnvelopeKind.Tool,
            new ToolId("tools.sample"),
            new Dictionary<string, object?>(),
            null,
            "gate-1",
            new Dictionary<string, object?>());

        var first = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
        var second = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ExecutionResultKind.Failed, first.Kind);
        Assert.Equal("tool.not_available", first.ErrorCode);
    }

    [Fact]
    public void Decision_requests_return_decision_required_deterministically()
    {
        var client = new NullProviderClient();
        var envelope = new ExecutionEnvelope(
            "req-null-decision",
            ExecutionEnvelopeKind.Decision,
            null,
            new Dictionary<string, object?>(),
            null,
            "gate-2",
            new Dictionary<string, object?> { ["plan.id"] = "plan-1" });

        var first = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
        var second = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ExecutionResultKind.DecisionRequired, first.Kind);
        Assert.NotNull(first.DecisionRequest);
        Assert.Equal("gate-2", first.DecisionRequest!.RouteGateId);
    }
}
