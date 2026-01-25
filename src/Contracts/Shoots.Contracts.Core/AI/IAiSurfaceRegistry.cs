using System.Collections.Generic;

namespace Shoots.Contracts.Core.AI;

/// <summary>
/// Deterministic narration hook options for a UI surface.
/// </summary>
public enum AiNarrationHook
{
    Plan,
    State,
    Error,
    Result
}

/// <summary>
/// Deterministic AI surface registration metadata.
/// </summary>
public sealed record AiSurfaceRegistration(
    string SurfaceId,
    string SurfaceKind,
    string DisplayName,
    IReadOnlyList<string> DeclaredIntents,
    IReadOnlyList<AiNarrationHook> NarrationHooks,
    string SnapshotProvider
);

/// <summary>
/// Registry for UI surfaces that opt into AI narration.
/// </summary>
public interface IAiSurfaceRegistry
{
    void Register(AiSurfaceRegistration registration);

    IReadOnlyList<AiSurfaceRegistration> RegisteredSurfaces { get; }
}
