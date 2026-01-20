using System;
using System.Net.Http;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderFailureTests
{
    [Fact]
    public void Maps_timeout_failures()
    {
        var failure = ProviderFailure.FromException(new TimeoutException("timeout"));

        Assert.Equal(ProviderFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public void Maps_transport_failures()
    {
        var failure = ProviderFailure.FromException(new HttpRequestException("down"));

        Assert.Equal(ProviderFailureKind.Transport, failure.Kind);
    }

    [Fact]
    public void Maps_invalid_output_failures()
    {
        var failure = ProviderFailure.FromException(new FormatException("bad"));

        Assert.Equal(ProviderFailureKind.InvalidOutput, failure.Kind);
    }

    [Fact]
    public void Maps_contract_violations()
    {
        var failure = ProviderFailure.FromException(new InvalidOperationException("bad"));

        Assert.Equal(ProviderFailureKind.ContractViolation, failure.Kind);
    }

    [Fact]
    public void Unwraps_inner_failures()
    {
        var failure = ProviderFailure.FromException(new InvalidOperationException("wrapper", new FormatException("bad")));

        Assert.Equal(ProviderFailureKind.InvalidOutput, failure.Kind);
    }

    [Fact]
    public void Preserves_provider_failure_exception()
    {
        var expected = new ProviderFailure(ProviderFailureKind.Transport, "down", "provider", "System.Net.Http.HttpRequestException");
        var failure = ProviderFailure.FromException(new ProviderFailureException(expected));

        Assert.Same(expected, failure);
    }
}
