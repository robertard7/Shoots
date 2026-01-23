#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Ui.Services;

/// <summary>
/// UI-facing execution command surface.
/// Passive. Delegates all authority to RuntimeFacade.
/// </summary>
public interface IExecutionCommandService
{
    Task<RuntimeResult> StartAsync(BuildPlan plan, CancellationToken ct = default);

    Task CancelAsync(CancellationToken ct = default);

    Task<IRuntimeStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default);
}

public sealed class ExecutionCommandService : IExecutionCommandService
{
    private readonly IRuntimeFacade _runtimeFacade;

    public ExecutionCommandService(IRuntimeFacade runtimeFacade)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
    }

    public Task<RuntimeResult> StartAsync(BuildPlan plan, CancellationToken ct = default)
        => _runtimeFacade.StartExecution(plan, ct);

    public Task CancelAsync(CancellationToken ct = default)
        => _runtimeFacade.CancelExecution(ct);

    public Task<IRuntimeStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
        => _runtimeFacade.QueryStatus(ct);
}
