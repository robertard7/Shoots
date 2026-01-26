using System.Collections.Generic;
using Shoots.Runtime.Abstractions;

namespace Shoots.UI.ExecutionEnvironments;

// UI-facing settings wrapper, runtime-owned data
public sealed record ExecutionEnvironmentSettings(
    string ActiveRootFsId,
    IReadOnlyList<RootFsDescriptor> RootFsCatalog,
    string? RootFsSourceOverride
);

