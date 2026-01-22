using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core.Backends.RootFs;

public sealed record RootFsProvisionRequest(
    RootFsDescriptor Descriptor,
    string CachePath
);

public sealed record RootFsProvisionResult(
    RootFsProvenance Provenance
);

public interface IRootFsProvider
{
    string ProviderId { get; }

    bool CanHandle(RootFsDescriptor descriptor);

    Task<RootFsProvisionResult> ProvideAsync(
        RootFsProvisionRequest request,
        CancellationToken ct = default);
}
