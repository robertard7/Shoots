namespace Shoots.UI.Blueprints;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed record SystemBlueprint(
    string Name,
    string Description,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> Artifacts,
    DateTimeOffset CreatedUtc
);
