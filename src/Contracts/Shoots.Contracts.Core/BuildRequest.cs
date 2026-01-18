namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable request for planning.
/// </summary>
public sealed record BuildRequest(
    string CommandId,
    IReadOnlyDictionary<string, object?> Args
);
