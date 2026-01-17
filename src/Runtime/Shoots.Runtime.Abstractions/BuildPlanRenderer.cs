using System.Text;
using System.Text.Json;

namespace Shoots.Runtime.Abstractions;

public static class BuildPlanRenderer
{
    public static string RenderText(BuildPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var builder = new StringBuilder();
        builder.AppendLine($"plan={plan.PlanId}");
        builder.AppendLine($"command={plan.Request.CommandId}");
        builder.AppendLine("steps:");
        foreach (var step in plan.Steps)
        {
            builder.AppendLine($"- {step.Id}: {step.Description}");
        }

        builder.AppendLine("artifacts:");
        foreach (var artifact in plan.Artifacts)
        {
            builder.AppendLine($"- {artifact.Id}: {artifact.Description}");
        }

        return builder.ToString();
    }

    public static string RenderJson(BuildPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        return JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        );
    }
}
