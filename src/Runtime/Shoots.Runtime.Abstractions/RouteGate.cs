using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteGate
{
    public static IRuntimeNarrator? Narrator { get; set; }

    public static bool TryAdvance(
        BuildPlan plan,
        RoutingState state,
        RouteDecision? decision,
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

        if (string.IsNullOrWhiteSpace(state.IntentToken.ConstraintsHash) ||
            string.IsNullOrWhiteSpace(state.IntentToken.CurrentStateHash))
        {
            error = new RuntimeError(
                "route_intent_token_missing",
                "Intent token is required to advance routing.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (state.CurrentRouteIndex == 0 && routeStep.Intent == RouteIntent.Terminate)
        {
            error = new RuntimeError(
                "route_start_terminate",
                "First route step cannot be terminate.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, error);
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
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var expectedToken = RouteIntentTokenFactory.Create(plan.Request.WorkOrder, routeStep);
        var expectedHash = RouteIntentTokenFactory.ComputeTokenHash(expectedToken);
        var actualHash = RouteIntentTokenFactory.ComputeTokenHash(state.IntentToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_intent_token_mismatch",
                "Intent token does not match the current routing state.");
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
            narrator?.OnNodeHalted(state, routeStep, error);
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
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (state.CurrentRouteIndex == 0 && rule.NodeKind != MermaidNodeKind.Start)
        {
            error = new RuntimeError(
                "route_start_node_invalid",
                "First route step must be a Start node.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (state.CurrentRouteIndex != 0 && rule.NodeKind == MermaidNodeKind.Start)
        {
            error = new RuntimeError(
                "route_start_node_misplaced",
                "Start node can only appear at the first route step.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var allowedNextNodes = rule.AllowedNextNodes ?? Array.Empty<string>();

        narrator?.OnRouteEntered(state, routeStep, state.IntentToken, allowedNextNodes);
        narrator?.OnNodeEntered(state, routeStep, state.IntentToken, allowedNextNodes);

        if (!IsIntentCompatible(routeStep.Intent, rule.NodeKind))
        {
            error = new RuntimeError(
                "route_intent_node_kind_mismatch",
                "Route intent does not match node kind.",
                new { routeStep.Intent, rule.NodeKind });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (rule.NodeKind == MermaidNodeKind.Terminate && allowedNextNodes.Count > 0)
        {
            error = new RuntimeError(
                "route_terminal_outbound",
                "Terminate nodes cannot have outbound edges.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (rule.NodeKind != MermaidNodeKind.Terminate && allowedNextNodes.Count == 0)
        {
            error = new RuntimeError(
                "route_outbound_missing",
                "Non-terminal nodes must declare at least one outbound edge.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var snapshot = registry.GetSnapshot();

        if (plan.Steps.Any(step => step is ToolBuildStep))
        {
            if (!ToolAuthorityValidator.TryValidateSnapshot(plan, snapshot, out error))
            {
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error ?? RuntimeError.Internal("Tool authority validation failed"));
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
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
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
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
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
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (decision?.ToolSelection is not null && decision.ToolSelection.ToolId != routeStep.ToolInvocation.ToolId)
            {
                error = new RuntimeError(
                    "route_tool_invocation_conflict",
                    "Decision tool does not match route tool invocation.",
                    new { decision = decision.ToolSelection.ToolId.Value, invocation = routeStep.ToolInvocation.ToolId.Value });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
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
                narrator?.OnNodeHalted(state, routeStep, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            var effectiveDecision = decision?.ToolSelection
                ?? (routeStep.ToolInvocation is null
                    ? null
                    : new ToolSelectionDecision(routeStep.ToolInvocation.ToolId, routeStep.ToolInvocation.Bindings));

            if (effectiveDecision is null)
            {
                if (decision is not null)
                {
                    error = new RuntimeError(
                        "route_tool_decision_missing",
                        "Tool selection decision is required for SelectTool intent.",
                        routeStep.NodeId);
                    nextState = state with { Status = RoutingStatus.Halted };
                    narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                    narrator?.OnHalted(nextState, error);
                    return false;
                }

                nextState = state with { Status = RoutingStatus.Waiting };
                narrator?.OnDecisionRequired(nextState, routeStep, state.IntentToken, allowedNextNodes);
                error = null;
                return false;
            }

            if (!TryValidateToolSelection(plan, snapshot, effectiveDecision, out error))
            {
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            narrator?.OnDecisionAccepted(state, routeStep, state.IntentToken, allowedNextNodes);
        }
        else
        {
            if (decision?.ToolSelection is not null)
            {
                error = new RuntimeError(
                    "route_decision_unexpected",
                    "Decision output is only allowed for SelectTool intent.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }
        }

        if (decision is not null)
        {
            if (string.IsNullOrWhiteSpace(decision.IntentToken.ConstraintsHash) ||
                string.IsNullOrWhiteSpace(decision.IntentToken.CurrentStateHash))
            {
                error = new RuntimeError(
                    "route_intent_token_missing",
                    "Decision intent token is required.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            var decisionHash = RouteIntentTokenFactory.ComputeTokenHash(decision.IntentToken);
            if (!string.Equals(decisionHash, actualHash, StringComparison.Ordinal))
            {
                error = new RuntimeError(
                    "route_intent_token_mismatch",
                    "Decision intent token does not match routing state.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }
        }

        if (decision is not null && decision.ObservedIntent != state.CurrentRouteIntent)
        {
            error = new RuntimeError(
                "route_intent_observed_mismatch",
                "Decision intent does not match routing state.",
                new { decision.ObservedIntent, state.CurrentRouteIntent });
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (allowedNextNodes.Count == 0)
        {
            if (routeStep.Intent != RouteIntent.Terminate)
            {
                error = new RuntimeError(
                    "route_terminal_missing",
                    "Terminal route step must use Terminate intent.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (decision is not null)
            {
                error = new RuntimeError(
                    "route_decision_unexpected",
                    "Decision output is not allowed for terminal nodes.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            nextState = state with { Status = RoutingStatus.Completed };
            narrator?.OnCompleted(nextState, routeStep, state.IntentToken, allowedNextNodes);
            error = null;
            return true;
        }

        string? nextNodeId = null;

        if (decision is not null && string.IsNullOrWhiteSpace(decision.SuggestedNextNodeId))
        {
            error = new RuntimeError(
                "route_next_node_missing",
                "Decision must include next node id.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (allowedNextNodes.Count == 1)
        {
            if (decision is not null &&
                !string.Equals(decision.SuggestedNextNodeId, allowedNextNodes[0], StringComparison.Ordinal))
            {
                error = new RuntimeError(
                    "route_next_node_invalid",
                    "Decision next node is not allowed.",
                    new { decision = decision.SuggestedNextNodeId, allowed = allowedNextNodes[0] });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            nextNodeId = allowedNextNodes[0];
        }
        else
        {
            if (decision is null)
            {
                if (routeStep.Owner == DecisionOwner.Ai)
                {
                    nextState = state with { Status = RoutingStatus.Waiting };
                    narrator?.OnDecisionRequired(nextState, routeStep, state.IntentToken, allowedNextNodes);
                    error = null;
                    return false;
                }

                error = new RuntimeError(
                    "route_decision_missing",
                    "Decision is required to choose the next node.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(decision.SuggestedNextNodeId))
            {
                error = new RuntimeError(
                    "route_next_node_missing",
                    "Decision must include next node id.",
                    routeStep.NodeId);
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (!allowedNextNodes.Any(node => string.Equals(node, decision.SuggestedNextNodeId, StringComparison.Ordinal)))
            {
                error = new RuntimeError(
                    "route_next_node_invalid",
                    "Decision next node is not allowed.",
                    new { decision = decision.SuggestedNextNodeId, allowed = allowedNextNodes });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            nextNodeId = decision.SuggestedNextNodeId;
        }

        narrator?.OnNodeTransitionChosen(state, routeStep, state.IntentToken, allowedNextNodes, nextNodeId);

        var nextIndex = FindRouteStepIndex(plan.Steps, nextNodeId);
        if (nextIndex < 0 || plan.Steps[nextIndex] is not RouteStep nextStep)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Next route step is invalid.",
                nextNodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var nextToken = RouteIntentTokenFactory.Create(plan.Request.WorkOrder!, nextStep);

        nextState = state with
        {
            IntentToken = nextToken,
            CurrentRouteIndex = nextIndex,
            CurrentRouteIntent = nextStep.Intent,
            Status = RoutingStatus.Pending
        };
        narrator?.OnNodeAdvanced(nextState, routeStep, state.IntentToken, allowedNextNodes, nextNodeId);
        error = null;
        return true;
    }

    private static bool IsIntentCompatible(RouteIntent intent, MermaidNodeKind nodeKind)
    {
        if (nodeKind == MermaidNodeKind.Terminate)
            return intent == RouteIntent.Terminate;

        return intent != RouteIntent.Terminate;
    }

    private static int FindRouteStepIndex(
        IReadOnlyList<BuildStep> steps,
        string nodeId)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] is RouteStep step &&
                string.Equals(step.NodeId, nodeId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryValidateToolSelection(
        BuildPlan plan,
        IReadOnlyList<ToolRegistryEntry> snapshot,
        ToolSelectionDecision selection,
        out RuntimeError? error)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

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
