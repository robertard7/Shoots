using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.AiHelp;

public sealed class BlueprintCatalogAiHelpSurface : IAiHelpSurface
{
    private readonly IReadOnlyList<string> _blueprintNames;

    public BlueprintCatalogAiHelpSurface(IReadOnlyList<string> blueprintNames)
    {
        _blueprintNames = blueprintNames;
    }

    public string SurfaceId => "blueprints";

    public string SurfaceKind => "Blueprint Catalog";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Blueprint),
        new AiIntentDescriptor(AiIntentType.Suggest, AiIntentScope.Blueprint)
    };

    public string DescribeContext()
        => _blueprintNames.Count == 0
            ? "No blueprints are registered."
            : $"{_blueprintNames.Count} blueprints are registered.";

    public string DescribeCapabilities()
        => _blueprintNames.Count == 0
            ? "Blueprint editing is available when items are added."
            : $"Blueprints: {string.Join(", ", _blueprintNames.Take(5))}.";

    public string DescribeConstraints()
        => "Blueprint edits are stored per workspace.";
}
