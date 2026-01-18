using System;
using System.Text;
using System.Linq;
using System.Text.Json;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class BuildPlanRenderer
{
    // CONTRACT FROZEN
    // Any change here requires updating golden tests.

    public static string RenderText(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        var builder = new StringBuilder(256);

        builder.Append("plan=").AppendLine(plan.PlanId);
        builder.Append("command=").AppendLine(plan.Request.CommandId);
        builder.Append("workorder.id=").AppendLine(plan.Request.WorkOrder.Id.Value);
        builder.Append("workorder.goal=").AppendLine(plan.Request.WorkOrder.Goal);

        builder.Append("authority.provider=").AppendLine(plan.Authority.ProviderId.Value);
        builder.Append("authority.kind=").AppendLine(plan.Authority.Kind.ToString());
        builder.Append("authority.hash=").AppendLine(plan.PlanId);

        builder.Append("delegation.policy=").AppendLine(plan.Authority.PolicyId);
        builder.Append("delegation.allowed=").AppendLine(plan.Authority.AllowsDelegation.ToString());

        builder.AppendLine("steps:");
        foreach (var step in plan.Steps)
        {
            builder.Append("- ")
                   .Append(step.Id)
                   .Append(": ")
                   .AppendLine(step.Description);

            if (step is AiBuildStep aiStep)
            {
                builder.Append("  prompt=").AppendLine(aiStep.Prompt);
                builder.Append("  schema=").AppendLine(aiStep.OutputSchema);
            }

            if (step is ToolBuildStep toolStep)
            {
                builder.Append("  tool=").AppendLine(toolStep.ToolId.Value);
                builder.AppendLine("  inputs:");
                foreach (var binding in toolStep.InputBindings
                             .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Append("    - ")
                           .Append(binding.Key)
                           .Append(": ")
                           .AppendLine(binding.Value?.ToString() ?? "null");
                }

                builder.AppendLine("  outputs:");
                foreach (var output in toolStep.Outputs)
                {
                    builder.Append("    - ")
                           .Append(output.Name)
                           .Append(": ")
                           .Append(output.Type)
                           .Append(" (")
                           .Append(output.Description)
                           .AppendLine(")");
                }
            }

            if (step is RouteStep routeStep)
            {
                builder.Append("  node=").AppendLine(routeStep.NodeId);
                builder.Append("  intent=").AppendLine(routeStep.Intent.ToString());
                builder.Append("  owner=").AppendLine(routeStep.Owner.ToString());
                builder.Append("  workorder=").AppendLine(routeStep.WorkOrderId.Value);

                if (routeStep.ToolInvocation is not null)
                {
                    builder.Append("  tool=").AppendLine(routeStep.ToolInvocation.ToolId.Value);
                    builder.Append("  tool.workorder=").AppendLine(routeStep.ToolInvocation.WorkOrderId.Value);
                    builder.AppendLine("  tool.bindings:");
                    foreach (var binding in routeStep.ToolInvocation.Bindings
                                 .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        builder.Append("    - ")
                               .Append(binding.Key)
                               .Append(": ")
                               .AppendLine(binding.Value?.ToString() ?? "null");
                    }
                }
            }
        }

        builder.AppendLine("artifacts:");
        foreach (var artifact in plan.Artifacts)
        {
            builder.Append("- ")
                   .Append(artifact.Id)
                   .Append(": ")
                   .AppendLine(artifact.Description);
        }

        if (plan.ToolResult is not null)
        {
            builder.AppendLine("tool.result:");
            builder.Append("  id=").AppendLine(plan.ToolResult.ToolId.Value);
            builder.Append("  success=").AppendLine(plan.ToolResult.Success.ToString());
            builder.AppendLine("  outputs:");
            foreach (var output in plan.ToolResult.Outputs
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("    - ")
                       .Append(output.Key)
                       .Append(": ")
                       .AppendLine(output.Value?.ToString() ?? "null");
            }
        }

        return builder.ToString();
    }

    public static string RenderJson(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        return JsonSerializer.Serialize(
            plan,
            BuildPlanJsonContext.Default.BuildPlan
        );
    }
}
