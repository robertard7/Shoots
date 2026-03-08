using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed class RuntimePlanner : IPlanner
{
    private readonly IPlanner _fallback;

    public RuntimePlanner(IPlanner? fallback = null)
    {
        _fallback = fallback ?? new DemoPlanner();
    }

    public bool TryBuildPlan(ProjectModel project, out PlanModel plan)
    {
        return _fallback.TryBuildPlan(project, out plan);
    }
}
