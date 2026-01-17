using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

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

        // ----------------------------
        // Normalize request (PURE)
        // ----------------------------

        var normalizedCommandId = request.CommandId.Trim();

        var normalizedArgs =
            new SortedDictionary<string, object?>(
                request.Args ?? throw new ArgumentException("args are required", nameof(request)),
                StringComparer.OrdinalIgnoreCase
            );

        var normalizedRequest = request with
        {
            CommandId = normalizedCommandId,
            Args = normalizedArgs
        };

        // ----------------------------
        // Build steps (deterministic)
        // ----------------------------

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

        // Guardrail: planner must not leak execution metadata
        if (steps.Any(s => s.Id.Contains("execute:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Plan steps must not embed execution metadata.");

        // ----------------------------
        // Artifacts (stable ordering)
        // ----------------------------

        var artifacts = new[]
        {
            new BuildArtifact("plan.txt", "Human-readable plan output."),
            new BuildArtifact("plan.json", "Machine-readable plan output."),
            new BuildArtifact("result.json", "Runtime execution result."),
            new BuildArtifact("resolution.json", "Failure classification output.")
        };

        // ----------------------------
        // Provisional plan (authority seed)
        // ----------------------------

        if (string.IsNullOrWhiteSpace(_policy.PolicyId))
            throw new InvalidOperationException("Delegation policy id is required.");

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

        // ----------------------------
        // Delegation decision (PURE)
        // ----------------------------

        var decision = _policy.Decide(normalizedRequest, provisionalPlan);

        if (!decision.Authority.AllowsDelegation &&
            decision.Authority.Kind == ProviderKind.Delegated)
        {
            throw new InvalidOperationException(
                "Delegation cannot be delegated without permission."
            );
        }

        // ----------------------------
        // Deterministic hash
        // ----------------------------

        var planId = BuildPlanHasher.ComputePlanId(
            normalizedRequest,
            decision.Authority
        );

        // ----------------------------
        // Final plan (IMMUTABLE)
        // ----------------------------

        return new BuildPlan(
            PlanId: planId,
            Request: normalizedRequest,
            Authority: decision.Authority,
            Steps: steps,
            Artifacts: artifacts
        );
    }
}
