#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
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

        var status = await facade.QueryStatusAsync(CancellationToken.None);

        AssertVersion(status.Version, 0, 0, 0);
        Assert.Equal(
            ComputePolicyHash(
                AiVisibilityMode.Visible,
                allowPanelToggle: true,
                allowCopyExport: true,
                enterpriseMode: false),
            status.PolicyHash);
    }

    [Fact]
    public async Task RuntimeFacadeAllowsHiddenAiPolicy()
    {
        var facade = new RuntimeFacade(
            new StubRuntimeHost(),
            new StubPolicyResolver());

        var status = await facade.QueryStatusAsync(CancellationToken.None);

        AssertVersion(status.Version, 0, 0, 0);
        Assert.Equal(
            ComputePolicyHash(
                AiVisibilityMode.HiddenForEndUsers,
                allowPanelToggle: false,
                allowCopyExport: false,
                enterpriseMode: true),
            status.PolicyHash);
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

    private static void AssertVersion(object? versionObj, int expectedMajor, int expectedMinor, int expectedPatch)
    {
        Assert.NotNull(versionObj);

        var (major, minor, patch) = ReadVersionTriple(versionObj!);

        Assert.Equal(expectedMajor, major);
        Assert.Equal(expectedMinor, minor);
        Assert.Equal(expectedPatch, patch);
    }

    private static (int Major, int Minor, int Patch) ReadVersionTriple(object versionObj)
    {
        // Supports both RuntimeVersion and RuntimeVersionInfo (and anything else
        // that exposes Major/Minor/Patch or VersionMajor/VersionMinor/VersionPatch).
        var t = versionObj.GetType();

        static int ReadIntProp(Type t, object o, string name)
        {
            var p = t.GetProperty(name);
            if (p is null) return int.MinValue;

            var v = p.GetValue(o);
            if (v is int i) return i;
            if (v is short s) return s;
            if (v is byte b) return b;

            // Some people store numbers as strings because they hate everyone.
            if (v is string str && int.TryParse(str, out var parsed)) return parsed;

            return int.MinValue;
        }

        var major = ReadIntProp(t, versionObj, "Major");
        var minor = ReadIntProp(t, versionObj, "Minor");
        var patch = ReadIntProp(t, versionObj, "Patch");

        if (major != int.MinValue && minor != int.MinValue && patch != int.MinValue)
            return (major, minor, patch);

        major = ReadIntProp(t, versionObj, "VersionMajor");
        minor = ReadIntProp(t, versionObj, "VersionMinor");
        patch = ReadIntProp(t, versionObj, "VersionPatch");

        if (major != int.MinValue && minor != int.MinValue && patch != int.MinValue)
            return (major, minor, patch);

        // Last resort: ToString() parsing (gross, but deterministic).
        var s = versionObj.ToString() ?? string.Empty;
        var parts = s.Split(new[] { '.', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], out major) &&
            int.TryParse(parts[1], out minor) &&
            int.TryParse(parts[2], out patch))
            return (major, minor, patch);

        throw new InvalidOperationException(
            $"Unable to read version triple from type '{t.FullName}'. Value: '{s}'.");
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