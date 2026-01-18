using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// AI provider boundary (contracts only).
/// </summary>
public interface IAiProvider
{
    AiResponse Invoke(AiRequest request);
}
