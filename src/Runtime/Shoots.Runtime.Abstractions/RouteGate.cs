using System;
using System.Linq;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteGate
{
    public static bool TryAdvance(
        BuildPlan plan,
        RouteState state,
        IToolRegistry registry,
        out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));

        if (plan.Request.WorkOrder is null || state.WorkOrder is null)
        {
            error = new RuntimeError(
                "route_workorder_missing",
                "Work order is required to advance routing.");
            return false;
        }

        if (plan.Request.RouteRules is null)
        {
            error = new RuntimeError(
                "route_rules_missing",
                "Route rules are required to advance routing.");
            return false;
        }

        if (!string.Equals(plan.Request.WorkOrder.Id.Value, state.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_mismatch",
                "Work order mismatch between plan and state.",
                new { plan = plan.Request.WorkOrder.Id.Value, state = state.WorkOrder.Id.Value });
            return false;
        }

        if (state.StepIndex < 0 || state.StepIndex >= plan.Steps.Count)
        {
            error = new RuntimeError(
                "route_step_out_of_range",
                "Route step index is out of range.",
                state.StepIndex);
            return false;
        }

        if (plan.Steps[state.StepIndex] is not RouteStep routeStep)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Route step is required at the current index.",
                plan.Steps[state.StepIndex].Id);
            return false;
        }

        if (!string.Equals(routeStep.WorkOrderId.Value, plan.Request.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_step_mismatch",
                "Route step work order does not match plan work order.",
                new { routeStep = routeStep.WorkOrderId.Value, plan = plan.Request.WorkOrder.Id.Value });
            return false;
        }

        var rule = plan.Request.RouteRules.FirstOrDefault(
            candidate => string.Equals(candidate.NodeId, routeStep.NodeId, StringComparison.Ordinal));
        if (rule is null)
        {
            error = new RuntimeError(
                "route_rule_missing",
                "Route rule is missing for the current node.",
                routeStep.NodeId);
            return false;
        }

        if (rule.Intent != routeStep.Intent || rule.Owner != routeStep.Owner)
        {
            error = new RuntimeError(
                "route_rule_mismatch",
                "Route step does not match route rule.",
                new { rule.Intent, rule.Owner, routeStep.Intent, routeStep.Owner });
            return false;
        }

        if (plan.Steps.Any(step => step is ToolBuildStep))
        {
            if (!ToolAuthorityValidator.TryValidate(plan, registry, out error))
                return false;
        }

        if (routeStep.Intent == RouteIntent.SelectTool)
        {
            if (state.ToolSelection is null)
            {
                error = new RuntimeError(
                    "route_decision_required",
                    "Tool selection is required for SelectTool intent.");
                return false;
            }

            if (!TryValidateToolSelection(plan, state.ToolSelection, registry, out error))
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateToolSelection(
        BuildPlan plan,
        ToolSelection selection,
        IToolRegistry registry,
        out RuntimeError? error)
    {
        var entry = registry.GetTool(selection.ToolId);
        if (entry is null)
        {
            error = new RuntimeError(
                "tool_missing",
                $"Tool '{selection.ToolId.Value}' is not registered.");
            return false;
        }

        if (!ToolAuthorityValidator.TryValidateAuthority(plan.Authority, entry.Spec.RequiredAuthority, out error))
            return false;

        if (selection.InputBindings is null)
        {
            error = new RuntimeError(
                "tool_bindings_missing",
                "Tool bindings are required.");
            return false;
        }

        foreach (var input in entry.Spec.Inputs)
        {
            if (input.Required && !selection.InputBindings.ContainsKey(input.Name))
            {
                error = new RuntimeError(
                    "tool_binding_missing",
                    $"Tool binding '{input.Name}' is required.");
                return false;
            }
        }

        foreach (var binding in selection.InputBindings.Keys)
        {
            if (entry.Spec.Inputs.All(input => !string.Equals(input.Name, binding, StringComparison.Ordinal)))
            {
                error = new RuntimeError(
                    "tool_binding_unknown",
                    $"Tool binding '{binding}' is not defined for '{selection.ToolId.Value}'.");
                return false;
            }
        }

        error = null;
        return true;
    }
}
