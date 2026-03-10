#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;

namespace Shoots.UI.Services;

public sealed class NullExecutionCommandService : IExecutionCommandService
{
    public Task<ExecutionStartResult> StartAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default)
    {
        _ = options;
        _ = ct;

        // Use whichever BuildPlan property exists in your Contracts.Core.
        // Common shapes: plan.PlanId / plan.Id. We'll handle both patterns safely.
        var planId =
            TryGetPlanId(plan)
            ?? "unknown-plan";

        var planHash =
            TryGetPlanHash(plan)
            ?? "unknown-hash";

        return Task.FromResult(new ExecutionStartResult(
            Outcome: ExecutionOutcome.Completed,
            WorkOrderId: plan.Request.WorkOrder.Id.Value,
            PlanId: planId,
            PlanHash: planHash,
            Message: "Simulated host execution via NullExecutionCommandService."
        ));
    }

    public Task CancelAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
    }

    public Task<ExecutionStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        _ = ct;

        return Task.FromResult(new ExecutionStatusSnapshot(
            Version: new ExecutionVersionInfo(0, 0, 0, "null"),
            PolicyHash: "null",
            StateLabel: "Not configured"
        ));
    }

    private static string? TryGetPlanId(BuildPlan plan)
    {
        // If BuildPlan has PlanId property:
        var prop = plan.GetType().GetProperty("PlanId");
        if (prop?.GetValue(plan) is string s1 && !string.IsNullOrWhiteSpace(s1))
            return s1;

        // If BuildPlan has Id property:
        prop = plan.GetType().GetProperty("Id");
        if (prop?.GetValue(plan) is string s2 && !string.IsNullOrWhiteSpace(s2))
            return s2;

        // If BuildPlan has something like plan.Id.Value:
        if (prop?.GetValue(plan) is object o2)
        {
            var valueProp = o2.GetType().GetProperty("Value");
            if (valueProp?.GetValue(o2) is string s3 && !string.IsNullOrWhiteSpace(s3))
                return s3;
        }

        return null;
    }

    private static string? TryGetPlanHash(BuildPlan plan)
    {
        var prop = plan.GetType().GetProperty("PlanHash");
        if (prop?.GetValue(plan) is string s1 && !string.IsNullOrWhiteSpace(s1))
            return s1;

        // Some shapes use GraphHash or Hash
        prop = plan.GetType().GetProperty("GraphHash");
        if (prop?.GetValue(plan) is string s2 && !string.IsNullOrWhiteSpace(s2))
            return s2;

        prop = plan.GetType().GetProperty("Hash");
        if (prop?.GetValue(plan) is string s3 && !string.IsNullOrWhiteSpace(s3))
            return s3;

        return null;
    }
}
