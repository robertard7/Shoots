namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Persistence boundary for execution envelopes.
/// </summary>
public interface IRuntimePersistence
{
    void Save(ExecutionEnvelope envelope);

    ExecutionEnvelope? Load(string planId);
}
