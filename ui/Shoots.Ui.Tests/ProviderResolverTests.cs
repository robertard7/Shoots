using Shoots.UI.Services;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ProviderResolverTests
{
    [Theory]
    [InlineData("Local", "adapter.local")]
    [InlineData("Remote", "adapter.remote")]
    [InlineData("Delegated", "adapter.delegated")]
    public void Resolver_accepts_known_kinds(string kind, string adapterId)
    {
        var resolver = new ProviderResolver();

        var result = resolver.Resolve(kind);

        Assert.True(result.Success);
        Assert.Equal(adapterId, result.AdapterId);
        Assert.Equal(string.Empty, result.ErrorCode);
    }

    [Fact]
    public void Resolver_rejects_unknown_kind_with_stable_error_code()
    {
        var resolver = new ProviderResolver();

        var result = resolver.Resolve("Ollama");

        Assert.False(result.Success);
        Assert.Equal("provider.kind.unknown", result.ErrorCode);
    }
}
