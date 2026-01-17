namespace Shoots.Runtime.Abstractions;

public interface IRuntimeNarrator
{
    void OnPlan(string text);
    void OnCommand(RuntimeCommandSpec command, RuntimeRequest request);
    void OnResult(RuntimeResult result);
    void OnError(RuntimeError error);
}
