using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class DeterministicBuildPlanner : IBuildPlanner
{
    private readonly IRuntimeServices _services;

    public DeterministicBuildPlanner(IRuntimeServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
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

        return new BuildPlan(
            PlanId: BuildPlanHasher.ComputePlanId(request),
            Request: request with { CommandId = normalizedCommandId, Args = normalizedArgs },
            Steps: steps,
            Artifacts: artifacts
        );
    }
}
