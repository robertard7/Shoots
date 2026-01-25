using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IRuntimeFacade
{
    Task<RuntimeResult> StartExecution(BuildPlan plan, CancellationToken ct = default);

    Task<IRuntimeStatusSnapshot> QueryStatus(CancellationToken ct = default);

    IAsyncEnumerable<RoutingTraceEntry> SubscribeTrace(CancellationToken ct = default);

    Task CancelExecution(CancellationToken ct = default);
}

public interface IRuntimeStatusSnapshot
{
    RuntimeVersion Version { get; }

    string PolicyHash { get; }
}
