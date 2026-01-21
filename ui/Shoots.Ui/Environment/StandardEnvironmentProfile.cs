// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public sealed class StandardEnvironmentProfile : IEnvironmentProfile
{
    private static readonly IReadOnlyList<SandboxPreparationStep> Steps =
    [
        new SandboxPreparationStep("artifacts", SandboxPreparationKind.CreateDirectory),
        new SandboxPreparationStep("traces", SandboxPreparationKind.CreateDirectory),
        new SandboxPreparationStep("temp", SandboxPreparationKind.CreateDirectory)
    ];

    public string Name => "Standard";

    public string Description => "Creates the default Shoots sandbox folders.";

    public EnvironmentCapability DeclaredCapabilities => EnvironmentCapability.None;

    public IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps => Steps;
}
