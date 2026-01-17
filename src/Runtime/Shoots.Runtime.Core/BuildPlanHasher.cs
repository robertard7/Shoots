using System.Security.Cryptography;
using System.Text;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public static class BuildPlanHasher
{
    public static string ComputePlanId(
        BuildRequest request,
        DelegationAuthority authority)
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

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeToken(string value) => value.Trim();
}
