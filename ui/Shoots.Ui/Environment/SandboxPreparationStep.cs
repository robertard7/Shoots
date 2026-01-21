namespace Shoots.UI.Environment;

public sealed record SandboxPreparationStep(
    string RelativePath,
    SandboxPreparationKind Kind
);

public enum SandboxPreparationKind
{
    CreateDirectory
}
