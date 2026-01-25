using System.Collections.Generic;

namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic UI surface contract (screen, panel, editor, graph).
/// </summary>
public interface IAiSurface
{
    string SurfaceId { get; }
    string SurfaceKind { get; }
    string DisplayName { get; }
    IReadOnlyList<string> Capabilities { get; }
}
