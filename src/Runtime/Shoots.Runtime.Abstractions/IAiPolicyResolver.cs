using Shoots.Contracts.Core.AI;

namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Resolves AI presentation policy before UI is constructed.
/// </summary>
public interface IAiPolicyResolver
{
    AiPresentationPolicy Resolve(AiAccessRole accessRole);
}
