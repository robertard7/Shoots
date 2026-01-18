using System;
using System.Linq;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteGate
{
    public static IRuntimeNarrator? Narrator { get; set; }

    public static bool TryAdvance(
        BuildPlan plan,
        RoutingState state,
        ToolSelectionDecision? decision,
        IToolRegistry registry,
        out RoutingState nextState,
        out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));

        var narrator = Narrator;

        if (state.Status == RoutingStatus.Completed || state.Status == RoutingStatus.Halted)
        {
            error = new RuntimeError(
                "route_state_final",
                "Routing state is final and cannot advance.",
                state.Status.ToString());
            nextState = state;
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (plan.Request.WorkOrder is null)
        {
            error = new RuntimeError(
                "route_workorder_missing",
                "Work order is required to advance routing.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (plan.Request.RouteRules is null)
        {
            error = new RuntimeError(
                "route_rules_missing",
                "Route rules are required to advance routing.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.WorkOrderId.Value))
        {
            error = new RuntimeError(
                "route_workorder_missing",
                "Work order id is required to advance routing.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (!string.Equals(plan.Request.WorkOrder.Id.Value, state.WorkOrderId.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_mismatch",
                "Work order mismatch between plan and state.",
                new { plan = plan.Request.WorkOrder.Id.Value, state = state.WorkOrderId.Value });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (state.CurrentRouteIndex < 0 || state.CurrentRouteIndex >= plan.Steps.Count)
        {
            error = new RuntimeError(
                "route_step_out_of_range",
                "Route step index is out of range.",
                state.CurrentRouteIndex);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (plan.Steps[state.CurrentRouteIndex] is not RouteStep routeStep)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Route step is required at the current index.",
                plan.Steps[state.CurrentRouteIndex].Id);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (state.CurrentRouteIndex == 0 && state.Status == RoutingStatus.Pending)
            narrator?.OnWorkOrderReceived(plan.Request.WorkOrder);

        narrator?.OnRouteEntered(state, routeStep);

        if (state.CurrentRouteIndex == 0 && routeStep.Intent == RouteIntent.Terminate)
        {
            error = new RuntimeError(
                "route_start_terminate",
                "First route step cannot be terminate.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (!string.Equals(routeStep.WorkOrderId.Value, plan.Request.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_step_mismatch",
                "Route step work order does not match plan work order.",
                new { routeStep = routeStep.WorkOrderId.Value, plan = plan.Request.WorkOrder.Id.Value });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
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
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (rule.Intent != routeStep.Intent || rule.Owner != routeStep.Owner)
        {
            error = new RuntimeError(
                "route_rule_mismatch",
                "Route step does not match route rule.",
                new { rule.Intent, rule.Owner, routeStep.Intent, routeStep.Owner });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var snapshot = registry.GetSnapshot();

        if (plan.Steps.Any(step => step is ToolBuildStep))
        {
            if (!ToolAuthorityValidator.TryValidateSnapshot(plan, snapshot, out error))
            {
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }
        }

        if (state.CurrentRouteIntent != routeStep.Intent)
        {
            error = new RuntimeError(
                "route_intent_mismatch",
                "Routing state intent does not match current route step.",
                new { state.CurrentRouteIntent, routeStep.Intent });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (routeStep.ToolInvocation is not null)
        {
            if (routeStep.Intent != RouteIntent.SelectTool)
            {
                error = new RuntimeError(
                    "route_tool_invocation_invalid",
                    "Tool invocation is only allowed for SelectTool intent.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (!string.Equals(routeStep.ToolInvocation.WorkOrderId.Value, state.WorkOrderId.Value, StringComparison.Ordinal))
            {
                error = new RuntimeError(
                    "route_tool_invocation_mismatch",
                    "Tool invocation work order does not match routing state.",
                    new { invocation = routeStep.ToolInvocation.WorkOrderId.Value, state = state.WorkOrderId.Value });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (decision is not null && decision.ToolId != routeStep.ToolInvocation.ToolId)
            {
                error = new RuntimeError(
                    "route_tool_invocation_conflict",
                    "Decision tool does not match route tool invocation.",
                    new { decision = decision.ToolId.Value, invocation = routeStep.ToolInvocation.ToolId.Value });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }
        }

        if (routeStep.Intent == RouteIntent.SelectTool)
        {
            if (routeStep.Owner != DecisionOwner.Ai)
            {
                error = new RuntimeError(
                    "route_owner_invalid",
                    "SelectTool intent requires Ai decision ownership.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }

            var effectiveDecision = decision
                ?? (routeStep.ToolInvocation is null
                    ? null
                    : new ToolSelectionDecision(routeStep.ToolInvocation.ToolId, routeStep.ToolInvocation.Bindings));

            if (effectiveDecision is null)
            {
                nextState = state with { Status = RoutingStatus.Waiting };
                narrator?.OnDecisionRequired(nextState, routeStep);
                error = null;
                return false;
            }

            if (!TryValidateToolSelection(snapshot, effectiveDecision, out error))
            {
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }

            narrator?.OnDecisionAccepted(state, routeStep);
        }
        else
        {
            if (decision is not null)
            {
                error = new RuntimeError(
                    "route_decision_unexpected",
                    "Decision output is only allowed for SelectTool intent.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }
        }

        var isTerminalStep = state.CurrentRouteIndex >= plan.Steps.Count - 1;
        if (routeStep.Intent == RouteIntent.Terminate)
        {
            if (!isTerminalStep)
            {
                error = new RuntimeError(
                    "route_terminate_not_terminal",
                    "Terminate intent must be at a terminal route step.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnHalted(nextState, error);
                return false;
            }

            nextState = state with { Status = RoutingStatus.Completed };
            narrator?.OnCompleted(nextState, routeStep);
            error = null;
            return true;
        }

        if (isTerminalStep)
        {
            error = new RuntimeError(
                "route_terminal_missing",
                "Terminal route step must use Terminate intent.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var nextIndex = state.CurrentRouteIndex + 1;
        if (nextIndex >= plan.Steps.Count || plan.Steps[nextIndex] is not RouteStep nextStep)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Next route step is invalid.",
                nextIndex);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        nextState = state with
        {
            CurrentRouteIndex = nextIndex,
            CurrentRouteIntent = nextStep.Intent,
            Status = RoutingStatus.Pending
        };
        error = null;
        return true;
    }

    private static bool TryValidateToolSelection(
        IReadOnlyList<ToolRegistryEntry> snapshot,
        ToolSelectionDecision selection,
        out RuntimeError? error)
    {
        var entry = snapshot.FirstOrDefault(candidate => candidate.Spec.ToolId == selection.ToolId);
        if (entry is null)
        {
            error = new RuntimeError(
                "tool_missing",
                $"Tool '{selection.ToolId.Value}' is not registered.");
            return false;
        }

        if (!ToolAuthorityValidator.TryValidateAuthority(plan.Authority, entry.Spec.RequiredAuthority, out error))
            return false;

        if (selection.Bindings is null)
        {
            error = new RuntimeError(
                "tool_bindings_missing",
                "Tool bindings are required.");
            return false;
        }

        foreach (var input in entry.Spec.Inputs)
        {
            if (input.Required && !selection.Bindings.ContainsKey(input.Name))
            {
                error = new RuntimeError(
                    "tool_binding_missing",
                    $"Tool binding '{input.Name}' is required.");
                return false;
            }
        }

        foreach (var binding in selection.Bindings.Keys)
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
