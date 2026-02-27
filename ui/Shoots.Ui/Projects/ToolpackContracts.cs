namespace Shoots.UI.Projects;

public enum UiToolpackTier
{
    Public,
    Developer,
    System
}

public enum UiToolpackCapability
{
    FileSystem,
    Process,
    Network,
    Kernel,
    Build,
    Deploy
}

public interface IUiToolpackPolicySnapshot
{
    UiToolpackTier AllowedTier { get; }

    IReadOnlyList<UiToolpackCapability> AllowedCapabilities { get; }
}
