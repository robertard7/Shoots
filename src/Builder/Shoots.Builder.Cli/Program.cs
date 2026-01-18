using System.Text;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: shoots <command> --graph <mermaid> [--out <path>]");
    return 1;
}

string? command = null;
string? graph = null;
string? outputPath = null;
var extraArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (string.Equals(arg, "--graph", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --graph");
            return 1;
        }

        graph = args[++i];
        continue;
    }

    if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --out");
            return 1;
        }

        outputPath = args[++i];
        continue;
    }

    if (command is null)
    {
        command = arg;
        continue;
    }

    var parts = arg.Split('=', 2);
    if (parts.Length == 2)
    {
        extraArgs[parts[0]] = parts[1];
        continue;
    }

    Console.Error.WriteLine($"unknown argument: {arg}");
    return 1;
}

if (string.IsNullOrWhiteSpace(command))
{
    Console.Error.WriteLine("command is required");
    return 1;
}

if (string.IsNullOrWhiteSpace(graph))
{
    Console.Error.WriteLine("--graph is required");
    return 1;
}

extraArgs["plan.graph"] = graph;

var buildRequest = new BuildRequest(
    CommandId: command,
    Args: extraArgs
);

var planner = new DeterministicBuildPlanner(
    new CliRuntimeServices(),
    new CliDelegationPolicy());

var plan = planner.Plan(buildRequest);
var json = BuildPlanRenderer.RenderJson(plan);

if (!string.IsNullOrWhiteSpace(outputPath))
{
    File.WriteAllText(outputPath, json, Encoding.UTF8);
}
else
{
    Console.WriteLine(json);
}

return 0;

internal sealed class CliRuntimeServices : IRuntimeServices
{
    public IReadOnlyList<RuntimeCommandSpec> GetAllCommands() => Array.Empty<RuntimeCommandSpec>();

    public RuntimeCommandSpec? GetCommand(string commandId) => null;
}

internal sealed class CliDelegationPolicy : IDelegationPolicy
{
    public string PolicyId => "cli-local";

    public DelegationDecision Decide(BuildRequest request, BuildPlan plan)
    {
        _ = request ?? throw new ArgumentNullException(nameof(request));
        _ = plan ?? throw new ArgumentNullException(nameof(plan));

        return new DelegationDecision(
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: PolicyId,
                AllowsDelegation: false
            )
        );
    }
}
