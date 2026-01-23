using Shoots.Contracts.Core;

namespace Shoots.UI.ViewModels;

public sealed class ExecutionStepSummaryViewModel
{
    public ExecutionStepSummaryViewModel(
        string id,
        string description,
        string ownerLabel,
        string ownerColor,
        string ownerHint)
    {
        Id = id;
        Description = description;
        OwnerLabel = ownerLabel;
        OwnerColor = ownerColor;
        OwnerHint = ownerHint;
    }

    public string Id { get; }

    public string Description { get; }

    public string OwnerLabel { get; }

    public string OwnerColor { get; }

    public string OwnerHint { get; }

    public static ExecutionStepSummaryViewModel FromStep(BuildStep step)
    {
        var owner = step is RouteStep routeStep ? routeStep.Owner : DecisionOwner.Runtime;
        var ownerLabel = owner switch
        {
            DecisionOwner.Ai => "AI",
            DecisionOwner.Human => "Human",
            DecisionOwner.Rule => "Rule",
            _ => "Runtime"
        };

        var ownerColor = owner switch
        {
            DecisionOwner.Ai => "#FF2563EB",
            DecisionOwner.Human => "#FF16A34A",
            DecisionOwner.Rule => "#FF7C3AED",
            _ => "#FF6B7280"
        };

        var ownerHint = owner switch
        {
            DecisionOwner.Ai => "Decision suggested by AI.",
            DecisionOwner.Human => "Decision approved by a human.",
            DecisionOwner.Rule => "Decision enforced by deterministic rules.",
            _ => "Decision owned by runtime defaults."
        };

        return new ExecutionStepSummaryViewModel(
            step.Id,
            step.Description,
            ownerLabel,
            ownerColor,
            ownerHint);
    }
}
