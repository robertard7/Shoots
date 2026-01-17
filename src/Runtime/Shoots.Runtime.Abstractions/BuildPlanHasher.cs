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
/// - authority.ProviderId, authority.Kind, authority.PolicyId, authority.AllowsDelegation
/// - request.Args ordered by key (case-insensitive), normalized key/value tokens (including plan.graph)
/// - steps ordered as provided (id + description, plus AI prompt/schema when present)
/// - tool steps include tool id + normalized input bindings + declared outputs
/// - artifacts ordered as provided (id + description)
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
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));
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
        }

        foreach (var artifact in artifacts)
        {
            sb.Append("|artifact=")
              .Append(NormalizeToken(artifact.Id))
              .Append('=')
              .Append(NormalizeTextToken(artifact.Description));
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
