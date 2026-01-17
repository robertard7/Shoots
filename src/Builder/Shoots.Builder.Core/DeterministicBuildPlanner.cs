using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

public sealed class DeterministicBuildPlanner : IBuildPlanner
{
    private readonly IRuntimeServices _services;
    private readonly IDelegationPolicy _policy;

    public DeterministicBuildPlanner(
        IRuntimeServices services,
        IDelegationPolicy policy)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public BuildPlan Plan(BuildRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));

        // Normalize command
        var normalizedCommandId = request.CommandId.Trim();

        // Normalize args deterministically:
        // - case-insensitive keys
        // - stable ordering
        // - no mutation of the request's original dictionary
        var normalizedArgs = new SortedDictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in request.Args)
            normalizedArgs[kvp.Key] = kvp.Value;

        var normalizedRequest = request with
        {
            CommandId = normalizedCommandId,
            Args = normalizedArgs
        };

        // Deterministic steps
        var steps = new List<BuildStep>();

        var spec = _services.GetCommand(normalizedCommandId);

        steps.Add(
            spec is null
                ? new BuildStep("validate-command", "Fail if the command is unknown.")
                : new BuildStep("validate-command", $"Validate command '{spec.CommandId}' args.")
        );

        steps.Add(new BuildStep(
            "resolve-command",
            $"Resolve command '{normalizedCommandId}'."
        ));

        steps.Add(new BuildStep(
            "execute-command",
            $"Execute command '{normalizedCommandId}'."
        ));

        // Guardrail: steps must not embed execution metadata
        if (steps.Any(s => s.Id.Contains("execute:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Plan steps must not embed execution metadata.");

        // Stable artifacts list
        var artifacts = new[]
        {
            new BuildArtifact("plan.txt", "Human-readable plan output."),
            new BuildArtifact("plan.json", "Machine-readable plan output."),
            new BuildArtifact("result.json", "Runtime execution result."),
            new BuildArtifact("resolution.json", "Failure classification output.")
        };

        if (string.IsNullOrWhiteSpace(_policy.PolicyId))
            throw new InvalidOperationException("Delegation policy id is required.");

        // Provisional plan for delegation decision (authority seed)
        var provisionalAuthority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: _policy.PolicyId,
            AllowsDelegation: false
        );

        var provisionalPlan = new BuildPlan(
            PlanId: string.Empty,
            Request: normalizedRequest,
            Authority: provisionalAuthority,
            Steps: steps,
            Artifacts: artifacts
        );

        // Delegation decision (pure)
        var decision = _policy.Decide(normalizedRequest, provisionalPlan);

        // Deterministic hash (single authority)
        var planId = BuildPlanHasher.ComputePlanId(normalizedRequest, decision.Authority);

        return new BuildPlan(
            PlanId: planId,
            Request: normalizedRequest,
            Authority: decision.Authority,
            Steps: steps,
            Artifacts: artifacts
        );
    }
}
