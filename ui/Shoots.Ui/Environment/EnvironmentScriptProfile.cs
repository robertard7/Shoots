// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public sealed class EnvironmentScriptProfile : IEnvironmentProfile
{
    private readonly EnvironmentScript _script;

    public EnvironmentScriptProfile(EnvironmentScript script)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
    }

    public string Name => _script.Name;

    public string Description => _script.Description;

    public EnvironmentCapability DeclaredCapabilities => _script.DeclaredCapabilities;

    public IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps => _script.SandboxSteps;
}
