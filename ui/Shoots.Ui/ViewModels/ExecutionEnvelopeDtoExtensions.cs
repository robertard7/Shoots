using Shoots.Runtime.Abstractions;

namespace Shoots.UI.ViewModels;

internal static class ExecutionEnvelopeDtoExtensions
{
    public static string GetExecutionId(this ExecutionEnvelopeDto envelope)
        => envelope.PlanId;
}
