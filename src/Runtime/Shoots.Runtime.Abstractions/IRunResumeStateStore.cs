namespace Shoots.Runtime.Abstractions;

public interface IRunResumeStateStore
{
    RunResumeState? LoadByWorkOrderId(string workOrderId);

    void SaveByWorkOrderId(string workOrderId, RunResumeState state);
}
