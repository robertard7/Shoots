#nullable enable
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

public sealed class TextRuntimeNarrator : IRuntimeNarrator
{
    private readonly Action<string> _emit;

    public TextRuntimeNarrator(Action<string> emit)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    public void OnPlan(string text) => _emit($"[plan] {text}");

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        if (request is null) throw new ArgumentNullException(nameof(request));

        _emit($"[command] {command.CommandId} args={FormatArgs(request)}");
    }

    public void OnResult(RuntimeResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        if (result.Ok) _emit("[result] ok");
        else if (result.Error is not null) _emit($"[result] failed ({result.Error.Code})");
        else _emit("[result] failed");
    }

    public void OnError(RuntimeError error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
        _emit($"[error] {error.Code}: {error.Message}");
    }

    public void OnRoute(RouteNarration narration)
    {
        if (narration is null) throw new ArgumentNullException(nameof(narration));

        var step = narration.CurrentStep is null
            ? "none"
            : $"{narration.CurrentStep.NodeId}:{narration.CurrentStep.Intent}/{narration.CurrentStep.Owner}";

        var decision = narration.DecisionRequired ? "decision=required" : "decision=none";
        var halt = narration.HaltReason is null ? "halt=none" : $"halt={narration.HaltReason.Code}";

        _emit($"[route] workorder={narration.WorkOrder.Id.Value} step={step} {decision} {halt}");
    }

    private static string FormatArgs(RuntimeRequest request)
    {
        if (request.Args.Count == 0) return "{}";
        return "{ " + string.Join(", ", request.Args.Select(kv => $"{kv.Key}={kv.Value}")) + " }";
    }
}
