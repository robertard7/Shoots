using System;
using System.Text;
using System.Linq;
using System.Text.Json;

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
        }

        builder.AppendLine("artifacts:");
        foreach (var artifact in plan.Artifacts)
        {
            builder.Append("- ")
                   .Append(artifact.Id)
                   .Append(": ")
                   .AppendLine(artifact.Description);
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
