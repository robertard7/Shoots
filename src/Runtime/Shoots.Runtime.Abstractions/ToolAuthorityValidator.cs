using System;
using System.Linq;
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
        {
            error = new RuntimeError(
                "tool_authority_missing",
                "Plan authority is required for tool validation.");
            return false;
        }

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

    public static bool TryValidateSnapshot(
        BuildPlan plan,
        IReadOnlyList<ToolRegistryEntry> snapshot,
        out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        if (plan.Authority is null)
        {
            error = new RuntimeError(
                "tool_authority_missing",
                "Plan authority is required for tool validation.");
            return false;
        }

        foreach (var step in plan.Steps)
        {
            if (step is not ToolBuildStep toolStep)
                continue;

            var entry = snapshot.FirstOrDefault(candidate => candidate.Spec.ToolId == toolStep.ToolId);
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

    public static bool TryValidateAuthority(
        DelegationAuthority authority,
        ToolAuthorityScope required,
        out RuntimeError? error)
    {
        if (authority is null)
            throw new ArgumentNullException(nameof(authority));
        if (required is null)
            throw new ArgumentNullException(nameof(required));

        if (MeetsAuthority(authority, required))
        {
            error = null;
            return true;
        }

        error = new RuntimeError(
            "tool_authority_denied",
            $"Tool requires '{required.RequiredProviderKind}' authority.");
        return false;
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
