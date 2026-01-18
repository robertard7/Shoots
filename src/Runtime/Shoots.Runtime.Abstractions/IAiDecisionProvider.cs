using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public interface IAiDecisionProvider
{
    ToolSelectionDecision? RequestDecision(AiDecisionRequest request);
}
