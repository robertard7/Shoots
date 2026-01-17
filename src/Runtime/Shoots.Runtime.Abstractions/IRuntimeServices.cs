namespace Shoots.Runtime.Abstractions;

public interface IRuntimeServices
{
    /// <summary>Returns all known command specs (aggregated across modules).</summary>
    IReadOnlyList<RuntimeCommandSpec> GetAllCommands();

    /// <summary>Returns one command spec or null if not found.</summary>
    RuntimeCommandSpec? GetCommand(string commandId);
}
