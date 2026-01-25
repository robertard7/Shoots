using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// AI provider capability contract (immutable).
/// </summary>
public interface IAiProviderCapabilities
{
    ProviderId ProviderId { get; }

    IReadOnlyList<string> Capabilities { get; }
}
