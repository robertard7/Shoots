using System.Text.Json.Serialization;

namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic tool result envelope.
/// </summary>
public sealed record ToolResult(
    ToolId ToolId,
    [property: JsonConverter(typeof(DictionaryStringObjectJsonConverter))]
    IReadOnlyDictionary<string, object?> Outputs,
    bool Success
);
