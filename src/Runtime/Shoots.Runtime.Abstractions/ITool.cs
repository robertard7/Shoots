using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Tool contract boundary (no execution logic).
/// </summary>
public interface ITool
{
    ToolSpec Spec { get; }
}
