using System;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class ToolAuthorityValidator
{
    public static bool TryValidate(
        BuildPlan plan,
        IToolRegistry registry,
        out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        if (plan.Authority is null)
            throw new ArgumentException("plan authority is required", nameof(plan));

        foreach (var step in plan.Steps)
        {
            if (step is not ToolBuildStep toolStep)
                continue;

            var entry = registry.GetTool(toolStep.ToolId);
            if (entry is null)
            {
                error = new RuntimeError(
                    "tool_missing",
                    $"Tool '{toolStep.ToolId.Value}' is not registered.");
                return false;
            }

            if (!MeetsAuthority(plan.Authority, entry.Spec.RequiredAuthority))
            {
                error = new RuntimeError(
                    "tool_authority_denied",
                    $"Tool '{toolStep.ToolId.Value}' requires '{entry.Spec.RequiredAuthority.RequiredProviderKind}' authority.");
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool MeetsAuthority(
        DelegationAuthority authority,
        ToolAuthorityScope required)
    {
        if (authority.Kind < required.RequiredProviderKind)
            return false;

        var capabilities = GetCapabilities(authority.Kind);
        return (capabilities & required.RequiredCapabilities) == required.RequiredCapabilities;
    }

    private static ProviderCapabilities GetCapabilities(ProviderKind kind)
    {
        return ProviderCapabilities.Plan | ProviderCapabilities.Execute | ProviderCapabilities.Artifacts;
    }
}
