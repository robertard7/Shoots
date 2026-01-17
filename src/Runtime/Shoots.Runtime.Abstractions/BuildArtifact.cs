namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Declares an expected artifact produced by a plan.
/// </summary>
/// <param name="Id">Stable artifact identifier.</param>
/// <param name="Description">Human-readable artifact description.</param>
public sealed record BuildArtifact(
    string Id,
    string Description
);
