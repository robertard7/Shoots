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
        Assert.Equal("linux.unknown.v1", result.ToolResult.Outputs["tool_id"]);
        Assert.Equal("tool.not_available", result.ToolResult.Outputs["error.code"]);
        Assert.Equal("Tool 'linux.unknown.v1' is not available.", result.ToolResult.Outputs["error.message"]);
    }

    [Fact]
    public async Task Network_tool_is_guarded_when_disabled()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory(), allowNetwork: false);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-2",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.net.http_get_text.v1"),
            new Dictionary<string, object?> { ["url"] = "https://example.com" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(ProviderExecutionResultKind.ToolExecuted, result.Kind);
        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.network_disabled", result.ToolResult.Outputs["error.code"]);
    }

    [Fact]
    public async Task Missing_required_binding_returns_bindings_invalid()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-3",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.write_text.v1"),
            new Dictionary<string, object?> { ["path"] = "tmp/a.txt" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(ProviderExecutionResultKind.ToolExecuted, result.Kind);
        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.bindings_invalid", result.ToolResult.Outputs["error.code"]);
        Assert.Equal("text", result.ToolResult.Outputs["missing_inputs"]);
    }

    [Fact]
    public async Task Unknown_binding_returns_bindings_invalid()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-4",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["path"] = "tmp/a.txt", ["nope"] = true },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(ProviderExecutionResultKind.ToolExecuted, result.Kind);
        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.bindings_invalid", result.ToolResult.Outputs["error.code"]);
        Assert.Equal("nope", result.ToolResult.Outputs["unknown_inputs"]);
    }

    [Fact]
    public async Task Bindings_invalid_payload_shape_is_frozen()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-5",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["nope"] = true },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        var keys = result.ToolResult!.Outputs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "error.code", "error.message", "missing_inputs", "tool_id", "type_error", "unknown_inputs" }, keys);
    }

    [Fact]
    public async Task Int_binding_accepts_string_value()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-6",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["path"] = "docs/index.md", ["max_bytes"] = "16" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.True(result.ToolResult!.Success);
    }

    [Fact]
    public async Task Int_binding_rejects_non_whole_double()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-7",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["path"] = "docs/index.md", ["max_bytes"] = 16.5d },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.bindings_invalid", result.ToolResult.Outputs["error.code"]);
        Assert.Equal("max_bytes:int", result.ToolResult.Outputs["type_error"]);
    }

    [Fact]
    public async Task Bool_binding_accepts_string_value()
    {
        var client = new EmbeddedToolProviderClient(Directory.GetCurrentDirectory());

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-8",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.append_text.v1"),
            new Dictionary<string, object?> { ["path"] = "tmp/append.txt", ["text"] = "x", ["newline"] = "false" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.True(result.ToolResult!.Success);
    }
}
