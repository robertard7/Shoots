using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteIntentTokenFactory
{
    public static RouteIntentToken Create(string constraintsHash, string currentStateHash)
    {
        if (string.IsNullOrWhiteSpace(constraintsHash))
            throw new ArgumentException("constraints hash is required", nameof(constraintsHash));
        if (string.IsNullOrWhiteSpace(currentStateHash))
            throw new ArgumentException("current state hash is required", nameof(currentStateHash));

        return new RouteIntentToken(
            NormalizeHash(constraintsHash),
            NormalizeHash(currentStateHash));
    }

    public static RouteIntentToken Create(WorkOrder workOrder, RouteStep originStep)
    {
        if (workOrder is null)
            throw new ArgumentNullException(nameof(workOrder));
        if (originStep is null)
            throw new ArgumentNullException(nameof(originStep));

        var constraintsHash = ComputeConstraintsHash(workOrder.Constraints ?? Array.Empty<string>());
        var currentStateHash = ComputeCurrentStateHash(workOrder.Id, originStep);
        return Create(constraintsHash, currentStateHash);
    }

    public static string ComputeTokenHash(RouteIntentToken token)
    {
        if (string.IsNullOrWhiteSpace(token.ConstraintsHash))
            throw new ArgumentException("constraints hash is required", nameof(token));
        if (string.IsNullOrWhiteSpace(token.CurrentStateHash))
            throw new ArgumentException("current state hash is required", nameof(token));

        var builder = new StringBuilder();
        builder.Append("constraints=").Append(NormalizeHash(token.ConstraintsHash));
        builder.Append("|state=").Append(NormalizeHash(token.CurrentStateHash));
        return HashTools.ComputeSha256Hash(builder.ToString());
    }

    private static string ComputeConstraintsHash(IEnumerable<string> constraints)
    {
        var normalized = constraints
            .Select(item => item?.Trim() ?? string.Empty)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        return HashTools.ComputeSha256Hash(string.Join("|", normalized));
    }

    private static string ComputeCurrentStateHash(WorkOrderId workOrderId, RouteStep step)
    {
        var builder = new StringBuilder();
        builder.Append("workorder=").Append(NormalizeToken(workOrderId.Value));
        builder.Append("|step.id=").Append(NormalizeToken(step.Id));
        builder.Append("|node=").Append(NormalizeToken(step.NodeId));
        builder.Append("|intent=").Append(step.Intent.ToString());
        builder.Append("|owner=").Append(step.Owner.ToString());
        builder.Append("|step.workorder=").Append(NormalizeToken(step.WorkOrderId.Value));

        if (step.ToolInvocation is not null)
        {
            builder.Append("|tool.id=").Append(NormalizeToken(step.ToolInvocation.ToolId.Value));
            builder.Append("|tool.workorder=").Append(NormalizeToken(step.ToolInvocation.WorkOrderId.Value));

            foreach (var binding in step.ToolInvocation.Bindings
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("|tool.binding=")
                    .Append(NormalizeToken(binding.Key))
                    .Append('=')
                    .Append(NormalizeText(binding.Value?.ToString() ?? "null"));
            }
        }

        return HashTools.ComputeSha256Hash(builder.ToString());
    }

    private static string NormalizeHash(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeToken(string value) => value.Trim();

    private static string NormalizeText(string value) => value.Trim();
}
