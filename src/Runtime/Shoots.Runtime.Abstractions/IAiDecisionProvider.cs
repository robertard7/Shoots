using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public interface IAiDecisionProvider
{
    RouteDecision? RequestDecision(AiDecisionRequest request);
}
