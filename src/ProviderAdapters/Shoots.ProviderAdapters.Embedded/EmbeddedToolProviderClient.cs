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

    public EmbeddedToolProviderClient(string repoRoot, int maxBytesOut = 16384, bool allowNetwork = false)
    {
        _registry = LinuxToolHandlerRegistry.CreateDefault();
        _context = ToolExecutionContext.Create(repoRoot, CancellationToken.None, maxBytesOut, allowNetwork);
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
