// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public interface IEnvironmentCapabilityProvider
{
    EnvironmentCapability AvailableCapabilities { get; }

    bool HasCapability(EnvironmentCapability capability);

    void Update(EnvironmentCapability capabilities);
}

public sealed class EnvironmentCapabilityProvider : IEnvironmentCapabilityProvider
{
    public EnvironmentCapability AvailableCapabilities { get; private set; } = EnvironmentCapability.None;

    public bool HasCapability(EnvironmentCapability capability) =>
        (AvailableCapabilities & capability) == capability;

    public void Update(EnvironmentCapability capabilities)
    {
        AvailableCapabilities = capabilities;
    }
}
