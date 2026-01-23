#nullable enable

using System;
using System.Text;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "usage: shoots <command> --graph <mermaid> --workorder-id <id> --workorder-request <text> --workorder-goal <text> " +
        "--route <node:intent:owner:allowedOutputKind> [--constraint <text>] [--success <text>] [--out <path>]");
    return 1;
}

string? command = null;
string? graph = null;
string? outputPath = null;
string? workOrderId = null;
string? workOrderGoal = null;
string? workOrderRequest = null;

var extraArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
var constraints = new List<string>();
var successCriteria = new List<string>();
var routeRules = new List<RouteRule>();

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];

    if (string.Equals(arg, "--graph", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --graph");

        graph = args[i];
        continue;
    }

    if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --out");

        outputPath = args[i];
        continue;
    }

    if (string.Equals(arg, "--workorder-id", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --workorder-id");

        workOrderId = args[i];
        continue;
    }

    if (string.Equals(arg, "--workorder-goal", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --workorder-goal");

        workOrderGoal = args[i];
        continue;
    }

    if (string.Equals(arg, "--workorder-request", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --workorder-request");

        workOrderRequest = args[i];
        continue;
    }

    if (string.Equals(arg, "--constraint", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --constraint");

        constraints.Add(args[i]);
        continue;
    }

    if (string.Equals(arg, "--success", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --success");

        successCriteria.Add(args[i]);
        continue;
    }

    if (string.Equals(arg, "--route", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length)
            return Error("missing value for --route");

        var routeValue = args[i];
        var routeParts = routeValue.Split(':', 4, StringSplitOptions.None);

        if (routeParts.Length != 4)
            return Error("invalid --route format, expected node:intent:owner:allowedOutputKind");

        if (!Enum.TryParse<RouteIntent>(routeParts[1], true, out var intent))
            return Error($"invalid route intent: {routeParts[1]}");

        if (!Enum.TryParse<DecisionOwner>(routeParts[2], true, out var owner))
            return Error($"invalid route owner: {routeParts[2]}");

        if (string.IsNullOrWhiteSpace(routeParts[3]))
            return Error("route allowed output kind is required");

        routeRules.Add(new RouteRule(
            NodeId: routeParts[0],
            Intent: intent,
            Owner: owner,
            AllowedOutputKind: routeParts[3],
            NodeKind: MermaidNodeKind.Route,
            AllowedNextNodes: Array.Empty<string>()
        ));

        continue;
    }

    if (command is null)
    {
        command = arg;
        continue;
    }

    var kvp = arg.Split('=', 2);
    if (kvp.Length == 2)
    {
        extraArgs[kvp[0]] = kvp[1];
        continue;
    }

    return Error($"unknown argument: {arg}");
}

if (string.IsNullOrWhiteSpace(command)) return Error("command is required");
if (string.IsNullOrWhiteSpace(graph)) return Error("--graph is required");
if (string.IsNullOrWhiteSpace(workOrderId)) return Error("--workorder-id is required");
if (string.IsNullOrWhiteSpace(workOrderGoal)) return Error("--workorder-goal is required");
if (string.IsNullOrWhiteSpace(workOrderRequest)) return Error("--workorder-request is required");
if (routeRules.Count == 0) return Error("at least one --route is required");

extraArgs["plan.graph"] = graph;

var workOrder = new WorkOrder(
    Id: new WorkOrderId(workOrderId),
    OriginalRequest: workOrderRequest,
    Goal: workOrderGoal,
    Constraints: constraints,
    SuccessCriteria: successCriteria
);

var buildRequest = new BuildRequest(
    WorkOrder: workOrder,
    CommandId: command,
    Args: extraArgs,
    RouteRules: routeRules
);

var planner = new DeterministicBuildPlanner(
    new CliRuntimeServices(),
    new CliDelegationPolicy()
);

var plan = planner.Plan(buildRequest);
var json = BuildPlanRenderer.RenderJson(plan);

if (!string.IsNullOrWhiteSpace(outputPath))
    File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
else
    Console.WriteLine(json);

return 0;

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

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
