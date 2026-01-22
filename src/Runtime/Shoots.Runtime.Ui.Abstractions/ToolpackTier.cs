namespace Shoots.Runtime.Ui.Abstractions;

public enum ToolpackTier
{
    Public,
    Developer,
    System
}

public enum ToolpackCapability
{
    FileSystem,
    Process,
    Network,
    Kernel,
    Build,
    Deploy
}

public interface IToolpackPolicySnapshot
{
    ToolpackTier AllowedTier { get; }

    IReadOnlyList<ToolpackCapability> AllowedCapabilities { get; }
}
