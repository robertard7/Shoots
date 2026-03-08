using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public interface IPlanner
{
    bool TryBuildPlan(ProjectModel project, out PlanModel plan);
}
