namespace Shoots.Runtime.Abstractions;

public interface IRunResumeStateStore
{
    RunResumeState? Load(string planId);

    void Save(string planId, RunResumeState state);
}
