using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Loader;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class PolicyHashTripwireTests
{
    [Fact]
    public async Task PolicyHash_uses_frozen_field_order_and_format()
    {
        var facade = new RuntimeFacade(
            new StubRuntimeHost(),
            new StubPolicyResolver());

        var status = await facade.QueryStatus();

        var expected = $"{AiVisibilityMode.AdminOnly}|{true}|{false}|{true}";
        var expectedHash = HashTools.ComputeSha256Hash(expected);

        Assert.Matches("^[0-9a-f]{64}$", status.PolicyHash);
        Assert.Equal(expectedHash, status.PolicyHash);
    }

    private sealed class StubRuntimeHost : IRuntimeHost
    {
        public RuntimeVersion Version => new(0, 0, 0);

        public RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return RuntimeResult.Fail(RuntimeError.Internal("Stub runtime host."));
        }
    }

    private sealed class StubPolicyResolver : IAiPolicyResolver
    {
        public AiPresentationPolicy Resolve(AiAccessRole accessRole)
        {
            _ = accessRole;
            return new AiPresentationPolicy(
                AiVisibilityMode.AdminOnly,
                AllowAiPanelToggle: true,
                AllowCopyExport: false,
                EnterpriseMode: true);
        }
    }
}
