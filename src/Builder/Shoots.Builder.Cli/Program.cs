using System;
using System.Collections.Generic;
using System.Text;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: shoots <command> --graph <mermaid> --workorder-id <id> --workorder-request <text> --workorder-goal <text> --route <node:intent:owner:allowedOutputKind> [--constraint <text>] [--success <text>] [--out <path>]");
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

    if (string.Equals(arg, "--workorder-id", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --workorder-id");
            return 1;
        }

        workOrderId = args[++i];
        continue;
    }

    if (string.Equals(arg, "--workorder-goal", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --workorder-goal");
            return 1;
        }

        workOrderGoal = args[++i];
        continue;
    }

    if (string.Equals(arg, "--workorder-request", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --workorder-request");
            return 1;
        }

        workOrderRequest = args[++i];
        continue;
    }

    if (string.Equals(arg, "--constraint", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --constraint");
            return 1;
        }

        constraints.Add(args[++i]);
        continue;
    }

    if (string.Equals(arg, "--success", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --success");
            return 1;
        }

        successCriteria.Add(args[++i]);
        continue;
    }

    if (string.Equals(arg, "--route", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --route");
            return 1;
        }

        var routeValue = args[++i];
        var parts = routeValue.Split(':', 4, StringSplitOptions.None);
        if (parts.Length != 4)
        {
            Console.Error.WriteLine("invalid --route format, expected node:intent:owner:allowedOutputKind");
            return 1;
        }

        if (!Enum.TryParse<RouteIntent>(parts[1], true, out var intent))
        {
            Console.Error.WriteLine($"invalid route intent: {parts[1]}");
            return 1;
        }

        if (!Enum.TryParse<DecisionOwner>(parts[2], true, out var owner))
        {
            Console.Error.WriteLine($"invalid route owner: {parts[2]}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(parts[3]))
        {
            Console.Error.WriteLine("route allowed output kind is required");
            return 1;
        }

        routeRules.Add(new RouteRule(parts[0], intent, owner, parts[3], MermaidNodeKind.Route, Array.Empty<string>()));
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

if (string.IsNullOrWhiteSpace(workOrderId))
{
    Console.Error.WriteLine("--workorder-id is required");
    return 1;
}

if (string.IsNullOrWhiteSpace(workOrderGoal))
{
    Console.Error.WriteLine("--workorder-goal is required");
    return 1;
}

if (string.IsNullOrWhiteSpace(workOrderRequest))
{
    Console.Error.WriteLine("--workorder-request is required");
    return 1;
}

if (routeRules.Count == 0)
{
    Console.Error.WriteLine("at least one --route is required");
    return 1;
}

extraArgs["plan.graph"] = graph;

var workOrder = new WorkOrder(
    Id: new WorkOrderId(workOrderId),
    OriginalRequest: workOrderRequest,
    Goal: workOrderGoal,
    Constraints: constraints,
    SuccessCriteria: successCriteria);

var buildRequest = new BuildRequest(
    WorkOrder: workOrder,
    CommandId: command,
    Args: extraArgs,
    RouteRules: routeRules
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
