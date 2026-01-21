// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public interface IEnvironmentProfileService
{
    IReadOnlyList<IEnvironmentProfile> Profiles { get; }

    EnvironmentProfileResult? LastResult { get; }

    EnvironmentCapability AvailableCapabilities { get; }

    EnvironmentProfileResult ApplyProfile(string sandboxRoot, IEnvironmentProfile profile);
}
