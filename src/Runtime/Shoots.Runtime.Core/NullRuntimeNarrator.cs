using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullRuntimeNarrator : IRuntimeNarrator
{
    public static readonly NullRuntimeNarrator Instance = new();

    public NullRuntimeNarrator()
    {
    }

    public void OnPlan(string text)
    {
        // Intentionally no-op
    }

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        if (request is null) throw new ArgumentNullException(nameof(request));
    }

    public void OnResult(RuntimeResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
    }

    public void OnError(RuntimeError error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
    }

    public void OnRoute(RouteNarration narration)
    {
        if (narration is null) throw new ArgumentNullException(nameof(narration));
    }
}
