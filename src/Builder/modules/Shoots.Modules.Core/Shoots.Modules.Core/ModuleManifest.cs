using Shoots.Runtime.Loader;
using Shoots.Modules.Core;

[assembly: RuntimeModuleManifest(
    moduleId: "core",
    moduleType: typeof(CoreModule),
    minMajor: 1, minMinor: 0, minPatch: 0,
    maxMajor: 1, maxMinor: 9, maxPatch: 0
)]
