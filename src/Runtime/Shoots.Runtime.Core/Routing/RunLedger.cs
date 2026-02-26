using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Shoots.Runtime.Core.Routing;

public sealed record RunLedgerStep(
    string StepId,
    string NodeId,
    RouteNodeKind NodeKind,
    string? ToolId,
    IReadOnlyDictionary<string, string> Bindings,
    IReadOnlyList<string> OutgoingNodeIds);

public sealed class RunLedger
{
    public RunLedger(
        string routeGraphHash,
        string catalogHash,
        string requestHash,
        string planId,
        IReadOnlyList<RunLedgerStep> steps,
        IReadOnlyList<PlanArtifact> artifacts)
    {
        RouteGraphHash = routeGraphHash;
        CatalogHash = catalogHash;
        RequestHash = requestHash;
        PlanId = planId;
        Steps = new ReadOnlyCollection<RunLedgerStep>(steps.ToArray());
        Artifacts = new ReadOnlyCollection<PlanArtifact>(artifacts.ToArray());
    }

    public string RouteGraphHash { get; }

    public string CatalogHash { get; }

    public string RequestHash { get; }

    public string PlanId { get; }

    public IReadOnlyList<RunLedgerStep> Steps { get; }

    public IReadOnlyList<PlanArtifact> Artifacts { get; }
}

public static class RunLedgerBuilder
{
    public static RunLedger Create(BuildPlan plan, PlannerRequest request, RouteDefinition route, string catalogHash)
    {
        var routeHash = ComputeRouteHash(route);
        var requestHash = ComputeRequestHash(request);

        var steps = plan.Steps
            .Select(step => new RunLedgerStep(
                step.StepId,
                step.NodeId,
                step.NodeKind,
                step.ToolId,
                new ReadOnlyDictionary<string, string>(step.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                new ReadOnlyCollection<string>(step.Outgoing.Select(edge => edge.TargetNodeId).OrderBy(id => id, StringComparer.Ordinal).ToArray())))
            .ToArray();

        return new RunLedger(routeHash, catalogHash, requestHash, plan.PlanId, steps, plan.Artifacts);
    }

    public static string ComputeRouteHash(RouteDefinition route)
    {
        var builder = new StringBuilder();
        foreach (var step in route.Steps.OrderBy(step => step.NodeId, StringComparer.Ordinal))
        {
            builder.Append(step.StepId).Append('|').Append(step.NodeId).Append('|').Append(step.Kind).Append('\n');
            foreach (var edge in step.OutgoingEdges.OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal).ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal))
                builder.Append(edge.TargetNodeId).Append('|').Append(edge.Predicate ?? string.Empty).Append('\n');
        }

        return ComputeHash(builder.ToString());
    }

    public static string ComputeRequestHash(PlannerRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.CommandId).Append('\n');
        foreach (var input in request.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(input.Key).Append('=').Append(input.Value).Append('\n');

        builder.Append($"limits:{request.Limits.MaxSteps}|{request.Limits.MaxArtifacts}|{request.Limits.MaxEmittedTextBytes}");
        return ComputeHash(builder.ToString());
    }

    private static string ComputeHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed record ReplayResult(
    string PlanId,
    IReadOnlyList<RunLedgerStep> Steps,
    IReadOnlyList<PlanArtifact> Artifacts);

public sealed class ReplayRunner
{
    public ReplayResult Replay(RunLedger ledger, PlannerRequest request, RouteDefinition route, string catalogHash)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        var routeHash = RunLedgerBuilder.ComputeRouteHash(route);
        if (!string.Equals(routeHash, ledger.RouteGraphHash, StringComparison.Ordinal))
            throw new PlannerKernelException("replay.hash_mismatch", "Route graph hash mismatch.");

        if (!string.Equals(catalogHash, ledger.CatalogHash, StringComparison.Ordinal))
            throw new PlannerKernelException("replay.hash_mismatch", "Catalog hash mismatch.");

        var requestHash = RunLedgerBuilder.ComputeRequestHash(request);
        if (!string.Equals(requestHash, ledger.RequestHash, StringComparison.Ordinal))
            throw new PlannerKernelException("replay.hash_mismatch", "Request hash mismatch.");

        return new ReplayResult(ledger.PlanId, ledger.Steps, ledger.Artifacts);
    }
}
