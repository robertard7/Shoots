using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core.AI;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Loader;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RuntimeFacadeBoundaryTests
{
    [Fact]
    public async Task RuntimeFacadeLoadsWithoutUi()
    {
        var facade = new RuntimeFacade(new StubRuntimeHost());

        var status = await facade.QueryStatus();

        Assert.Equal(new RuntimeVersion(0, 0, 0), status.Version);
        Assert.Equal(ComputePolicyHash(AiVisibilityMode.Visible, true, true, false), status.PolicyHash);
    }

    [Fact]
    public async Task RuntimeFacadeAllowsHiddenAiPolicy()
    {
        var facade = new RuntimeFacade(
            new StubRuntimeHost(),
            new StubPolicyResolver());

        var status = await facade.QueryStatus();

        Assert.Equal(new RuntimeVersion(0, 0, 0), status.Version);
        Assert.Equal(ComputePolicyHash(AiVisibilityMode.HiddenForEndUsers, false, false, true), status.PolicyHash);
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
                AiVisibilityMode.HiddenForEndUsers,
                AllowAiPanelToggle: false,
                AllowCopyExport: false,
                EnterpriseMode: true);
        }
    }

    private static string ComputePolicyHash(
        AiVisibilityMode visibility,
        bool allowPanelToggle,
        bool allowCopyExport,
        bool enterpriseMode)
    {
        var value = $"{visibility}|{allowPanelToggle}|{allowCopyExport}|{enterpriseMode}";
        return HashTools.ComputeSha256Hash(value);
    }
}
