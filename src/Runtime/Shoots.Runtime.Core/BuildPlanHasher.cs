using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public static class BuildPlanHasher
{
    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority)
    {
        return Shoots.Runtime.Abstractions.BuildPlanHasher.ComputePlanId(request, authority);
    }
}
