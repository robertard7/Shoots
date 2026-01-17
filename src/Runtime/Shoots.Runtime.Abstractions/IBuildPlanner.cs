namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Creates deterministic plans from canonical build requests.
/// </summary>
public interface IBuildPlanner
{
    BuildPlan Plan(BuildRequest request);
}
