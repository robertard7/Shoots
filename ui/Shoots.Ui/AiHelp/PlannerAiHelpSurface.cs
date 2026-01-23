using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.AiHelp;

public sealed class PlannerAiHelpSurface : IAiHelpSurface
{
    private readonly BuildPlan? _plan;

    public PlannerAiHelpSurface(BuildPlan? plan)
    {
        _plan = plan;
    }

    public string SurfaceId => "planner";

    public string SurfaceKind => "Planner";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Planner),
        new AiIntentDescriptor(AiIntentType.Compare, AiIntentScope.Planner)
    };

    public string DescribeContext()
        => _plan is null ? "No plan is loaded." : $"Planner view for plan {_plan.PlanId}.";

    public string DescribeCapabilities()
        => _plan is null ? "Planner preview is unavailable." : $"Steps: {_plan.Steps.Count}, artifacts: {_plan.Artifacts.Count}.";

    public string DescribeConstraints()
        => "Planner previews are read-only.";
}
