using System;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class BuildPlanIdentity
{
    public static string ComputePlanHash(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        return BuildPlanHasher.ComputePlanId(
            plan.Request,
            plan.Authority,
            plan.Steps,
            plan.Artifacts,
            plan.ToolResult);
    }
}
