using System;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

[Flags]
public enum EnvironmentCapability
{
    None = 0,
    SourceControl = 1 << 0,
    Database = 1 << 1,
    ArtifactExport = 1 << 2
}
