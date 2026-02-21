using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Abstractions;
using Shoots.ProviderAdapters.Abstractions;
using Shoots.ProviderAdapters.Null;
using Shoots.Runtime.Core;

namespace Shoots.Host.Core;

public sealed class HostRunCoordinator
{
    private readonly RuntimeOrchestrator _orchestrator;

    public HostRunCoordinator(IToolRegistry registry, IAiDecisionProvider aiDecisionProvider, IRuntimeNarrator? narrator = null)
        : this(registry, aiDecisionProvider, new InMemoryRuntimePersistence(), narrator, null)
    {
    }

    public HostRunCoordinator(IToolRegistry registry, IAiDecisionProvider aiDecisionProvider, IProviderClient providerClient, IRuntimeNarrator? narrator = null)
        : this(registry, aiDecisionProvider, new InMemoryRuntimePersistence(), narrator, providerClient)
    {
    }

    public HostRunCoordinator(IToolRegistry registry, IAiDecisionProvider aiDecisionProvider, InMemoryRuntimePersistence persistence, IRuntimeNarrator? narrator = null)
        : this(registry, aiDecisionProvider, persistence, narrator, null)
    {
    }

    public HostRunCoordinator(IToolRegistry registry, IAiDecisionProvider aiDecisionProvider, InMemoryRuntimePersistence persistence, IRuntimeNarrator? narrator, IProviderClient? providerClient)
    {
        _orchestrator = new RuntimeOrchestrator(
            registry,
            aiDecisionProvider,
            narrator ?? new NullRuntimeNarrator(),
            providerClient ?? new NullProviderClient(),
            persistence);
    }

    public ExecutionEnvelope Run(BuildPlan plan, RuntimeRunOptions? options = null)
        => _orchestrator.Run(plan, options);

    public static RuntimeRunOptions CreateResumeOptions(DecisionInjectionRequest request, HostResumeIntent intent)
    {
        var decisionPayload = JsonSerializer.Serialize(new
        {
            workOrderId = request.WorkOrderId,
            planHash = request.PlanHash,
            routeGateId = request.RouteGateId,
            toolId = request.ToolId,
            bindings = request.BindingsJsonCanonical
        });

        return intent.Mode switch
        {
            HostResumeIntentMode.OverridePlanChange => new RuntimeRunOptions(ResumeMode.OverridePlanChange, decisionPayload, AllowPlanChangeOverride: true),
            HostResumeIntentMode.DiscardWaitingStartOver => new RuntimeRunOptions(ResumeMode.DiscardWaitingStartOver, decisionPayload, DiscardWaiting: true),
            HostResumeIntentMode.InjectDecision => new RuntimeRunOptions(ResumeMode.InjectDecision, decisionPayload),
            _ => new RuntimeRunOptions(ResumeMode.None, decisionPayload)
        };
    }

    public ExecutionEnvelope Resume(BuildPlan plan, DecisionInjectionRequest request, HostResumeIntent intent)
    {
        return _orchestrator.Run(plan, CreateResumeOptions(request, intent));
    }

    public ExecutionEnvelope Resume(BuildPlan plan, DecisionInjectionRequest request)
    {
        return _orchestrator.Run(plan, CreateResumeOptions(request, new HostResumeIntent(HostResumeIntentMode.InjectDecision)));
    }
}
