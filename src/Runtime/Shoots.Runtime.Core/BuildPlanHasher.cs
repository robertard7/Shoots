using System.Security.Cryptography;
using System.Text;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public static class BuildPlanHasher
{
    public static string ComputePlanId(
        BuildRequest request,
        ProviderId authorityProviderId,
        ProviderKind authorityKind,
        string delegationPolicyId,
        bool allowsDelegation)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));
        if (string.IsNullOrWhiteSpace(authorityProviderId.Value))
            throw new ArgumentException("authority provider id is required", nameof(authorityProviderId));
        if (string.IsNullOrWhiteSpace(delegationPolicyId))
            throw new ArgumentException("delegation policy id is required", nameof(delegationPolicyId));

        var sb = new StringBuilder();
        sb.Append("command=").Append(NormalizeToken(request.CommandId));
        sb.Append("|authority.provider=").Append(NormalizeToken(authorityProviderId.Value));
        sb.Append("|authority.kind=").Append(authorityKind.ToString());
        sb.Append("|delegation.policy=").Append(NormalizeToken(delegationPolicyId));
        sb.Append("|delegation.allowed=").Append(allowsDelegation.ToString());

        foreach (var kvp in request.Args.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("|arg=")
              .Append(NormalizeToken(kvp.Key))
              .Append('=')
              .Append(NormalizeToken(kvp.Value?.ToString() ?? "null"));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeToken(string value) => value.Trim();
}
