using System;
using System.Threading;
using System.Threading.Tasks;
<<<<<<< HEAD:src/ProviderAdapters/Shoots.ProviderAdapters.Null/NullProviderClient.cs
using Shoots.ProviderAdapters.Abstractions;
using Shoots.Runtime.Abstractions.Provider;

namespace Shoots.ProviderAdapters.Null;
=======
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions.Provider;

namespace Shoots.Providers.Null;
>>>>>>> origin/main:src/Providers/Shoots.Providers.Null/NullProviderClient.cs

public sealed class NullProviderClient : IProviderClient
{
    public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct)
    {
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));

        ct.ThrowIfCancellationRequested();

        if (envelope.Kind == ProviderExecutionEnvelopeKind.Decision)
        {
            var decisionRequest = new ProviderDecisionRequest(
                envelope.RequestId,
                envelope.RouteGateId ?? string.Empty,
                envelope.Context);

            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.DecisionRequired,
                null,
                decisionRequest,
                null,
                null));
        }

        return ValueTask.FromResult(new ProviderExecutionResult(
            envelope.RequestId,
            ProviderExecutionResultKind.Failed,
            null,
            null,
            "tool.not_available",
            $"Tool '{envelope.ToolId?.Value ?? "unknown"}' is not available."));
    }
}
