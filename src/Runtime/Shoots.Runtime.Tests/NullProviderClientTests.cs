using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Providers.Null;
using Shoots.Runtime.Abstractions.Provider;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class NullProviderClientTests
{
    [Fact]
    public async Task Tool_requests_fail_deterministically()
    {
        var client = new NullProviderClient();
        var envelope = new ProviderExecutionEnvelope(
            "req-null-tool",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("tools.sample"),
            new Dictionary<string, object?>(),
            null,
            "gate-1",
            new Dictionary<string, object?>());

        var first = await client.ExecuteAsync(envelope, default);
        var second = await client.ExecuteAsync(envelope, default);

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ProviderExecutionResultKind.Failed, first.Kind);
        Assert.Equal("tool.not_available", first.ErrorCode);
    }

    [Fact]
    public async Task Decision_requests_return_decision_required_deterministically()
    {
        var client = new NullProviderClient();
        var envelope = new ProviderExecutionEnvelope(
            "req-null-decision",
            ProviderExecutionEnvelopeKind.Decision,
            null,
            new Dictionary<string, object?>(),
            null,
            "gate-2",
            new Dictionary<string, object?> { ["plan.id"] = "plan-1" });

        var first = await client.ExecuteAsync(envelope, default);
        var second = await client.ExecuteAsync(envelope, default);

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ProviderExecutionResultKind.DecisionRequired, first.Kind);
        Assert.NotNull(first.DecisionRequest);
        Assert.Equal("gate-2", first.DecisionRequest!.RouteGateId);
    }
}
