using System.Collections.Generic;

namespace Shoots.Contracts.Core.AI;

/// <summary>
/// Deterministic narration contract (what the system explains back).
/// </summary>
public interface IAiNarration
{
    string Summary { get; }
    IReadOnlyList<string> Reasons { get; }
    IReadOnlyList<string> SuggestedNextSteps { get; }
}

/// <summary>
/// Narrator contract requiring explicit surface + intent.
/// </summary>
public interface IAiNarrator
{
    IAiNarration Narrate(IAiSurface surface, IAiIntent intent, IReadOnlyList<string> context);
}
