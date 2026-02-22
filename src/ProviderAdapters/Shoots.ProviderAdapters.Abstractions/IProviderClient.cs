using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions.Provider;

<<<<<<< HEAD:src/ProviderAdapters/Shoots.ProviderAdapters.Abstractions/IProviderClient.cs
namespace Shoots.ProviderAdapters.Abstractions;
=======
namespace Shoots.Providers.Abstractions;
>>>>>>> origin/main:src/Providers/Shoots.Providers.Abstractions/IProviderClient.cs

public interface IProviderClient
{
    ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct);
}
