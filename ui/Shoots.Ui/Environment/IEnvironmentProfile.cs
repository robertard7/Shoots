// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public interface IEnvironmentProfile
{
    string Name { get; }
    string Description { get; }
    EnvironmentCapability DeclaredCapabilities { get; }
    IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps { get; }
}
