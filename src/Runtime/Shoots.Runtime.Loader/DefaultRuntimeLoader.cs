using System.Reflection;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class DefaultRuntimeLoader : IRuntimeLoader
{
    public IReadOnlyList<IRuntimeModule> LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException(directoryPath);

        var modules = new List<IRuntimeModule>();

        foreach (var dll in Directory.EnumerateFiles(directoryPath, "*.dll"))
        {
            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(dll);
            }
            catch
            {
                continue; // bad assembly = ignored
            }

            RuntimeModuleManifestAttribute? manifest;
            try
            {
                manifest = asm.GetCustomAttribute<RuntimeModuleManifestAttribute>();
            }
            catch
            {
                continue;
            }

            if (manifest is null)
                continue;

            if (string.IsNullOrWhiteSpace(manifest.ModuleId))
                continue;

            var moduleType = manifest.ModuleType;

            if (moduleType.IsAbstract)
                continue;

            if (!typeof(IRuntimeModule).IsAssignableFrom(moduleType))
                continue;

            if (moduleType.GetConstructor(Type.EmptyTypes) is null)
                continue;

            try
            {
                if (Activator.CreateInstance(moduleType) is IRuntimeModule module)
                    modules.Add(module);
            }
            catch
            {
                continue;
            }
        }

        return modules;
    }

    // Compatibility is evaluated by the host, not during discovery.
    public bool IsCompatible(
        RuntimeVersion hostVersion,
        RuntimeVersion moduleVersion,
        out RuntimeError? error)
    {
        error = null;

        if (moduleVersion.Major != hostVersion.Major)
        {
            error = new RuntimeError(
                "incompatible_major",
                $"Module major {moduleVersion.Major} != host major {hostVersion.Major}"
            );
            return false;
        }

        if (moduleVersion.Minor > hostVersion.Minor)
        {
            error = new RuntimeError(
                "incompatible_minor",
                $"Module minor {moduleVersion.Minor} > host minor {hostVersion.Minor}"
            );
            return false;
        }

        return true;
    }
}
