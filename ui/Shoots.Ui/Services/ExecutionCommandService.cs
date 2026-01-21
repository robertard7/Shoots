using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class ExecutionCommandService : IExecutionCommandService
{
    private readonly IRuntimeFacade _runtimeFacade;

    public ExecutionCommandService(IRuntimeFacade runtimeFacade)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
    }

    public Task<RuntimeResult> StartAsync(BuildPlan plan, CancellationToken ct = default)
    {
        return _runtimeFacade.StartExecution(plan, ct);
    }

    public Task CancelAsync(CancellationToken ct = default)
    {
        return _runtimeFacade.CancelExecution(ct);
    }

    public Task<IRuntimeStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        return _runtimeFacade.QueryStatus(ct);
    }
}
