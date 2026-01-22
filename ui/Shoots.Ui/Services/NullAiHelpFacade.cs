using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class NullAiHelpFacade : IAiHelpFacade
{
    public Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;
        return Task.FromResult("AI Help is offline.");
    }

    public Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;
        return Task.FromResult("No runtime context is available.");
    }

    public Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;
        return Task.FromResult("AI Help is descriptive only.");
    }
}
