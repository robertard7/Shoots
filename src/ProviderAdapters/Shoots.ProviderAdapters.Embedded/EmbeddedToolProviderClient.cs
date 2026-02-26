using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Tools.Abstractions;
using Shoots.Tools.Linux;

namespace Shoots.ProviderAdapters.Embedded;

public sealed class EmbeddedToolProviderClient : IProviderClient
{
    private readonly LinuxToolHandlerRegistry _registry;
    private readonly ToolExecutionContext _baseContext;
    private readonly IReadOnlyDictionary<string, ToolSpec> _specs;

    public EmbeddedToolProviderClient(
        string repoRoot,
        int maxBytesOut = 16384,
        int maxTimeoutMs = 30000,
        bool allowNetwork = false,
        bool allowPrivileged = false,
        string? workingDirectory = null)
    {
        // dotnet test does not guarantee CWD == repo root.
        // Resolve a stable repo root by walking upward until we find the catalog (preferred) or a solution file (fallback).
        var resolvedRepoRoot = ResolveRepoRoot(repoRoot);

        _registry = LinuxToolHandlerRegistry.CreateDefault();

        var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? resolvedRepoRoot
            : Path.GetFullPath(workingDirectory, resolvedRepoRoot);

        if (!LinuxToolHandlers.IsPathWithin(resolvedRepoRoot, resolvedWorkingDirectory))
            throw new ArgumentOutOfRangeException(nameof(workingDirectory), "Working directory must stay within repository root.");

        _baseContext = ToolExecutionContext.Create(resolvedRepoRoot, CancellationToken.None, maxBytesOut, maxTimeoutMs, allowNetwork, allowPrivileged) with
        {
            WorkingDirectory = resolvedWorkingDirectory
        };

        _specs = LoadSpecsOrEmpty(resolvedRepoRoot);
    }

