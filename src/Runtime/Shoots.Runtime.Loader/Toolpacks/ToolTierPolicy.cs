using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader.Toolpacks;

public sealed class ToolTierPolicy
{
    public ToolTierPolicy(ToolTier allowedTier)
    {
        AllowedTier = allowedTier;
    }

    public ToolTier AllowedTier { get; }

    public bool Allows(ToolTier tier) => (int)tier <= (int)AllowedTier;

    public IReadOnlyList<ToolpackManifest> Filter(IEnumerable<ToolpackManifest> toolpacks)
    {
        if (toolpacks is null)
            throw new ArgumentNullException(nameof(toolpacks));

        return toolpacks
            .Where(pack => Allows(pack.Tier))
            .OrderBy(pack => pack.ToolpackId, StringComparer.Ordinal)
            .ToList();
    }

    public static ToolTierPolicy FromSnapshot(IToolpackPolicySnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        return new ToolTierPolicy(ToToolTier(snapshot.AllowedTier));
    }

    private static ToolTier ToToolTier(ToolpackTier tier) =>
        tier switch
        {
            ToolpackTier.Public => ToolTier.Public,
            ToolpackTier.Developer => ToolTier.Developer,
            ToolpackTier.System => ToolTier.System,
            _ => ToolTier.Public
        };
}
