using System;
using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Blueprints;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed record SystemBlueprint(
    string Name,
    string Description,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> Artifacts,
    DateTimeOffset CreatedUtc,
    string Version = "1.0",
    string Definition = ""
) : IAiHelpSurface
{
    public string SurfaceId => $"blueprint:{Name}";

    public string SurfaceKind => $"Blueprint {Name}";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Blueprint),
        new AiIntentDescriptor(AiIntentType.Validate, AiIntentScope.Blueprint),
        new AiIntentDescriptor(AiIntentType.Suggest, AiIntentScope.Blueprint)
    };

    public string DescribeContext()
    {
        var summary = string.IsNullOrWhiteSpace(Description) ? "No description available." : Description.Trim();
        return $"Blueprint '{Name}' v{Version} created {CreatedUtc:u}. {summary}";
    }

    public string DescribeCapabilities()
    {
        if (Intents.Count == 0)
            return "No intents are listed.";

        return $"Intents: {string.Join("; ", Intents)}.";
    }

    public string DescribeConstraints()
    {
        if (Artifacts.Count == 0)
            return "No artifacts are listed.";

        return $"Artifacts: {string.Join("; ", Artifacts)}.";
    }
}
