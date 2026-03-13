using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteGate
{
    private static readonly AsyncLocal<IRuntimeNarrator?> NarratorSlot = new();

    public static IRuntimeNarrator? Narrator
    {
        get => NarratorSlot.Value;
        set => NarratorSlot.Value = value;
    }

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

        // FINAL STATE GUARD
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

        // WORK ORDER VALIDATION
        if (plan.Request.WorkOrder is null)
        {
            error = new RuntimeError("route_workorder_missing", "Work order is required.");
            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (!string.Equals(plan.Request.WorkOrder.Id.Value, state.WorkOrderId.Value, StringComparison.Ordinal))
        {
            error = new RuntimeError(
                "route_workorder_mismatch",
                "Work order mismatch between plan and state.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        if (plan.Request.RouteRules is null)
        {
            error = new RuntimeError("route_rules_missing", "Route rules are required.");
            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var routeStep = ResolveRouteStep(plan.Steps, state.CurrentNodeId);
        if (routeStep is null)
        {
            error = new RuntimeError("route_step_invalid", "Route step missing.", state.CurrentNodeId);
            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var rule = plan.Request.RouteRules.FirstOrDefault(r => r.NodeId == routeStep.NodeId);
        if (rule is null)
        {
            error = new RuntimeError("route_rule_missing", "Route rule missing.", routeStep.NodeId);
            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var allowedNextNodes = rule.AllowedNextNodes ?? Array.Empty<string>();

        narrator?.OnRouteEntered(state, routeStep, state.IntentToken, allowedNextNodes);
        narrator?.OnNodeEntered(state, routeStep, state.IntentToken, allowedNextNodes);

        // DECISION ON WRONG STEP
        if (routeStep.Intent != RouteIntent.SelectTool && decision?.ToolSelection is not null)
        {
            error = new RuntimeError(
                "route_decision_unexpected",
                "Tool selection decision is only valid for SelectTool steps.");

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

		// OWNER VALIDATION (only applies when explicit decision provided)
		if (routeStep.Intent == RouteIntent.SelectTool &&
			decision?.ToolSelection is not null &&
			rule.Owner != DecisionOwner.Ai)
		{
			error = new RuntimeError(
				"route_owner_invalid",
				"SelectTool route steps must be owned by Ai.");

			nextState = state.WithStatus(RoutingStatus.Halted);
			narrator?.OnHalted(nextState, error);
			return false;
		}

        // SELECT TOOL FLOW
        if (routeStep.Intent == RouteIntent.SelectTool)
        {
            var explicitDecision = decision?.ToolSelection;

            if (explicitDecision is null)
            {
                switch (rule.DecisionPolicy)
                {
                    case DecisionPolicy.Error:
                        error = new RuntimeError(
                            "route_decision_required",
                            "Decision required by policy.",
                            routeStep.NodeId);

                        nextState = state.WithStatus(RoutingStatus.Halted);

                        narrator?.OnDecisionGateRequiredError(
                            state,
                            routeStep,
                            state.IntentToken,
                            allowedNextNodes,
                            rule.DecisionPolicy,
                            error);

                        narrator?.OnHalted(nextState, error);
                        return false;

                    case DecisionPolicy.Bypass when rule.FallbackToolSelection is not null:

                        if (allowedNextNodes.Count > 1)
                        {
                            error = new RuntimeError(
                                "route_step_invalid",
                                "Bypass requires at most one next node.",
                                routeStep.NodeId);

                            nextState = state.WithStatus(RoutingStatus.Halted);
                            narrator?.OnHalted(nextState, error);
                            return false;
                        }

                        var fallbackDecision = new ToolSelectionDecision(
                            rule.FallbackToolSelection.ToolId,
                            rule.FallbackToolSelection.Bindings);

                        narrator?.OnDecisionGateBypassed(
                            state,
                            routeStep,
                            state.IntentToken,
                            allowedNextNodes,
                            rule.DecisionPolicy,
                            fallbackDecision,
                            allowedNextNodes.Count == 0 ? routeStep.NodeId : allowedNextNodes[0]);

                        if (allowedNextNodes.Count == 0)
                        {
                            nextState = state.WithStatus(RoutingStatus.Completed);
                            narrator?.OnCompleted(nextState, routeStep, state.IntentToken, allowedNextNodes);
                            error = null;
                            return true;
                        }

                        var nextNodeId = allowedNextNodes[0];
                        var nextStep = ResolveRouteStep(plan.Steps, nextNodeId)!;
                        var nextRule = plan.Request.RouteRules.First(r => r.NodeId == nextNodeId);

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

                        EmitCompletionIfTerminal(plan, nextState, nextStep);

                        error = null;
                        return true;

                    default:
                        nextState = state.WithStatus(RoutingStatus.Waiting);

                        narrator?.OnDecisionGateWaiting(
                            nextState,
                            routeStep,
                            state.IntentToken,
                            allowedNextNodes,
                            rule.DecisionPolicy,
                            rule.FallbackToolSelection,
                            rule.FallbackNextNodeId);

                        error = null;
                        return false;
                }
            }

            narrator?.OnDecisionAccepted(state, routeStep, state.IntentToken, allowedNextNodes);
        }

        // TERMINAL EDGE
        if (allowedNextNodes.Count == 0)
        {
            nextState = state.WithStatus(RoutingStatus.Completed);
            narrator?.OnCompleted(nextState, routeStep, state.IntentToken, allowedNextNodes);
            error = null;
            return true;
        }

        // NORMAL ADVANCE
        if (allowedNextNodes.Count != 1 && decision?.NextNodeId is null)
        {
            error = new RuntimeError(
                "route_nextnode_ambiguous",
                "Multiple next nodes require explicit decision.",
                routeStep.NodeId);

            nextState = state.WithStatus(RoutingStatus.Halted);
            narrator?.OnHalted(nextState, error);
            return false;
        }

        var nextNode = decision?.NextNodeId ?? allowedNextNodes.Single();

        var nextRuleFinal = plan.Request.RouteRules.First(r => r.NodeId == nextNode);
        var nextStepFinal = ResolveRouteStep(plan.Steps, nextNode)!;

        nextState = state.Advance(
            RouteIntentTokenFactory.Create(plan, nextRuleFinal),
            nextNode,
            nextStepFinal.Intent);

        narrator?.OnNodeAdvanced(
            nextState,
            routeStep,
            state.IntentToken,
            allowedNextNodes,
            nextNode,
            RoutingDecisionSource.Mermaid);

        EmitCompletionIfTerminal(plan, nextState, nextStepFinal);

        error = null;
        return true;
    }

    private static void EmitCompletionIfTerminal(
        BuildPlan plan,
        RoutingState state,
        RouteStep step)
    {
        var narrator = Narrator;
        var rule = plan.Request.RouteRules.First(r => r.NodeId == step.NodeId);
        var nextAllowed = rule.AllowedNextNodes ?? Array.Empty<string>();

        if (nextAllowed.Count == 0)
        {
            var completedState = state.WithStatus(RoutingStatus.Completed);
            narrator?.OnCompleted(completedState, step, completedState.IntentToken, nextAllowed);
        }
    }

    private static RouteStep? ResolveRouteStep(
        IReadOnlyList<BuildStep> steps,
        string nodeId) =>
        steps.OfType<RouteStep>()
             .FirstOrDefault(s => s.NodeId == nodeId);
}
