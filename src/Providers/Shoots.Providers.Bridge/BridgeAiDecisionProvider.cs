using System;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions;

namespace Shoots.Providers.Bridge;

public sealed class BridgeAiDecisionProvider : IAiDecisionProvider
{
    private readonly ProviderRegistry _registry;
    private readonly string _providerId;

    public BridgeAiDecisionProvider(
        ProviderRegistry registry,
        string providerId)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("provider id is required", nameof(providerId));

        _providerId = providerId;
    }

    public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var adapter = _registry.Get(_providerId);
        if (adapter is null)
            throw new InvalidOperationException(
                $"Provider '{_providerId}' is not registered.");

        try
        {
            var contractCatalog = ToContractSnapshot(
                request.Catalog,
                request.CatalogHash);

            return adapter.RequestDecision(
                request.WorkOrder,
                request.RouteStep,
                request.GraphHash,
                request.CatalogHash,
                contractCatalog);
        }
        catch (Exception ex) when (ex is not ProviderFailureException)
        {
            var failure = ProviderFailure.FromException(ex, _providerId);
            throw new ProviderFailureException(failure, ex);
        }
    }

    private static Shoots.Contracts.Core.ToolCatalogSnapshot ToContractSnapshot(
        Shoots.Runtime.Abstractions.ToolCatalogSnapshot runtime,
        string catalogHash)
    {
        if (runtime is null)
            throw new ArgumentNullException(nameof(runtime));
        if (string.IsNullOrWhiteSpace(catalogHash))
            throw new ArgumentException("catalog hash is required", nameof(catalogHash));

        // Runtime snapshot → pure ToolSpec list
        var specs = runtime
            .Entries
            .Select(e => e.Spec)
            .ToArray();

        return new Shoots.Contracts.Core.ToolCatalogSnapshot(
            catalogHash,
            specs);
    }
}
