namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Declares an expected artifact produced by a plan.
/// </summary>
/// <param name="Id">Stable artifact identifier.</param>
/// <param name="Description">Human-readable artifact description.</param>
public sealed record BuildArtifact(
    string Id,
    string Description
);
