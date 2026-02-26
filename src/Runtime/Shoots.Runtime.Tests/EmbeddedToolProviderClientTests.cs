using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Embedded;
using Shoots.Runtime.Abstractions.Provider;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class EmbeddedToolProviderClientTests
{
    private static string RepoRoot => FindRepoRoot();

    [Fact]
    public async Task Unknown_tool_returns_stable_not_available_result()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

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
        var client = new EmbeddedToolProviderClient(RepoRoot, allowNetwork: false);

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
        var client = new EmbeddedToolProviderClient(RepoRoot);

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
        var client = new EmbeddedToolProviderClient(RepoRoot);

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
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-5",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["nope"] = true },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        var keys = result.ToolResult!.Outputs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "error.code", "error.message", "missing_inputs", "tool_id", "type_error", "unknown_inputs" }, keys);
    }

    [Fact]
    public async Task Int_binding_accepts_string_value()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-6",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["path"] = "docs/index.md", ["max_bytes"] = "16" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.True(result.ToolResult!.Success);
    }

    [Fact]
    public async Task Int_binding_rejects_non_whole_double()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-7",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.read_text.v1"),
            new Dictionary<string, object?> { ["path"] = "docs/index.md", ["max_bytes"] = 16.5d },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.bindings_invalid", result.ToolResult.Outputs["error.code"]);
        Assert.Equal("max_bytes:int", result.ToolResult.Outputs["type_error"]);
    }

    [Fact]
    public async Task Bool_binding_accepts_string_value()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-8",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.fs.append_text.v1"),
            new Dictionary<string, object?> { ["path"] = "tmp/append.txt", ["text"] = "x", ["newline"] = "false" },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.True(result.ToolResult!.Success);
    }

    [Fact]
    public async Task Missing_and_unknown_inputs_are_newline_joined_and_sorted()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-9",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.text.replace.v1"),
            new Dictionary<string, object?> { ["zzz"] = 1, ["aaa"] = 2 },
            null,
            null,
            new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("path\nreplace\nsearch", result.ToolResult.Outputs["missing_inputs"]);
        Assert.Equal("aaa\nzzz", result.ToolResult.Outputs["unknown_inputs"]);
        Assert.Equal(string.Empty, result.ToolResult.Outputs["type_error"]);
    }

    [Fact]
    public async Task Working_directory_escape_is_blocked()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-10",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.proc.cwd.v1"),
            new Dictionary<string, object?>(),
            null,
            null,
            new Dictionary<string, object?>
            {
                ["working_directory"] = "../../"
            }),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("fs.path_escape", result.ToolResult.Outputs["error.code"]);
    }

    [Fact]
    public async Task Context_cannot_escalate_network_gate_when_disabled_by_host()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot, allowNetwork: false);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-11",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.net.http_get_text.v1"),
            new Dictionary<string, object?> { ["url"] = "https://example.com" },
            null,
            null,
            new Dictionary<string, object?>
            {
                ["allow_network"] = true
            }),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.network_disabled", result.ToolResult.Outputs["error.code"]);
    }

    [Fact]
    public void Constructor_working_directory_escape_is_rejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EmbeddedToolProviderClient(RepoRoot, workingDirectory: "../../"));

        Assert.Equal("workingDirectory", ex.ParamName);
        Assert.Contains("repository root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_cannot_escalate_privileged_gate_when_disabled_by_host()
    {
        var client = new EmbeddedToolProviderClient(RepoRoot, allowPrivileged: false);

        var result = await client.ExecuteAsync(new ProviderExecutionEnvelope(
            "req-12",
            ProviderExecutionEnvelopeKind.Tool,
            new ToolId("linux.sys.systemctl_status.v1"),
            new Dictionary<string, object?>(),
            null,
            null,
            new Dictionary<string, object?>
            {
                ["allow_privileged"] = true
            }),
            CancellationToken.None);

        Assert.NotNull(result.ToolResult);
        Assert.False(result.ToolResult!.Success);
        Assert.Equal("tool.privileged_disabled", result.ToolResult.Outputs["error.code"]);
    }

    private static string FindRepoRoot()
    {
        static bool LooksLikeRepoRoot(string dir)
        {
            var catalog = Path.Combine(dir, "etc", "tools.catalog.json");
            if (File.Exists(catalog))
                return true;

            var sln = Path.Combine(dir, "Shoots.sln");
            return File.Exists(sln);
        }

        var dir = Path.GetFullPath(Directory.GetCurrentDirectory());
        var current = new DirectoryInfo(dir);

        for (var i = 0; i < 16 && current is not null; i++)
        {
            if (LooksLikeRepoRoot(current.FullName))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not locate repo root from '{dir}'. Expected to find etc/tools.catalog.json or Shoots.sln in an ancestor directory.");
    }
}