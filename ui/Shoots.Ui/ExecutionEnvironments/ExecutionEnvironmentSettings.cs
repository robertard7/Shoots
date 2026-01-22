using System.Collections.Generic;

namespace Shoots.UI.ExecutionEnvironments;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed record ExecutionEnvironmentSettings(
    string ActiveRootFsId,
    IReadOnlyList<RootFsDescriptor> RootFsCatalog
);

public sealed record RootFsDescriptor(
    string Id,
    string DisplayName,
    string SourceType,
    string? DefaultUrl,
    string? Checksum,
    string License,
    string? Notes
);
