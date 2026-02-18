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
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var narrator = Narrator;

        // 1) Final states
        if (state.Status is RoutingStatus.Completed or RoutingStatus.Halted)
        {
            error = new RuntimeError(
                "route_state_final",
                "Routing state is final and cannot advance.",
                state.Status.ToString());

            nextState = state;
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 2) Required plan fields
        if (plan.Request.WorkOrder is null)
        {
            error = new RuntimeError(
                "route_workorder_missing",
                "Work order is required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (plan.Request.RouteRules is null)
        {
            error = new RuntimeError(
                "route_rules_missing",
                "Route rules are required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(plan.GraphStructureHash))
        {
            error = new RuntimeError(
                "route_graph_hash_missing",
                "Graph structure hash is required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.WorkOrderId.Value))
        {
            error = new RuntimeError(
                "route_workorder_missing",
                "Work order id is required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (!string.Equals(plan.Request.WorkOrder.Id.Value, state.WorkOrderId.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_mismatch",
                "Work order mismatch between plan and state.",
                new
                {
                    PlanWorkOrder = plan.Request.WorkOrder.Id.Value,
                    StateWorkOrder = state.WorkOrderId.Value
                });

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.CurrentNodeId))
        {
            error = new RuntimeError(
                "route_node_missing",
                "Current node id is required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 3) Resolve current step
        var routeStep = ResolveRouteStep(plan.Steps, state.CurrentNodeId);
        if (routeStep is null)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Route step is required for the current node.",
                state.CurrentNodeId);

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 4) Intent token presence
        if (string.IsNullOrWhiteSpace(state.IntentToken.WorkOrderId.Value) ||
            string.IsNullOrWhiteSpace(state.IntentToken.CurrentNodeId) ||
            state.IntentToken.AllowedNextNodeIds is null ||
            string.IsNullOrWhiteSpace(state.IntentToken.GraphStructureHash))
        {
            error = new RuntimeError(
                "route_intent_token_missing",
                "Intent token is required to advance routing.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, Array.Empty<string>(), error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 5) Step workorder match
        if (!string.Equals(routeStep.WorkOrderId.Value, plan.Request.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_step_mismatch",
                "Route step work order does not match plan work order.",
                new
                {
                    StepWorkOrder = routeStep.WorkOrderId.Value,
                    PlanWorkOrder = plan.Request.WorkOrder.Id.Value
                });

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, Array.Empty<string>(), error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 6) Resolve rule
        var rule = plan.Request.RouteRules.FirstOrDefault(
            r => string.Equals(r.NodeId, routeStep.NodeId, StringComparison.Ordinal));

        if (rule is null)
        {
            error = new RuntimeError(
                "route_rule_missing",
                "Route rule is missing for the current node.",
                routeStep.NodeId);

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, Array.Empty<string>(), error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var allowedNextNodes = rule.AllowedNextNodes ?? Array.Empty<string>();

        if (routeStep.Intent != RouteIntent.SelectTool && decision?.ToolSelection is not null)
        {
            error = new RuntimeError(
                "route_decision_unexpected",
                "Tool selection decision is only valid for SelectTool route steps.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (routeStep.Intent == RouteIntent.SelectTool && rule.Owner != DecisionOwner.Ai)
        {
            error = new RuntimeError(
                "route_owner_invalid",
                "SelectTool route steps must be owned by Ai.",
                rule.Owner.ToString());

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 7) Token hash match
        var expectedToken = RouteIntentTokenFactory.Create(plan, rule);
        var expectedHash = RouteIntentTokenFactory.ComputeTokenHash(expectedToken);
        var actualHash = RouteIntentTokenFactory.ComputeTokenHash(state.IntentToken);

        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_intent_token_mismatch",
                "Intent token does not match the current routing state.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        narrator?.OnRouteEntered(state, routeStep, state.IntentToken, allowedNextNodes);
        narrator?.OnNodeEntered(state, routeStep, state.IntentToken, allowedNextNodes);

        // 8) Intent compatibility
        if (!IsIntentCompatible(routeStep.Intent, rule.NodeKind))
        {
            error = new RuntimeError(
                "route_intent_node_kind_mismatch",
                "Route intent does not match node kind.",
                new { routeStep.Intent, rule.NodeKind });

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 9) Tool authority validation (only if tool build steps exist)
        var snapshot = registry.GetSnapshot();

        if (plan.Steps.Any(s => s is ToolBuildStep) &&
            !ToolAuthorityValidator.TryValidateSnapshot(plan, snapshot, out error))
        {
            error ??= RuntimeError.Internal("Tool authority validation failed");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 10) SelectTool handling (policy-driven)
        if (routeStep.Intent == RouteIntent.SelectTool)
        {
            var explicitDecision = decision?.ToolSelection;
            var invocationDecision =
                routeStep.ToolInvocation is null
                    ? null
                    : new ToolSelectionDecision(
                        routeStep.ToolInvocation.ToolId,
                        routeStep.ToolInvocation.Bindings);

            ToolSelectionDecision? effectiveDecision = explicitDecision ?? invocationDecision;
            if (effectiveDecision is null)
            {
                switch (rule.DecisionPolicy)
                {
                    case DecisionPolicy.Bypass when rule.FallbackToolSelection is not null:
                        effectiveDecision = new ToolSelectionDecision(
                            rule.FallbackToolSelection.ToolId,
                            rule.FallbackToolSelection.Bindings);
                        var bypassNextNodeId = allowedNextNodes.Count == 0 ? routeStep.NodeId : allowedNextNodes.Single();
                        narrator?.OnDecisionGateBypassed(
                            state,
                            routeStep,
                            state.IntentToken,
                            allowedNextNodes,
                            rule.DecisionPolicy,
                            effectiveDecision,
                            bypassNextNodeId);
                        break;

                    case DecisionPolicy.Error:
                        error = new RuntimeError(
                            "route_decision_required",
                            "A decision is required by policy for this route step.",
                            routeStep.NodeId);

                        nextState = state.WithStatus(RoutingStatus.Halted);
                        narrator?.OnDecisionGateRequiredError(state, routeStep, state.IntentToken, allowedNextNodes, rule.DecisionPolicy, error);
                        narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
                        narrator?.OnHalted(nextState, error);
                        return false;

                    default:
                        nextState = state.WithStatus(RoutingStatus.Waiting);
                        narrator?.OnDecisionGateWaiting(nextState, routeStep, state.IntentToken, allowedNextNodes, rule.DecisionPolicy, rule.FallbackToolSelection, rule.FallbackNextNodeId);
                        narrator?.OnDecisionRequired(nextState, routeStep, state.IntentToken, allowedNextNodes);
                        error = null;
                        return false;
                }
            }

            if (!TryValidateToolSelection(plan, snapshot, effectiveDecision, out error))
            {
                nextState = state.WithStatus(RoutingStatus.Halted);
                narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error!);
                narrator?.OnHalted(nextState, error!);
                return false;
            }

            narrator?.OnDecisionAccepted(state, routeStep, state.IntentToken, allowedNextNodes);
        }

        // 11) Terminal handling
        if (allowedNextNodes.Count == 0)
        {
            nextState = state.WithStatus(RoutingStatus.Completed);
            narrator?.OnCompleted(nextState, routeStep, state.IntentToken, allowedNextNodes);
            error = null;
            return true;
        }

        // 12) Choose next node
        var nextNodeId = decision?.NextNodeId ?? allowedNextNodes.Single();

        var nextStep = ResolveRouteStep(plan.Steps, nextNodeId);
        if (nextStep is null)
        {
            error = new RuntimeError(
                "route_step_invalid",
                "Next route step is invalid.",
                nextNodeId);

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var nextRule = plan.Request.RouteRules.FirstOrDefault(r => r.NodeId == nextNodeId);
        if (nextRule is null)
        {
            error = new RuntimeError(
                "route_rule_missing",
                "Route rule is missing for the next node.",
                nextNodeId);

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnNodeHalted(state, routeStep, state.IntentToken, allowedNextNodes, error);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        // 13) Advance state
        nextState = state.Advance(
            RouteIntentTokenFactory.Create(plan, nextRule),
            nextNodeId,
            nextStep.Intent);

        narrator?.OnNodeAdvanced(
            nextState,
            routeStep,
            state.IntentToken,
            allowedNextNodes,
            nextNodeId,
            RoutingDecisionSource.Mermaid);

        error = null;
        return true;
    }

    private static bool IsIntentCompatible(RouteIntent intent, MermaidNodeKind nodeKind) =>
        nodeKind switch
        {
            MermaidNodeKind.Start => intent != RouteIntent.Terminate,
            MermaidNodeKind.Route => intent != RouteIntent.Terminate,
            MermaidNodeKind.Gate => intent != RouteIntent.Terminate,
            MermaidNodeKind.Tool => intent == RouteIntent.SelectTool,
            MermaidNodeKind.Terminal => intent == RouteIntent.Terminate,
            _ => false
        };

    private static RouteStep? ResolveRouteStep(
        IReadOnlyList<BuildStep> steps,
        string nodeId) =>
        steps.OfType<RouteStep>()
             .FirstOrDefault(s => s.NodeId == nodeId);

    private static bool TryValidateToolSelection(
        BuildPlan plan,
        IReadOnlyList<ToolRegistryEntry> snapshot,
        ToolSelectionDecision selection,
        out RuntimeError? error)
    {
        var entry = snapshot.FirstOrDefault(e => e.Spec.ToolId == selection.ToolId);
        if (entry is null)
        {
            error = new RuntimeError(
                "tool_missing",
                $"Tool '{selection.ToolId.Value}' is not registered.");
            return false;
        }

        if (!ToolAuthorityValidator.TryValidateAuthority(
                plan.Authority,
                entry.Spec.RequiredAuthority,
                out error))
            return false;

        foreach (var input in entry.Spec.Inputs.Where(i => i.Required))
        {
            if (!selection.Bindings.ContainsKey(input.Name))
            {
                error = new RuntimeError(
                    "tool_binding_missing",
                    $"Tool binding '{input.Name}' is required.");
                return false;
            }
        }

        foreach (var binding in selection.Bindings.Keys)
        {
            if (entry.Spec.Inputs.All(i => i.Name != binding))
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
