using System.Threading;
using System.Threading.Tasks;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpFacade
{
    Task<string> GetContextSummaryAsync(AiHelpContext context, CancellationToken ct = default);

    Task<string> ExplainStateAsync(AiHelpContext context, CancellationToken ct = default);

    Task<string> SuggestNextStepsAsync(AiHelpContext context, CancellationToken ct = default);
}

public sealed record AiHelpContext(
    string? WorkspaceName,
    string? WorkspaceRootPath,
    string? ExecutionState,
    string? RuntimeVersion,
    string? EnvironmentProfile,
    string? LastAppliedProfile);
