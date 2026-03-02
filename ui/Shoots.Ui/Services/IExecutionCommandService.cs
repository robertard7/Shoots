#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;

namespace Shoots.UI.Services;

/// <summary>
/// UI-facing execution command surface.
/// Must NOT depend on Shoots.Runtime.* assemblies.
/// </summary>
public interface IExecutionCommandService
{
    Task<ExecutionStartResult> StartAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default);

    Task CancelAsync(CancellationToken ct = default);

    Task<ExecutionStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default);
}