using System;
using System.Collections.Generic;
using System.Text;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class AiHelpFacade : IAiHelpFacade
{
    private readonly IRuntimeFacade _runtimeFacade;
    private readonly IRuntimeNarratorSummary _narratorSummary;

    public AiHelpFacade(IRuntimeFacade runtimeFacade, IRuntimeNarratorSummary narratorSummary)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
        _narratorSummary = narratorSummary ?? throw new ArgumentNullException(nameof(narratorSummary));
    }

    public async Task<string> GetContextSummaryAsync(AiHelpContext context, CancellationToken ct = default)
    {
        var status = await _runtimeFacade.QueryStatus(ct).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine("Informational assistance only.");
        builder.AppendLine(_narratorSummary.DescribeRuntime(status.Version));
        builder.AppendLine(DescribeWorkspace(context));
        return builder.ToString().Trim();
    }

    public async Task<string> ExplainStateAsync(AiHelpContext context, CancellationToken ct = default)
    {
        var status = await _runtimeFacade.QueryStatus(ct).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine("State summary:");
        builder.AppendLine(_narratorSummary.DescribeRuntime(status.Version));
        if (!string.IsNullOrWhiteSpace(context.ExecutionState))
            builder.AppendLine($"Execution state: {context.ExecutionState}.");
        if (!string.IsNullOrWhiteSpace(context.EnvironmentProfile))
            builder.AppendLine($"Environment profile: {context.EnvironmentProfile}.");
        if (!string.IsNullOrWhiteSpace(context.LastAppliedProfile))
            builder.AppendLine($"Last applied: {context.LastAppliedProfile}.");
        return builder.ToString().Trim();
    }

    public Task<string> SuggestNextStepsAsync(AiHelpContext context, CancellationToken ct = default)
    {
        _ = ct;
        var steps = new List<string>
        {
            "Review the current workspace and environment summary.",
            "Confirm the execution plan before starting a run.",
            "Apply environment profiles or scripts only when needed."
        };

        if (string.IsNullOrWhiteSpace(context.WorkspaceName))
            steps.Insert(0, "Select a workspace to scope context.");

        return Task.FromResult(string.Join(" ", steps));
    }

    private static string DescribeWorkspace(AiHelpContext context)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceName))
            return "Workspace: none selected.";

        if (string.IsNullOrWhiteSpace(context.WorkspaceRootPath))
            return $"Workspace: {context.WorkspaceName}.";

        return $"Workspace: {context.WorkspaceName} ({context.WorkspaceRootPath}).";
    }
}
