using System.Diagnostics;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.Providers.Bridge;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class RuntimeFacade : IRuntimeFacade
{
    private readonly IRuntimeHost _host;
    private readonly IAiPolicyResolver _policyResolver;
    private readonly AiPresentationPolicy _policy;

    public RuntimeFacade(IRuntimeHost host)
        : this(host, null)
    { }

    public RuntimeFacade(IRuntimeHost host, IAiPolicyResolver? policyResolver)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _policyResolver = policyResolver ?? new DefaultAiPolicyResolver();
        _policy = _policyResolver.Resolve(AiAccessRole.Developer);
        Trace.WriteLine($"[Shoots.Runtime.Loader] AI policy: {_policy.Visibility} (enterprise={_policy.EnterpriseMode})");
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
        return Task.FromResult<IRuntimeStatusSnapshot>(
            new RuntimeStatusSnapshot(_host.Version, ComputePolicyHash(_policy)));
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

    private sealed record RuntimeStatusSnapshot(RuntimeVersion Version, string PolicyHash) : IRuntimeStatusSnapshot;

    private static void EnforceEmbeddedProvider()
    {
        var registry = ProviderRegistryFactory.CreateDefault();
        Trace.WriteLine($"[Shoots.Runtime.Loader] Provider order: {string.Join(", ", registry.RegistrationOrder)}");
        registry.EnsureEmbeddedProviderPrimary();
    }

    private static string ComputePolicyHash(AiPresentationPolicy policy)
    {
        // ⚠️ CONTRACT FROZEN: Keep ordering aligned with AiPresentationPolicy.ContractShape.
        // Any change requires a contract version bump plus tripwire updates.
        var value = $"{policy.Visibility}|{policy.AllowAiPanelToggle}|{policy.AllowCopyExport}|{policy.EnterpriseMode}";
        return HashTools.ComputeSha256Hash(value);
    }
}
