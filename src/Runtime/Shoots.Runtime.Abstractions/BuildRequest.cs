namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Canonical, immutable build request sent from the builder to the runtime.
/// </summary>
/// <param name="CommandId">Input command identifier (caller-provided).</param>
/// <param name="Args">Input arguments for the command (caller-provided).</param>
public sealed record BuildRequest(
    string CommandId,
    IReadOnlyDictionary<string, object?> Args
);
