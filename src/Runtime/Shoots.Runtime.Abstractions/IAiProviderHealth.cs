using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic health state for AI providers.
/// </summary>
public enum AiProviderHealthStatus
{
    Online,
    Degraded,
    Offline
}

/// <summary>
/// AI provider health contract (immutable).
/// </summary>
public interface IAiProviderHealth
{
    ProviderId ProviderId { get; }

    AiProviderHealthStatus Status { get; }

    string Detail { get; }
}
