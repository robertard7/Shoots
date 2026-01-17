namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Entry point for executing commands against the runtime.
/// Host implementations are responsible for wiring modules and providing environment.
/// </summary>
public interface IRuntimeHost
{
    RuntimeVersion Version { get; }

    RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default);
}
