namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeCommandSpec(
    string CommandId,        // e.g. "exec.run"
    string Description,
    IReadOnlyList<RuntimeArgSpec> Args
);
