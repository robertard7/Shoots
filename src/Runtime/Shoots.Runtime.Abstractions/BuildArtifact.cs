namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Declares an expected artifact produced by a plan.
/// </summary>
public sealed record BuildArtifact(
    string Id,
    string Description
);
