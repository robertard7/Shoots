using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class NullAiHelpFacade : IAiHelpFacade
{
    public Task<string> GetContextSummaryAsync(AiHelpContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return Task.FromResult("AI Help is unavailable.");
    }

    public Task<string> ExplainStateAsync(AiHelpContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return Task.FromResult("No runtime context is available.");
    }

    public Task<string> SuggestNextStepsAsync(AiHelpContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return Task.FromResult("AI Help is informational only.");
    }
}
