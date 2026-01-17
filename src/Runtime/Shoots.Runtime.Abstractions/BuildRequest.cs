namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Canonical, immutable build request sent from the builder to the runtime.
/// </summary>
/// <param name="CommandId">Input command identifier (caller-provided).</param>
/// <param name="Args">Input arguments for the command (caller-provided).</param>
public sealed record BuildRequest(
    string CommandId,
    IReadOnlyDictionary<string, object?> Args
);
