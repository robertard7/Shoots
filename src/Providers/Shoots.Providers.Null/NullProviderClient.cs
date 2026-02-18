using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions.Execution;

namespace Shoots.Providers.Null;

public sealed class NullProviderClient : IProviderClient
{
    public ValueTask<ExecutionResult> ExecuteAsync(ExecutionEnvelope envelope, CancellationToken ct)
    {
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));

        ct.ThrowIfCancellationRequested();

        if (envelope.Kind == ExecutionEnvelopeKind.Decision)
        {
            var routeGateId = envelope.RouteGateId ?? string.Empty;
            var decisionRequest = new DecisionRequest(
                envelope.RequestId,
                routeGateId,
                envelope.Context);

            return ValueTask.FromResult(new ExecutionResult(
                envelope.RequestId,
                ExecutionResultKind.DecisionRequired,
                null,
                decisionRequest,
                null,
                null));
        }

        var result = new ExecutionResult(
            envelope.RequestId,
            ExecutionResultKind.Failed,
            null,
            null,
            "tool.not_available",
            $"Tool '{envelope.ToolId?.Value ?? "unknown"}' is not available.");

        return ValueTask.FromResult(result);
    }
}
