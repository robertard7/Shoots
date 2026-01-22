using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core.Backends.RootFs;

namespace Shoots.Runtime.Core.Backends;

public sealed class LinuxSandboxBackend : IExecutionBackend
{
    private readonly IReadOnlyList<IRootFsProvider> _providers;
    private readonly RootFsCache _cache;

    public LinuxSandboxBackend(IEnumerable<IRootFsProvider> providers, RootFsCache cache)
    {
        if (providers is null)
            throw new ArgumentNullException(nameof(providers));
        _providers = providers.ToList();
        if (_providers.Count == 0)
            throw new ArgumentException("at least one provider is required", nameof(providers));

        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<ExecutionBackendSnapshot> PrepareAsync(
        RootFsDescriptor descriptor,
        CancellationToken ct = default)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));

        var cachePath = _cache.GetCachePath(descriptor);
        if (_cache.TryLoadProvenance(cachePath, out var provenance))
            return new ExecutionBackendSnapshot(descriptor, cachePath, provenance);

        var provider = _providers.FirstOrDefault(p => p.CanHandle(descriptor));
        if (provider is null)
            throw new InvalidOperationException($"No provider for {descriptor.SourceType}.");

        var request = new RootFsProvisionRequest(descriptor, cachePath);
        var result = await provider.ProvideAsync(request, ct).ConfigureAwait(false);
        _cache.SaveProvenance(cachePath, result.Provenance);

        return new ExecutionBackendSnapshot(descriptor, cachePath, result.Provenance);
    }
}
