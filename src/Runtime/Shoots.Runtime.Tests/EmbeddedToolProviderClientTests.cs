using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Embedded;
using Shoots.Runtime.Abstractions.Provider;

namespace Shoots.Runtime.Tests;

public sealed class EmbeddedToolProviderClientTests
{
    [Fact]
    public async Task Unknown_tool_returns_stable_not_available_result()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-1",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.unknown.v1"),
            new Dictionary<string, object?>(),
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(ProviderExecutionResultKind.ToolExecuted, result.Kind);
        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.not_available", result.ToolResult.Outputs["error.code"]);
    }
}
