using System.Diagnostics;
using Shoots.Contracts.Core;
using Shoots.Providers.Bridge;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class RuntimeFacade : IRuntimeFacade
{
    private readonly IRuntimeHost _host;

    public RuntimeFacade(IRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        EnforceEmbeddedProvider();
    }

    public Task<RuntimeResult> StartExecution(BuildPlan plan, CancellationToken ct = default)
    {
        _ = plan;
        _ = ct;
        return Task.FromResult(RuntimeResult.Fail(RuntimeError.Internal("Runtime facade execution is not configured.")));
    }

    public Task<IRuntimeStatusSnapshot> QueryStatus(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<IRuntimeStatusSnapshot>(new RuntimeStatusSnapshot(_host.Version));
    }

    public async IAsyncEnumerable<RoutingTraceEntry> SubscribeTrace(CancellationToken ct = default)
    {
        _ = ct;
        yield break;
    }

    public Task CancelExecution(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
    }

    private sealed record RuntimeStatusSnapshot(RuntimeVersion Version) : IRuntimeStatusSnapshot;

    private static void EnforceEmbeddedProvider()
    {
        var registry = ProviderRegistryFactory.CreateDefault();
        Trace.WriteLine($"[Shoots.Runtime.Loader] Provider order: {string.Join(", ", registry.RegistrationOrder)}");
        registry.EnsureEmbeddedProviderPrimary();
    }
}
