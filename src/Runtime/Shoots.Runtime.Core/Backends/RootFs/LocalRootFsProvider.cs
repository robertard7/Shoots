using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core.Backends.RootFs;

public sealed class LocalRootFsProvider : IRootFsProvider
{
    public string ProviderId => "local";

    public bool CanHandle(RootFsDescriptor descriptor) =>
        descriptor.SourceType == RootFsSourceType.LocalPath;

    public Task<RootFsProvisionResult> ProvideAsync(
        RootFsProvisionRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(request.Descriptor.DefaultUrl))
            throw new ArgumentException("DefaultUrl is required for local rootfs.");

        Directory.CreateDirectory(request.CachePath);
        var pointerPath = Path.Combine(request.CachePath, "rootfs.path");
        File.WriteAllText(pointerPath, request.Descriptor.DefaultUrl);

        var metadata = new Dictionary<string, string>
        {
            ["path"] = request.Descriptor.DefaultUrl,
            ["pointer"] = pointerPath
        };

        var provenance = new RootFsProvenance(
            ProviderId,
            request.Descriptor.DefaultUrl,
            request.Descriptor.Checksum,
            DateTimeOffset.UtcNow,
            metadata);

        return Task.FromResult(new RootFsProvisionResult(provenance));
    }
}
