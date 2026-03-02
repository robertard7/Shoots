#if false
using System;
using System.Collections.Generic;

namespace Shoots.UI.ExecutionEnvironments;

/// <summary>
/// UI-facing settings wrapper.
/// This file must not depend on Shoots.Runtime.* assemblies.
/// </summary>
public sealed record ExecutionEnvironmentSettings(
    string ActiveRootFsId,
    IReadOnlyList<RootFsDescriptor> RootFsCatalog,
    string? RootFsSourceOverride);

/// <summary>
/// UI-safe RootFs descriptor (do not bind to runtime assembly types).
/// </summary>

#endif
