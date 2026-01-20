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

        if (string.IsNullOrWhiteSpace(plan.GraphStructureHash))
        {
            error = new RuntimeError(
                "route_graph_hash_missing",
                "Graph structure hash is required to advance routing.");
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

        if (string.IsNullOrWhiteSpace(state.CurrentNodeId))
        {
            error = new RuntimeError(
                "route_node_missing",
                "Current node id is required to advance routing.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var routeStep = ResolveRouteStep(plan.Steps, state.CurrentNodeId);
        if (routeStep is null)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Route step is required for the current node.",
                state.CurrentNodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.IntentToken.WorkOrderId.Value) ||
            string.IsNullOrWhiteSpace(state.IntentToken.CurrentNodeId) ||
            state.IntentToken.AllowedNextNodeIds is null ||
            string.IsNullOrWhiteSpace(state.IntentToken.GraphStructureHash))
        {
            error = new RuntimeError(
                "route_intent_token_missing",
                "Intent token is required to advance routing.");
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
            narrator?.OnNodeHalted(state, routeStep, error);
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

        var expectedToken = RouteIntentTokenFactory.Create(plan, rule);
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

        if (!string.Equals(state.IntentToken.WorkOrderId.Value, state.WorkOrderId.Value, StringComparison.Ordinal) ||
            !string.Equals(state.IntentToken.CurrentNodeId, state.CurrentNodeId, StringComparison.Ordinal) ||
            !string.Equals(state.IntentToken.GraphStructureHash, plan.GraphStructureHash, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_intent_token_mismatch",
                "Intent token does not match routing state.");
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var ruleAllowedNodes = rule.AllowedNextNodes ?? Array.Empty<string>();
        if (!state.IntentToken.AllowedNextNodeIds.SequenceEqual(ruleAllowedNodes, StringComparer.Ordinal))
        {
            error = new RuntimeError(
                "route_intent_token_mismatch",
                "Intent token allowed nodes do not match route rule.");
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
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (rule.NodeKind == MermaidNodeKind.Start && state.Status == RoutingStatus.Pending)
            narrator?.OnWorkOrderReceived(plan.Request.WorkOrder);

        if (rule.NodeKind == MermaidNodeKind.Start && routeStep.Intent == RouteIntent.Terminate)
        {
            error = new RuntimeError(
                "route_start_terminate",
                "Start node cannot use Terminate intent.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var allowedNextNodes = ruleAllowedNodes;

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

        if (rule.NodeKind == MermaidNodeKind.Terminal && allowedNextNodes.Count > 0)
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

        if (rule.NodeKind != MermaidNodeKind.Terminal && allowedNextNodes.Count == 0)
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

        if (rule.NodeKind == MermaidNodeKind.Gate && allowedNextNodes.Count < 2)
        {
            error = new RuntimeError(
                "route_gate_missing",
                "Gate nodes must declare multiple outbound edges.",
                routeStep.NodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (rule.NodeKind != MermaidNodeKind.Gate && allowedNextNodes.Count > 1)
        {
            error = new RuntimeError(
                "route_gate_invalid",
                "Multiple outbound edges require Gate node kind.",
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

        if (decision is not null)
        {
            var hasNextNode = !string.IsNullOrWhiteSpace(decision.NextNodeId);
            var hasToolSelection = decision.ToolSelection is not null;
            if (hasNextNode == hasToolSelection)
            {
                error = new RuntimeError(
                    "route_decision_invalid",
                    "Decision must include exactly one of next node id or tool selection.");
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

            if (decision?.NextNodeId is not null)
            {
                error = new RuntimeError(
                    "route_decision_unexpected",
                    "SelectTool decisions must not include next node id.");
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            var effectiveDecision = decision?.ToolSelection
                ?? (routeStep.ToolInvocation is null
                    ? null
                    : new ToolSelectionDecision(routeStep.ToolInvocation.ToolId, routeStep.ToolInvocation.Bindings));

            if (effectiveDecision is null)
            {
                if (state.Status == RoutingStatus.Waiting || decision is not null)
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

        if (routeStep.Intent == RouteIntent.SelectTool && allowedNextNodes.Count > 1)
        {
            error = new RuntimeError(
                "route_next_node_ambiguous",
                "SelectTool nodes must not branch to multiple next nodes.",
                routeStep.NodeId);
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

        if (allowedNextNodes.Count == 1)
        {
            if (decision?.NextNodeId is not null &&
                !string.Equals(decision.NextNodeId, allowedNextNodes[0], StringComparison.Ordinal))
            {
                error = new RuntimeError(
                    "route_next_node_invalid",
                    "Decision next node is not allowed.",
                    new { decision = decision.NextNodeId, allowed = allowedNextNodes[0] });
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
                if (state.Status == RoutingStatus.Waiting)
                {
                    error = new RuntimeError(
                        "route_decision_missing",
                        "Decision is required to choose the next node.",
                        routeStep.NodeId);
                }
                else if (routeStep.Owner == DecisionOwner.Ai)
                {
                    nextState = state with { Status = RoutingStatus.Waiting };
                    narrator?.OnDecisionRequired(nextState, routeStep, state.IntentToken, allowedNextNodes);
                    error = null;
                    return false;
                }
                else
                {
                    error = new RuntimeError(
                        "route_decision_missing",
                        "Decision is required to choose the next node.",
                        routeStep.NodeId);
                }
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(decision.NextNodeId))
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

            if (!allowedNextNodes.Any(node => string.Equals(node, decision.NextNodeId, StringComparison.Ordinal)))
            {
                error = new RuntimeError(
                    "route_next_node_invalid",
                    "Decision next node is not allowed.",
                    new { decision = decision.NextNodeId, allowed = allowedNextNodes });
                nextState = state with { Status = RoutingStatus.Halted };
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                narrator?.OnHalted(nextState, error);
                return false;
            }

            nextNodeId = decision.NextNodeId;
        }

        var decisionSource = decision?.NextNodeId is not null
            ? RoutingDecisionSource.Provider
            : routeStep.Owner is DecisionOwner.Rule or DecisionOwner.Runtime
                ? RoutingDecisionSource.Rule
                : RoutingDecisionSource.Mermaid;

        narrator?.OnNodeTransitionChosen(state, routeStep, state.IntentToken, allowedNextNodes, nextNodeId, decisionSource);

        var nextStep = ResolveRouteStep(plan.Steps, nextNodeId);
        if (nextStep is null)
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

        var nextRule = plan.Request.RouteRules
            .FirstOrDefault(candidate => string.Equals(candidate.NodeId, nextNodeId, StringComparison.Ordinal));
        if (nextRule is null)
        {
            error = new RuntimeError(
                "route_rule_missing",
                "Route rule is missing for the next node.",
                nextNodeId);
            nextState = state with { Status = RoutingStatus.Halted };
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var nextToken = RouteIntentTokenFactory.Create(plan, nextRule);

        nextState = state with
        {
            IntentToken = nextToken,
            CurrentNodeId = nextNodeId,
            CurrentRouteIntent = nextStep.Intent,
            Status = RoutingStatus.Pending
        };
        narrator?.OnNodeAdvanced(nextState, routeStep, state.IntentToken, allowedNextNodes, nextNodeId, decisionSource);
        error = null;
        return true;
    }

    private static bool IsIntentCompatible(RouteIntent intent, MermaidNodeKind nodeKind)
    {
        return nodeKind switch
        {
            MermaidNodeKind.Start => intent != RouteIntent.Terminate,
            MermaidNodeKind.Route => intent != RouteIntent.Terminate,
            MermaidNodeKind.Gate => intent != RouteIntent.Terminate,
            MermaidNodeKind.Tool => intent == RouteIntent.SelectTool,
            MermaidNodeKind.Terminal => intent == RouteIntent.Terminate,
            _ => false
        };
    }

    private static RouteStep? ResolveRouteStep(
        IReadOnlyList<BuildStep> steps,
        string nodeId)
    {
        return steps
            .OfType<RouteStep>()
            .FirstOrDefault(step => string.Equals(step.NodeId, nodeId, StringComparison.Ordinal));
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
