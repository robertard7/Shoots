namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable intent token carried through routing decisions.
/// </summary>
public readonly record struct RouteIntentToken(
    string ConstraintsHash,
    string CurrentStateHash
);
