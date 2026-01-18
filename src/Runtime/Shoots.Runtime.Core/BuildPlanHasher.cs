using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public static class BuildPlanHasher
{
    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts)
    {
        return Shoots.Runtime.Abstractions.BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts);
    }

    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts,
        ToolResult? toolResult)
    {
        return Shoots.Runtime.Abstractions.BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts, toolResult);
    }
}
