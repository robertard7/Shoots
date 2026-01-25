using System.Collections.Generic;

namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic intent contract (what the user is trying to do).
/// </summary>
public interface IAiIntent
{
    string IntentId { get; }
    string Goal { get; }
    IReadOnlyList<string> Constraints { get; }
}
