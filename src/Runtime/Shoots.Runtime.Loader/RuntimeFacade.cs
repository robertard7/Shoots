using System.Diagnostics;
using System.Runtime.CompilerServices;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.Providers.Bridge;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;
using System.Linq;

namespace Shoots.Runtime.Loader;

public sealed class RuntimeFacade : IRuntimeFacade
{
    private readonly IRuntimeHost _host;
    private readonly IAiPolicyResolver _policyResolver;
    private readonly AiPresentationPolicy _policy;
    private readonly string _policyHash;

    public RuntimeFacade(IRuntimeHost host)
        : this(host, null)
    { }

    public RuntimeFacade(IRuntimeHost host, IAiPolicyResolver? policyResolver)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _policyResolver = policyResolver ?? new DefaultAiPolicyResolver();
        _policy = _policyResolver.Resolve(AiAccessRole.Developer);
        _policyHash = ComputePolicyHash(_policy);
        EnforceEmbeddedProvider();
    }

    public Task<RuntimeResult> StartExecution(BuildPlan plan, CancellationToken ct = default)
        => Task.FromResult(RuntimeResult.Fail(
            RuntimeError.Internal("Runtime facade execution is not configured.")
        ));

    public Task<IRuntimeStatusSnapshot> QueryStatus(CancellationToken ct = default)
        => Task.FromResult<IRuntimeStatusSnapshot>(
            new RuntimeStatusSnapshot(_host.Version, _policyHash));

	public IAsyncEnumerable<RoutingTraceEntry> SubscribeTrace(
		CancellationToken ct = default)
	{
		return EmptyTrace(ct);
	}

	private static async IAsyncEnumerable<RoutingTraceEntry> EmptyTrace(
		[EnumeratorCancellation] CancellationToken ct)
	{
		await Task.CompletedTask;
		yield break;
	}

    public Task CancelExecution(CancellationToken ct = default)
        => Task.CompletedTask;

    private sealed record RuntimeStatusSnapshot(
        RuntimeVersion Version,
        string PolicyHash) : IRuntimeStatusSnapshot;

    private static void EnforceEmbeddedProvider()
    {
        var registry = ProviderRegistryFactory.CreateDefault();
        registry.EnsureEmbeddedProviderPrimary();
    }

    private static string ComputePolicyHash(AiPresentationPolicy policy)
    {
        var value = $"{policy.Visibility}|{policy.AllowAiPanelToggle}|{policy.AllowCopyExport}|{policy.EnterpriseMode}";
        return HashTools.ComputeSha256Hash(value);
    }
}
