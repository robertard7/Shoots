namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeInput(
    string Command,
    IReadOnlyDictionary<string, object?> Parameters
);
