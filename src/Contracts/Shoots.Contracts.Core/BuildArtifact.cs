namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable output contract for plan artifacts.
/// </summary>
public sealed record BuildArtifact(
    string Id,
    string Description
);
