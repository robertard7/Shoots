using System.IO;

namespace Shoots.UI.Environment;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
// Sandbox preparation is limited to idempotent file operations.
public sealed class EnvironmentProfileService
{
    private readonly IReadOnlyList<IEnvironmentProfile> _profiles;
    private readonly IEnvironmentProfileLogger _logger;
    private EnvironmentProfileResult? _lastResult;

    public EnvironmentProfileService()
        : this(new IEnvironmentProfile[]
        {
            new StandardEnvironmentProfile(),
            new ExtendedEnvironmentProfile()
        }, new NullEnvironmentProfileLogger())
    {
    }

    public EnvironmentProfileService(
        IReadOnlyList<IEnvironmentProfile> profiles,
        IEnvironmentProfileLogger logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<IEnvironmentProfile> Profiles => _profiles;

    public EnvironmentCapability AvailableCapabilities { get; private set; } = EnvironmentCapability.None;

    public EnvironmentProfileResult ApplyProfile(string sandboxRoot, IEnvironmentProfile profile)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
            throw new ArgumentException("sandbox root is required", nameof(sandboxRoot));
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));
        if (!Directory.Exists(sandboxRoot))
            throw new DirectoryNotFoundException($"Sandbox root not found: {sandboxRoot}");

        ValidateProfile(profile);

        if (_lastResult is not null && string.Equals(_lastResult.ProfileName, profile.Name, StringComparison.Ordinal))
            return _lastResult;

        var createdPaths = new List<string>();

        foreach (var step in profile.SandboxPreparationSteps)
        {
            var createdPath = ApplyStep(sandboxRoot, step);
            if (!string.IsNullOrWhiteSpace(createdPath))
                createdPaths.Add(createdPath);
        }

        AvailableCapabilities = profile.DeclaredCapabilities;
        var result = new EnvironmentProfileResult(
            profile.Name,
            createdPaths,
            profile.DeclaredCapabilities,
            DateTimeOffset.UtcNow);
        _lastResult = result;
        _logger.LogApplied(result);
        return result;
    }

    private static string? ApplyStep(string sandboxRoot, SandboxPreparationStep step)
    {
        if (string.IsNullOrWhiteSpace(step.RelativePath))
            throw new ArgumentException("sandbox step path is required", nameof(step));
        if (Path.IsPathRooted(step.RelativePath))
            throw new InvalidOperationException($"Sandbox step path must be relative: {step.RelativePath}");

        var targetPath = Path.Combine(sandboxRoot, step.RelativePath);
        var fullSandboxPath = Path.GetFullPath(sandboxRoot);
        var fullTargetPath = Path.GetFullPath(targetPath);

        if (!fullTargetPath.StartsWith(fullSandboxPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sandbox step path escapes root: {step.RelativePath}");

        if (step.Kind == SandboxPreparationKind.CreateDirectory)
        {
            if (Directory.Exists(fullTargetPath))
                return null;

            Directory.CreateDirectory(fullTargetPath);
            return fullTargetPath;
        }

        throw new InvalidOperationException($"Unknown sandbox preparation kind: {step.Kind}");
    }

    private static void ValidateProfile(IEnvironmentProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("Profile name is required.");
        if (profile.SandboxPreparationSteps is null)
            throw new InvalidOperationException("Profile steps are required.");
    }
}
