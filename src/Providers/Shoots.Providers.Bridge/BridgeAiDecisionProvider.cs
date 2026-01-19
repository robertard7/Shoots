using System;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions;

namespace Shoots.Providers.Bridge;

public sealed class BridgeAiDecisionProvider : IAiDecisionProvider
{
    private readonly ProviderRegistry _registry;
    private readonly string _providerId;

    public BridgeAiDecisionProvider(ProviderRegistry registry, string providerId)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("provider id is required", nameof(providerId));

        _providerId = providerId;
    }

    public RouteDecision? RequestDecision(AiDecisionRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var step = request.Step;
        if (step.Owner != DecisionOwner.Ai)
            return null;

        var adapter = _registry.Get(_providerId);
        if (adapter is null)
            throw new InvalidOperationException($"Provider '{_providerId}' is not registered.");

        return adapter.RequestDecision(
            request.WorkOrder,
            step,
            request.NodeKind,
            request.AllowedNextNodes,
            request.IntentToken,
            request.CatalogHash ?? string.Empty,
            string.Empty);
    }
}
