using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public interface IAiDecisionProvider
{
    /// <summary>
    /// Request a deterministic tool selection. Providers do not control routing and returning
    /// a tool does not advance the graph.
    /// </summary>
    ToolSelectionDecision? RequestDecision(AiDecisionRequest request);
}
