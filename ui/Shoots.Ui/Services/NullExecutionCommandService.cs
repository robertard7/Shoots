#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class NullExecutionCommandService : IExecutionCommandService
{
    public Task<RuntimeResult> StartAsync(BuildPlan plan, CancellationToken ct = default)
    {
        _ = plan;
        _ = ct;

        return Task.FromResult(
            RuntimeResult.Fail(
                RuntimeError.Internal("Execution command service is not configured.")
            )
        );
    }

    public Task CancelAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
    }

    public Task<IRuntimeStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        _ = ct;

        return Task.FromResult<IRuntimeStatusSnapshot>(
            new NullRuntimeStatusSnapshot(new RuntimeVersion(0, 0, 0))
        );
    }

    private sealed record NullRuntimeStatusSnapshot(RuntimeVersion Version)
        : IRuntimeStatusSnapshot;
}
