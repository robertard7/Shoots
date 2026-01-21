// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public sealed record EnvironmentScript(
    string Name,
    string Description,
    EnvironmentCapability DeclaredCapabilities,
    IReadOnlyList<SandboxPreparationStep> SandboxSteps
);
