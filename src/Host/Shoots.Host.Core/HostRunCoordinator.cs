using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Abstractions;
using Shoots.ProviderAdapters.Null;
using Shoots.Runtime.Core;

namespace Shoots.Host.Core;

public sealed class HostRunCoordinator
{
    private readonly RuntimeOrchestrator _orchestrator;

    public HostRunCoordinator(IToolRegistry registry, IAiDecisionProvider aiDecisionProvider, InMemoryRuntimePersistence persistence, IRuntimeNarrator? narrator = null)
    {
        _orchestrator = new RuntimeOrchestrator(
            registry,
            aiDecisionProvider,
            narrator ?? new NullRuntimeNarrator(),
            new NullProviderClient(),
            persistence);
    }

    public ExecutionEnvelope Run(BuildPlan plan, RuntimeRunOptions? options = null)
        => _orchestrator.Run(plan, options);

    public ExecutionEnvelope Resume(BuildPlan plan, DecisionInjectionRequest request)
    {
        var digest = DecisionDigest.Compute(request);
        var options = new RuntimeRunOptions(ResumeMode.InjectDecision, digest);
        return _orchestrator.Run(plan, options);
    }
}
