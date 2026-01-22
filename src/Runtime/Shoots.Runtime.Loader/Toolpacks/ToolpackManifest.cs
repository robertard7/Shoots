using System.Collections.Generic;
using System.Text.Json;

namespace Shoots.Runtime.Loader.Toolpacks;

public sealed record ToolpackManifest(
    string ToolpackId,
    string Version,
    ToolTier Tier,
    string Description,
    IReadOnlyList<ToolpackTool> Tools,
    JsonElement? Requires,
    string RiskNotes,
    string SourcePath
);

public sealed record ToolpackTool(
    string ToolId,
    string? Description,
    ToolpackAuthority Authority,
    IReadOnlyList<ToolpackInput>? Inputs,
    IReadOnlyList<ToolpackOutput>? Outputs,
    IReadOnlyList<string>? Tags
);

public sealed record ToolpackAuthority(
    string ProviderKind,
    IReadOnlyList<string>? Capabilities
);

public sealed record ToolpackInput(
    string Name,
    string Type,
    string? Description,
    bool Required
);

public sealed record ToolpackOutput(
    string Name,
    string Type,
    string? Description
);
