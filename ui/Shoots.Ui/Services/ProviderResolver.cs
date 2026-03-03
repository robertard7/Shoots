using System;

namespace Shoots.UI.Services;

public interface IProviderResolver
{
    ProviderResolveResult Resolve(string providerKind);
}

public sealed record ProviderResolveResult(bool Success, string AdapterId, string ErrorCode)
{
    public static ProviderResolveResult Ok(string adapterId) => new(true, adapterId, string.Empty);
    public static ProviderResolveResult Fail(string errorCode) => new(false, string.Empty, errorCode);
}

public sealed class ProviderResolver : IProviderResolver
{
    public ProviderResolveResult Resolve(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
        {
            return ProviderResolveResult.Fail("provider.kind.missing");
        }

        return providerKind.Trim().ToLowerInvariant() switch
        {
            "local" => ProviderResolveResult.Ok("adapter.local"),
            "remote" => ProviderResolveResult.Ok("adapter.remote"),
            "delegated" => ProviderResolveResult.Ok("adapter.delegated"),
            _ => ProviderResolveResult.Fail("provider.kind.unknown")
        };
    }
}
