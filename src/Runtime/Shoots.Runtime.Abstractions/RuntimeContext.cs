namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Execution context provided by the host.
/// </summary>
public sealed record RuntimeContext(
    string SessionId,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Env,
    IRuntimeServices? Services = null
);
