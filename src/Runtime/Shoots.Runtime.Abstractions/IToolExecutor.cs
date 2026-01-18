using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Tool executor boundary (no execution logic).
/// </summary>
public interface IToolExecutor
{
    ToolResult Execute(ToolInvocation invocation);
}
