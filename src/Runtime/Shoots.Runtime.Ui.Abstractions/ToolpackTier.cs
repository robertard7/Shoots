namespace Shoots.Runtime.Ui.Abstractions;

public enum ToolpackTier
{
    Public,
    Developer,
    System
}

public interface IToolpackPolicySnapshot
{
    ToolpackTier AllowedTier { get; }
}
