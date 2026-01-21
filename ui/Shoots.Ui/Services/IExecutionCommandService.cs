using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public interface IExecutionCommandService
{
    Task<RuntimeResult> StartAsync(BuildPlan plan, CancellationToken ct = default);

    Task CancelAsync(CancellationToken ct = default);

    Task<IRuntimeStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default);
}
