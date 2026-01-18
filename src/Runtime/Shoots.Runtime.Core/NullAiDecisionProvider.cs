using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullAiDecisionProvider : IAiDecisionProvider
{
    public static readonly NullAiDecisionProvider Instance = new();

    public ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        RoutingState state)
    {
        _ = workOrder ?? throw new ArgumentNullException(nameof(workOrder));
        _ = step ?? throw new ArgumentNullException(nameof(step));
        _ = state ?? throw new ArgumentNullException(nameof(state));

        return null;
    }
}
