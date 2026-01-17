namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeArgSpec(
    string Name,
    string Type,             // keep as string for now: "string", "int", "path", "json"
    bool Required,
    string Description
);
