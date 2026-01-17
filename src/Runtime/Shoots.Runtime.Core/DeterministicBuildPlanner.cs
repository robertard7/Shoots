using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class DeterministicBuildPlanner : IBuildPlanner
{
    private readonly IRuntimeServices _services;
    private readonly IDelegationPolicy _policy;

    public DeterministicBuildPlanner(IRuntimeServices services, IDelegationPolicy policy)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public BuildPlan Plan(BuildRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var normalizedCommandId = request.CommandId.Trim();
        var normalizedArgs = new SortedDictionary<string, object?>(
            request.Args,
            StringComparer.OrdinalIgnoreCase);
        var steps = new List<BuildStep>();

        var spec = _services.GetCommand(normalizedCommandId);
        if (spec is null)
        {
            steps.Add(new BuildStep("validate-command", "Fail if the command is unknown."));
        }
        else
        {
            steps.Add(new BuildStep("validate-command", $"Validate command '{spec.CommandId}' args."));
        }
        steps.Add(new BuildStep("resolve-command", $"Resolve command '{normalizedCommandId}'."));
        steps.Add(new BuildStep("execute-command", $"Execute command '{normalizedCommandId}'."));

        var artifacts = new[]
        {
            new BuildArtifact("plan.txt", "Human-readable plan output."),
            new BuildArtifact("plan.json", "Machine-readable plan output."),
            new BuildArtifact("result.json", "Runtime execution result."),
            new BuildArtifact("resolution.json", "Failure classification output.")
        };

        var normalizedRequest = request with { CommandId = normalizedCommandId, Args = normalizedArgs };
        var provisionalPlan = new BuildPlan(
            PlanId: string.Empty,
            Request: normalizedRequest,
            AuthorityProviderId: new ProviderId("local"),
            AuthorityKind: ProviderKind.Local,
            Steps: steps,
            Artifacts: artifacts
        );

        var decision = _policy.Decide(normalizedRequest, provisionalPlan);
        var planId = BuildPlanHasher.ComputePlanId(
            normalizedRequest,
            decision.AuthorityProviderId,
            decision.AuthorityKind);

        return new BuildPlan(
            PlanId: planId,
            Request: normalizedRequest,
            AuthorityProviderId: decision.AuthorityProviderId,
            AuthorityKind: decision.AuthorityKind,
            Steps: steps,
            Artifacts: artifacts
        );
    }
}
