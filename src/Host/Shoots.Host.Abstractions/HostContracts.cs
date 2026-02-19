using Shoots.Contracts.Core;

namespace Shoots.Host.Abstractions;

public sealed record HostPolicyOptions(
    TimeSpan ProviderTimeout,
    int MaxRetries,
    bool AllowRemote,
    bool AllowLocal,
    bool AllowCloudAssist
)
{
    public static HostPolicyOptions Default { get; } = new(
        ProviderTimeout: TimeSpan.FromSeconds(30),
        MaxRetries: 1,
        AllowRemote: true,
        AllowLocal: true,
        AllowCloudAssist: false);
}

public sealed record ModelDescriptor(
    string ModelId,
    string ProviderId,
    int Priority,
    bool IsRemote,
    bool SupportsTools
);

public interface IModelCatalog
{
    IReadOnlyList<ModelDescriptor> ListModels();
}

public sealed record ProviderSelectionRequest(
    WorkOrderId WorkOrderId,
    string PlanHash,
    HostPolicyOptions Policy,
    string? PreferredModelId = null
);

public sealed record ProviderSelection(
    string ProviderId,
    string ModelId,
    bool IsRemote
);

public interface IProviderPolicy
{
    ProviderSelection Select(ProviderSelectionRequest request, IReadOnlyList<ModelDescriptor> models);
}

public sealed record DecisionInjectionRequest(
    string WorkOrderId,
    string PlanHash,
    string RouteGateId,
    string ToolId,
    string BindingsJsonCanonical
);

public enum HostResumeIntentMode
{
    None = 0,
    InjectDecision = 1,
    OverridePlanChange = 2,
    DiscardWaitingStartOver = 3
}

public sealed record HostResumeIntent(HostResumeIntentMode Mode);
