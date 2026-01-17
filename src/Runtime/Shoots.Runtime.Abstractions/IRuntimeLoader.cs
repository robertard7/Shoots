namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Loads modules from disk or other sources and enforces compatibility.
/// </summary>
public interface IRuntimeLoader
{
    /// <summary>
    /// Loads modules from a directory (dlls). Loader decides the scanning strategy.
    /// </summary>
    IReadOnlyList<IRuntimeModule> LoadFromDirectory(string directoryPath);

    /// <summary>
    /// Validates that a module can run under a host/runtime version.
    /// </summary>
    bool IsCompatible(RuntimeVersion hostVersion, RuntimeVersion moduleVersion, out RuntimeError? error);
}
