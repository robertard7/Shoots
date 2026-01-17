namespace Shoots.Runtime.Abstractions;

/// <summary>
/// A module provides one or more commands. Modules must be deterministic and side-effect
/// safe except through declared capabilities (handled by the host).
/// </summary>
public interface IRuntimeModule
{
    string ModuleId { get; }          // e.g. "core.exec"
    RuntimeVersion ModuleVersion { get; }
	RuntimeVersion MinRuntimeVersion { get; }
	RuntimeVersion MaxRuntimeVersion { get; }


    /// <summary>
    /// Returns the set of commands exposed by this module.
    /// </summary>
    IReadOnlyList<RuntimeCommandSpec> Describe();

    /// <summary>
    /// Executes a single command by id.
    /// </summary>
    RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default);
}
