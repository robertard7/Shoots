using Shoots.Runtime.Abstractions;

namespace Shoots.UI.ViewModels;

internal static class ExecutionEnvelopeDtoExtensions
{
    public static string GetExecutionId(this ExecutionEnvelope envelope)
        => envelope.Plan.Request.WorkOrder?.Id.Value
            ?? envelope.Plan.PlanId
            ?? string.Empty;

    public static string GetExecutionId(this ExecutionEnvelopeDto envelope)
        => envelope.PlanId;
}
