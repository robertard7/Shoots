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
        _registry = LinuxToolHandlerRegistry.CreateDefault();
        _baseContext = ToolExecutionContext.Create(repoRoot, CancellationToken.None, maxBytesOut, maxTimeoutMs, allowNetwork, allowPrivileged) with
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? repoRoot : Path.GetFullPath(workingDirectory, repoRoot)
        };
        var catalogPath = Path.Combine(repoRoot, "etc", "tools.catalog.json");
        _specs = File.Exists(catalogPath)
            ? LinuxToolCatalog.LoadSpecs(catalogPath).ToDictionary(spec => spec.ToolId.Value, StringComparer.Ordinal)
            : new Dictionary<string, ToolSpec>(StringComparer.Ordinal);
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

        var invocationBindings = envelope.Args;
        if (_specs.TryGetValue(toolId.Value, out var spec))
        {
            var validation = ToolBindingValidator.Validate(spec, envelope.Args);
            if (!validation.IsValid)
            {
                var invalid = new ToolResult(toolId, new Dictionary<string, object?>
                {
                    ["tool_id"] = toolId.Value,
                    ["error.code"] = "tool.bindings_invalid",
                    ["error.message"] = "Tool bindings failed schema validation.",
                    ["missing_inputs"] = string.Join("\n", validation.Missing),
                    ["unknown_inputs"] = string.Join("\n", validation.Unknown),
                    ["type_error"] = validation.TypeError
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
                var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
                var truncated = Shoots.Tools.Linux.ToolResultFactory.TruncateUtf8(normalized, maxBytesOut);
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

}
