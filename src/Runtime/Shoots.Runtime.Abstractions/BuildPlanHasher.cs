using System;
using System.Security.Cryptography;
using System.Text;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Deterministic plan hash derived only from semantically relevant inputs.
/// Inputs (in order):
/// - request.CommandId (normalized)
/// - BuildContract.Version
/// - request.WorkOrder fields
/// - authority.ProviderId, authority.Kind, authority.PolicyId, authority.AllowsDelegation
/// - request.Args ordered by key (case-insensitive), normalized key/value tokens (including plan.graph)
/// - request.RouteRules ordered by node id (including allowed output kind)
/// - steps ordered as provided (id + description, plus AI prompt/schema when present)
/// - tool steps include tool id + normalized input bindings + declared outputs
/// - route steps include node id + intent + owner + work order id
/// - route steps include tool invocation details when present
/// - artifacts ordered as provided (id + description)
/// - tool result fields when provided
/// Excludes timestamps, environment/machine identifiers, absolute paths, and other non-semantic runtime state.
/// </summary>
public static class BuildPlanHasher
{
    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts)
    {
        return ComputePlanId(request, authority, steps, artifacts, null);
    }

    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts,
        ToolResult? toolResult)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));
        if (request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(request));
        if (request.WorkOrder.Constraints is null)
            throw new ArgumentException("work order constraints are required", nameof(request));
        if (request.WorkOrder.SuccessCriteria is null)
            throw new ArgumentException("work order success criteria are required", nameof(request));
        if (request.RouteRules is null)
            throw new ArgumentException("route rules are required", nameof(request));
        if (authority is null)
            throw new ArgumentNullException(nameof(authority));
        if (string.IsNullOrWhiteSpace(authority.ProviderId.Value))
            throw new ArgumentException("authority provider id is required", nameof(authority));
        if (string.IsNullOrWhiteSpace(authority.PolicyId))
            throw new ArgumentException("delegation policy id is required", nameof(authority));
        if (steps is null)
            throw new ArgumentNullException(nameof(steps));
        if (artifacts is null)
            throw new ArgumentNullException(nameof(artifacts));

        var sb = new StringBuilder();
        sb.Append("command=").Append(NormalizeToken(request.CommandId));
        sb.Append("|contract=").Append(BuildContract.Version);
        sb.Append("|workorder.id=").Append(NormalizeToken(request.WorkOrder.Id.Value));
        sb.Append("|workorder.request=").Append(NormalizeTextToken(request.WorkOrder.OriginalRequest));
        sb.Append("|workorder.goal=").Append(NormalizeTextToken(request.WorkOrder.Goal));

        foreach (var constraint in request.WorkOrder.Constraints)
            sb.Append("|workorder.constraint=").Append(NormalizeTextToken(constraint));

        foreach (var criterion in request.WorkOrder.SuccessCriteria)
            sb.Append("|workorder.success=").Append(NormalizeTextToken(criterion));
        sb.Append("|authority.provider=").Append(NormalizeToken(authority.ProviderId.Value));
        sb.Append("|authority.kind=").Append(authority.Kind.ToString());
        sb.Append("|delegation.policy=").Append(NormalizeToken(authority.PolicyId));
        sb.Append("|delegation.allowed=").Append(authority.AllowsDelegation.ToString());

        foreach (var kvp in request.Args.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("|arg=")
              .Append(NormalizeToken(kvp.Key))
              .Append('=')
              .Append(NormalizeToken(kvp.Value?.ToString() ?? "null"));
        }

        foreach (var rule in request.RouteRules
                     .OrderBy(rule => rule.NodeId, StringComparer.Ordinal))
        {
            sb.Append("|route.rule=")
              .Append(NormalizeToken(rule.NodeId))
              .Append(':')
              .Append(rule.Intent.ToString())
              .Append(':')
              .Append(rule.Owner.ToString())
              .Append('=')
              .Append(NormalizeToken(rule.AllowedOutputKind));
        }

        foreach (var step in steps)
        {
            sb.Append("|step=")
              .Append(NormalizeToken(step.Id))
              .Append('=')
              .Append(NormalizeTextToken(step.Description));

            if (step is AiBuildStep aiStep)
            {
                sb.Append("|ai.prompt=").Append(NormalizeTextToken(aiStep.Prompt));
                sb.Append("|ai.schema=").Append(NormalizeTextToken(aiStep.OutputSchema));
            }

            if (step is ToolBuildStep toolStep)
            {
                sb.Append("|tool.id=").Append(NormalizeToken(toolStep.ToolId.Value));

                foreach (var binding in toolStep.InputBindings
                             .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append("|tool.input=")
                      .Append(NormalizeToken(binding.Key))
                      .Append('=')
                      .Append(NormalizeTextToken(binding.Value?.ToString() ?? "null"));
                }

                foreach (var output in toolStep.Outputs)
                {
                    sb.Append("|tool.output=")
                      .Append(NormalizeToken(output.Name))
                      .Append(':')
                      .Append(NormalizeToken(output.Type))
                      .Append('=')
                      .Append(NormalizeTextToken(output.Description));
                }
            }

            if (step is RouteStep routeStep)
            {
                sb.Append("|route.node=").Append(NormalizeToken(routeStep.NodeId));
                sb.Append("|route.intent=").Append(routeStep.Intent.ToString());
                sb.Append("|route.owner=").Append(routeStep.Owner.ToString());
                sb.Append("|route.workorder=").Append(NormalizeToken(routeStep.WorkOrderId.Value));

                if (routeStep.ToolInvocation is not null)
                {
                    sb.Append("|route.tool.id=").Append(NormalizeToken(routeStep.ToolInvocation.ToolId.Value));
                    sb.Append("|route.tool.workorder=").Append(NormalizeToken(routeStep.ToolInvocation.WorkOrderId.Value));

                    foreach (var binding in routeStep.ToolInvocation.Bindings
                                 .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.Append("|route.tool.binding=")
                          .Append(NormalizeToken(binding.Key))
                          .Append('=')
                          .Append(NormalizeTextToken(binding.Value?.ToString() ?? "null"));
                    }
                }
            }
        }

        foreach (var artifact in artifacts)
        {
            sb.Append("|artifact=")
              .Append(NormalizeToken(artifact.Id))
              .Append('=')
              .Append(NormalizeTextToken(artifact.Description));
        }

        if (toolResult is not null)
        {
            sb.Append("|tool.result.id=").Append(NormalizeToken(toolResult.ToolId.Value));
            sb.Append("|tool.result.success=").Append(toolResult.Success.ToString());
            foreach (var output in toolResult.Outputs
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("|tool.result.output=")
                  .Append(NormalizeToken(output.Key))
                  .Append('=')
                  .Append(NormalizeTextToken(output.Value?.ToString() ?? "null"));
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeToken(string value) => value.Trim();

    private static string NormalizeTextToken(string value)
    {
        var normalized = value.Trim();
        if (LooksLikeAbsolutePath(normalized))
            throw new ArgumentException("absolute paths are not allowed in plan text inputs", nameof(value));
        return normalized;
    }

    private static bool LooksLikeAbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.StartsWith("/", StringComparison.Ordinal) ||
               value.StartsWith("\\", StringComparison.Ordinal) ||
               (value.Length > 1 && value[1] == ':');
    }
}
