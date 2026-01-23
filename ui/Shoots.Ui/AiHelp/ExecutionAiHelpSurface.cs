using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.AiHelp;

public sealed class ExecutionAiHelpSurface : IAiHelpSurface
{
    private readonly BuildPlan? _plan;
    private readonly ExecutionState _state;
    private readonly string _startReason;

    public ExecutionAiHelpSurface(BuildPlan? plan, ExecutionState state, string startReason)
    {
        _plan = plan;
        _state = state;
        _startReason = startReason;
    }

    public string SurfaceId => "execution";

    public string SurfaceKind => "Execution";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Execution),
        new AiIntentDescriptor(AiIntentType.Predict, AiIntentScope.Execution),
        new AiIntentDescriptor(AiIntentType.Risk, AiIntentScope.Execution)
    };

    public string DescribeContext()
    {
        if (_plan is null)
            return "No execution plan is loaded.";

        return $"Plan {_plan.PlanId} with {_plan.Steps.Count} steps and {_plan.Artifacts.Count} artifacts.";
    }

    public string DescribeCapabilities()
        => $"Execution state: {_state}.";

    public string DescribeConstraints()
        => $"Start readiness: {_startReason}.";
}
