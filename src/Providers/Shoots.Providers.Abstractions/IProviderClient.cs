using Shoots.Runtime.Abstractions.Execution;

namespace Shoots.Providers.Abstractions;

public interface IProviderClient
{
    ValueTask<ExecutionResult> ExecuteAsync(ExecutionEnvelope envelope, CancellationToken ct);
}
