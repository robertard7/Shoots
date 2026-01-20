using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteIntentTokenFactory
{
    public static RouteIntentToken Create(BuildPlan plan, RouteRule rule)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (rule is null)
            throw new ArgumentNullException(nameof(rule));
        if (plan.Request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.GraphStructureHash))
            throw new ArgumentException("graph structure hash is required", nameof(plan));

        var allowed = rule.AllowedNextNodes ?? Array.Empty<string>();
        var orderedAllowed = allowed
            .Select(node => node.Trim())
            .OrderBy(node => node, StringComparer.Ordinal)
            .ToArray();

        return new RouteIntentToken(
            plan.Request.WorkOrder.Id,
            NormalizeToken(rule.NodeId),
            orderedAllowed,
            NormalizeHash(plan.GraphStructureHash));
    }

    public static string ComputeTokenHash(RouteIntentToken token)
    {
        if (string.IsNullOrWhiteSpace(token.WorkOrderId.Value))
            throw new ArgumentException("work order id is required", nameof(token));
        if (string.IsNullOrWhiteSpace(token.CurrentNodeId))
            throw new ArgumentException("current node id is required", nameof(token));
        if (token.AllowedNextNodeIds is null)
            throw new ArgumentException("allowed next nodes are required", nameof(token));
        if (string.IsNullOrWhiteSpace(token.GraphStructureHash))
            throw new ArgumentException("graph structure hash is required", nameof(token));

        var builder = new StringBuilder();
        builder.Append("workorder=").Append(NormalizeToken(token.WorkOrderId.Value));
        builder.Append("|node=").Append(NormalizeToken(token.CurrentNodeId));
        builder.Append("|graph=").Append(NormalizeHash(token.GraphStructureHash));
        builder.Append("|allowed=").Append(string.Join(",", token.AllowedNextNodeIds.Select(NormalizeToken)));
        return HashTools.ComputeSha256Hash(builder.ToString());
    }
    private static string NormalizeHash(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeToken(string value) => value.Trim();
}
