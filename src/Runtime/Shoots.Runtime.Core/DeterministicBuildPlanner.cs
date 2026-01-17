using System.Security.Cryptography;
using System.Text;
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

        var steps = new List<BuildStep>
        {
            new("resolve-command", $"Resolve command '{request.CommandId}'."),
            new("execute-command", $"Execute command '{request.CommandId}'.")
        };

        var spec = _services.GetCommand(request.CommandId);
        if (spec is null)
        {
            steps.Insert(1, new BuildStep("validate-command", "Fail if the command is unknown."));
        }
        else
        {
            steps.Insert(1, new BuildStep("validate-command", $"Validate command '{spec.CommandId}' args."));
        }

        var artifacts = new List<BuildArtifact>
        {
            new("plan.txt", "Human-readable plan output."),
            new("plan.json", "Machine-readable plan output."),
            new("result.json", "Runtime execution result."),
            new("resolution.json", "Failure classification output.")
        };

        return new BuildPlan(
            PlanId: ComputePlanId(request),
            Request: request,
            Steps: steps,
            Artifacts: artifacts
        );
    }

    private static string ComputePlanId(BuildRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("command=").Append(request.CommandId);

        foreach (var kvp in request.Args.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("|arg=").Append(kvp.Key).Append('=').Append(kvp.Value?.ToString() ?? "null");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
