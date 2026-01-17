namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic AI request contract (opaque prompt input).
/// </summary>
/// <param name="Prompt">Deterministic prompt text.</param>
/// <param name="OutputSchema">Deterministic output schema string (opaque JSON).</param>
public sealed record AiRequest(
    string Prompt,
    string OutputSchema
);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic AI response contract (opaque output blob).
/// </summary>
/// <param name="Payload">Opaque response payload.</param>
public sealed record AiResponse(
    string Payload
);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// AI provider boundary (contracts only).
/// </summary>
public interface IAiProvider
{
    AiResponse Invoke(AiRequest request);
}
