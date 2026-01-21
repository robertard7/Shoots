// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public interface IEnvironmentProfilePrompt
{
    bool ConfirmCapabilityChange(EnvironmentCapability previous, EnvironmentCapability next);
}

public sealed class NoOpEnvironmentProfilePrompt : IEnvironmentProfilePrompt
{
    public bool ConfirmCapabilityChange(EnvironmentCapability previous, EnvironmentCapability next)
    {
        _ = previous;
        _ = next;
        return true;
    }
}
