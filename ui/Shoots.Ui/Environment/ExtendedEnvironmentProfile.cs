namespace Shoots.UI.Environment;

public sealed class ExtendedEnvironmentProfile : IEnvironmentProfile
{
    private static readonly IReadOnlyList<SandboxPreparationStep> Steps =
    [
        new SandboxPreparationStep("artifacts", SandboxPreparationKind.CreateDirectory),
        new SandboxPreparationStep("traces", SandboxPreparationKind.CreateDirectory),
        new SandboxPreparationStep("temp", SandboxPreparationKind.CreateDirectory),
        new SandboxPreparationStep("exports", SandboxPreparationKind.CreateDirectory)
    ];

    public string Name => "Extended";

    public string Description => "Adds optional capabilities alongside the standard sandbox folders.";

    public EnvironmentCapability DeclaredCapabilities =>
        EnvironmentCapability.SourceControl | EnvironmentCapability.Database | EnvironmentCapability.ArtifactExport;

    public IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps => Steps;
}
