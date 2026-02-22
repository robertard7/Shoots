using System.Collections.Generic;
using System.Text.Json;
<<<<<<< HEAD
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Null;
=======
using Shoots.Contracts.Core;
using Shoots.Providers.Null;
>>>>>>> origin/main
using Shoots.Runtime.Abstractions.Provider;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class NullProviderClientTests
{
    [Fact]
<<<<<<< HEAD
    public async Task Tool_requests_fail_deterministically()
=======
    public void Tool_requests_fail_deterministically()
>>>>>>> origin/main
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

<<<<<<< HEAD
        var first = await client.ExecuteAsync(envelope, default);
        var second = await client.ExecuteAsync(envelope, default);
=======
        var first = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
        var second = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
>>>>>>> origin/main

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ProviderExecutionResultKind.Failed, first.Kind);
        Assert.Equal("tool.not_available", first.ErrorCode);
    }

    [Fact]
<<<<<<< HEAD
    public async Task Decision_requests_return_decision_required_deterministically()
=======
    public void Decision_requests_return_decision_required_deterministically()
>>>>>>> origin/main
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

<<<<<<< HEAD
        var first = await client.ExecuteAsync(envelope, default);
        var second = await client.ExecuteAsync(envelope, default);
=======
        var first = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
        var second = client.ExecuteAsync(envelope, default).GetAwaiter().GetResult();
>>>>>>> origin/main

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(ProviderExecutionResultKind.DecisionRequired, first.Kind);
        Assert.NotNull(first.DecisionRequest);
        Assert.Equal("gate-2", first.DecisionRequest!.RouteGateId);
    }
}
