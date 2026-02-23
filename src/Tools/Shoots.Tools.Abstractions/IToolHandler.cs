using Shoots.Contracts.Core;

namespace Shoots.Tools.Abstractions;

public interface IToolHandler
{
    ToolId Id { get; }

    ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx);
}