    public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Kind == ProviderExecutionEnvelopeKind.Decision)
        {
            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.DecisionRequired,
                null,
                new ProviderDecisionRequest(envelope.RequestId, envelope.RouteGateId ?? string.Empty, envelope.Context),
                null,
                null));
        }

        var toolId = envelope.ToolId ?? new ToolId("unknown");
        var handler = _registry.Resolve(toolId);

        if (handler is null)
        {
            var unavailable = new ToolResult(toolId, new Dictionary<string, object?>
            {
                ["tool_id"] = toolId.Value,
                ["error.code"] = "tool.not_available",
                ["error.message"] = $"Tool '{toolId.Value}' is not available."
            }, false);

            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.ToolExecuted,
                unavailable,
                null,
                null,
                null));
        }

        // Default: raw bindings (may be replaced by normalized bindings if schema validation runs).
        var invocationBindings = envelope.Args;

        // If we have a catalog spec, validate + normalize to ensure deterministic binding behavior.
        if (_specs.TryGetValue(toolId.Value, out var spec))
        {
            var validation = ToolBindingValidator.Validate(spec, envelope.Args);

            if (!validation.IsValid)
            {
                var missing = (validation.Missing ?? Array.Empty<string>())
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                var unknown = (validation.Unknown ?? Array.Empty<string>())
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                var invalid = new ToolResult(toolId, new Dictionary<string, object?>
                {
                    ["tool_id"] = toolId.Value,
                    ["error.code"] = "tool.bindings_invalid",
                    ["error.message"] = "Tool bindings failed schema validation.",
                    ["missing_inputs"] = string.Join("\n", missing),
                    ["unknown_inputs"] = string.Join("\n", unknown),
                    ["type_error"] = validation.TypeError ?? string.Empty
                }, false);

                return ValueTask.FromResult(new ProviderExecutionResult(
                    envelope.RequestId,
                    ProviderExecutionResultKind.ToolExecuted,
                    invalid,
                    null,
                    null,
                    null));
            }

            invocationBindings = validation.NormalizedBindings;
        }

        var invocation = new ToolInvocation(toolId, invocationBindings, new WorkOrderId(envelope.RequestId));

        if (!TryCreateExecutionContext(envelope.Context, ct, toolId, out var context, out var contextErrorResult))
        {
            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.ToolExecuted,
                contextErrorResult,
                null,
                null,
                null));
        }

        var result = handler.Execute(invocation, context!);
        result = ShapeResult(result, context!.MaxBytesOut);

        return ValueTask.FromResult(new ProviderExecutionResult(
            envelope.RequestId,
            ProviderExecutionResultKind.ToolExecuted,
            result,
            null,
            null,
            null));
    }

    private bool TryCreateExecutionContext(
        IReadOnlyDictionary<string, object?> envelopeContext,
        CancellationToken ct,
        ToolId toolId,
        out ToolExecutionContext? context,
        out ToolResult? errorResult)
    {
        var workingDirectory = ResolveContextString(envelopeContext, "working_directory") ?? _baseContext.WorkingDirectory;
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory, _baseContext.RepoRoot);

        if (!LinuxToolHandlers.IsPathWithin(_baseContext.RepoRoot, fullWorkingDirectory))
        {
            context = null;
            errorResult = new ToolResult(toolId, new Dictionary<string, object?>
            {
                ["tool_id"] = toolId.Value,
                ["error.code"] = "fs.path_escape",
                ["error.message"] = "Working directory escapes repository root."
            }, false);

            return false;
        }

        var requestedNetwork = ResolveContextBool(envelopeContext, "allow_network");
        var requestedPrivileged = ResolveContextBool(envelopeContext, "allow_privileged");

        // Context can only narrow permissions, never expand.
        var allowNetwork = _baseContext.AllowNetwork && (requestedNetwork ?? true);
        var allowPrivileged = _baseContext.AllowPrivileged && (requestedPrivileged ?? true);

        context = _baseContext with
        {
            CancellationToken = ct,
            WorkingDirectory = fullWorkingDirectory,
            AllowNetwork = allowNetwork,
            AllowPrivileged = allowPrivileged
        };

        errorResult = null;
        return true;
    }

    private static ToolResult ShapeResult(ToolResult result, int maxBytesOut)
    {
        var changed = false;
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pair in result.Outputs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;

            if (value is string text)
            {
                var normalized = text
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal);

                var truncated = LinuxToolText.TruncateUtf8(normalized, maxBytesOut);
                changed |= !string.Equals(text, truncated, StringComparison.Ordinal);
                outputs[pair.Key] = truncated;
            }
            else
            {
                outputs[pair.Key] = value;
            }
        }

        if (changed)
            outputs["output.truncated"] = true;

        return changed ? new ToolResult(result.ToolId, outputs, result.Success) : result;
    }

    private static bool? ResolveContextBool(IReadOnlyDictionary<string, object?> context, string key)
    {
        if (!context.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ResolveContextString(IReadOnlyDictionary<string, object?> context, string key)
    {
        if (!context.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToString(value);
    }

    private static IReadOnlyDictionary<string, ToolSpec> LoadSpecsOrEmpty(string repoRoot)
    {
        var catalogPath = Path.Combine(repoRoot, "etc", "tools.catalog.json");
        if (!File.Exists(catalogPath))
            return new Dictionary<string, ToolSpec>(StringComparer.Ordinal);

        return LinuxToolCatalog.LoadSpecs(catalogPath)
            .ToDictionary(spec => spec.ToolId.Value, StringComparer.Ordinal);
    }

    private static string ResolveRepoRoot(string startPath)
    {
        static bool LooksLikeRepoRoot(string dir)
        {
            // preferred signal
            var catalog = Path.Combine(dir, "etc", "tools.catalog.json");
            if (File.Exists(catalog))
                return true;

            // fallback signal
            var sln = Path.Combine(dir, "Shoots.sln");
            return File.Exists(sln);
        }

        var dir = startPath;
        if (File.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? startPath;

        dir = Path.GetFullPath(dir);

        var current = new DirectoryInfo(dir);
        for (var i = 0; i < 16 && current is not null; i++)
        {
            if (LooksLikeRepoRoot(current.FullName))
                return current.FullName;

            current = current.Parent;
        }

        // Last resort: use what the caller gave us.
        return Path.GetFullPath(startPath);
    }
}