namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeRequest(
    string CommandId,
    IReadOnlyDictionary<string, object?> Args,
    RuntimeContext Context
);
