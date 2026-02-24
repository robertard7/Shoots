using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Tools.Abstractions;
using Shoots.Tools.Linux;

namespace Shoots.ProviderAdapters.Embedded;

public sealed class EmbeddedToolProviderClient : IProviderClient
{
    private readonly LinuxToolHandlerRegistry _registry;
    private readonly ToolExecutionContext _context;
    private readonly IReadOnlyDictionary<string, ToolSpec> _specs;

    public EmbeddedToolProviderClient(string repoRoot, int maxBytesOut = 16384, int maxTimeoutMs = 30000, bool allowNetwork = false)
    {
        _registry = LinuxToolHandlerRegistry.CreateDefault();
        _context = ToolExecutionContext.Create(repoRoot, CancellationToken.None, maxBytesOut, maxTimeoutMs, allowNetwork);
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
                    ["missing_inputs"] = validation.Missing,
                    ["unknown_inputs"] = validation.Unknown,
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
        }

        var invocation = new ToolInvocation(toolId, envelope.Args, new WorkOrderId(envelope.RequestId));
        var context = _context with { CancellationToken = ct };
        var result = handler.Execute(invocation, context);

        return ValueTask.FromResult(new ProviderExecutionResult(
            envelope.RequestId,
            ProviderExecutionResultKind.ToolExecuted,
            result,
            null,
            null,
            null));
    }
}
