using System.IO;

namespace Shoots.UI.Environment;

public sealed class EnvironmentProfileService
{
    private readonly IReadOnlyList<IEnvironmentProfile> _profiles;

    public EnvironmentProfileService()
        : this(new IEnvironmentProfile[]
        {
            new StandardEnvironmentProfile(),
            new ExtendedEnvironmentProfile()
        })
    {
    }

    public EnvironmentProfileService(IReadOnlyList<IEnvironmentProfile> profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public IReadOnlyList<IEnvironmentProfile> Profiles => _profiles;

    public EnvironmentCapability AvailableCapabilities { get; private set; } = EnvironmentCapability.None;

    public void ApplyProfile(string sandboxRoot, IEnvironmentProfile profile)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
            throw new ArgumentException("sandbox root is required", nameof(sandboxRoot));
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        foreach (var step in profile.SandboxPreparationSteps)
        {
            ApplyStep(sandboxRoot, step);
        }

        AvailableCapabilities = profile.DeclaredCapabilities;
    }

    private static void ApplyStep(string sandboxRoot, SandboxPreparationStep step)
    {
        if (string.IsNullOrWhiteSpace(step.RelativePath))
            throw new ArgumentException("sandbox step path is required", nameof(step));

        var targetPath = Path.Combine(sandboxRoot, step.RelativePath);

        if (step.Kind == SandboxPreparationKind.CreateDirectory)
        {
            Directory.CreateDirectory(targetPath);
            return;
        }

        throw new InvalidOperationException($"Unknown sandbox preparation kind: {step.Kind}");
    }
}
