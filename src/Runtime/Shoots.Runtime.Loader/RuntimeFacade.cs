using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.ProviderAdapters.Bridge;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

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

    public Task<RuntimeExecutionResult> StartExecutionAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default)
    {
        _ = plan;
        _ = options;
        _ = ct;

        // Loader layer is UI-facing. It should not leak runtime internals.
        // Until execution wiring is implemented, return a UI-safe failure.
        return Task.FromResult(new RuntimeExecutionResult(
            RuntimeExecutionOutcome.Failed,
            WorkOrderId: null,
            PlanId: null,
            PlanHash: null,
            Message: "Runtime facade execution is not configured."
        ));
    }

    public Task<RuntimeStatusSnapshot> QueryStatusAsync(CancellationToken ct = default)
    {
        _ = ct;

        var v = _host.Version;
        var versionInfo = new RuntimeVersionInfo(
            Major: v.Major,
            Minor: v.Minor,
            Patch: v.Patch,
            Label: v.ToString());

        return Task.FromResult(new RuntimeStatusSnapshot(
            Version: versionInfo,
            PolicyHash: _policyHash,
            StateLabel: null));
    }

    public IAsyncEnumerable<Shoots.Runtime.Ui.Abstractions.RoutingTraceEntry> SubscribeTraceAsync(
        CancellationToken ct = default)
        => EmptyTrace(ct);

    public Task CancelExecutionAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Shoots.Runtime.Ui.Abstractions.RoutingTraceEntry> EmptyTrace(
        [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;
        await Task.CompletedTask;
        yield break;
    }

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