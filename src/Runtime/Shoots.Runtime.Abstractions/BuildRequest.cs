namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Canonical build request sent from the builder to the runtime.
/// </summary>
public sealed record BuildRequest(
    string CommandId,
    IReadOnlyDictionary<string, object?> Args
);
