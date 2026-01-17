using System;
using System.Linq;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Loader;

public static class AiStepValidator
{
    public static bool ContainsAiSteps(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        return plan.Steps.Any(step => step is AiBuildStep);
    }

    public static bool TryValidate(BuildPlan plan, out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        if (plan.Authority is null)
        {
            error = new RuntimeError("missing_authority", "Plan authority is required.");
            return false;
        }

        foreach (var step in plan.Steps)
        {
            if (step is not AiBuildStep aiStep)
                continue;

            if (string.IsNullOrWhiteSpace(aiStep.Prompt))
            {
                error = new RuntimeError("ai_prompt_missing", "AI prompt text is required.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(aiStep.OutputSchema))
            {
                error = new RuntimeError("ai_schema_missing", "AI output schema is required.");
                return false;
            }
        }

        error = null;
        return true;
    }
}
