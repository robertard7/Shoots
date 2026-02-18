using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions.Provider;

namespace Shoots.ProviderAdapters.Abstractions;

public interface IProviderClient
{
    ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct);
}
