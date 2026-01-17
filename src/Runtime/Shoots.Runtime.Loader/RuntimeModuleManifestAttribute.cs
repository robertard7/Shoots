using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Loader;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RuntimeModuleManifestAttribute : Attribute
{
    public string ModuleId { get; }
    public Type ModuleType { get; }

    public RuntimeVersion MinRuntimeVersion { get; }
    public RuntimeVersion MaxRuntimeVersion { get; }

    public RuntimeModuleManifestAttribute(
        string moduleId,
        Type moduleType,
        int minMajor,
        int minMinor,
        int minPatch,
        int maxMajor,
        int maxMinor,
        int maxPatch)
    {
        ModuleId = moduleId ?? string.Empty;
        ModuleType = moduleType ?? throw new ArgumentNullException(nameof(moduleType));

        MinRuntimeVersion = new RuntimeVersion(minMajor, minMinor, minPatch);
        MaxRuntimeVersion = new RuntimeVersion(maxMajor, maxMinor, maxPatch);
    }
}
