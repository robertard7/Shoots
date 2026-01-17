using System.Text.Json.Serialization;

namespace Shoots.Contracts.Core;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic plan derived from a build request.
/// </summary>
/// <param name="PlanId">Derived hash identifier for the plan (deterministic).</param>
/// <param name="Request">Input request used to derive the plan.</param>
/// <param name="Authority">Immutable authority assigned at planning time.</param>
/// <param name="Steps">Ordered steps derived deterministically from the request.</param>
/// <param name="Artifacts">Ordered artifacts expected from executing the plan.</param>
public sealed record BuildPlan(
    string PlanId,
    BuildRequest Request,
    DelegationAuthority Authority,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<BuildArtifact> Artifacts
);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic step derived from a build request.
/// </summary>
/// <param name="Id">Stable step identifier.</param>
/// <param name="Description">Human-readable step description.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AiBuildStep), typeDiscriminator: "ai")]
[JsonDerivedType(typeof(ToolBuildStep), typeDiscriminator: "tool")]
public record BuildStep(
    string Id,
    string Description
);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic step representing an AI prompt request.
/// </summary>
/// <param name="Id">Stable step identifier.</param>
/// <param name="Description">Human-readable step description.</param>
/// <param name="Prompt">Deterministic prompt text.</param>
/// <param name="OutputSchema">Deterministic output schema string (opaque JSON).</param>
public sealed record AiBuildStep(
    string Id,
    string Description,
    string Prompt,
    string OutputSchema
) : BuildStep(Id, Description);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic step representing a tool selection and its bindings.
/// </summary>
/// <param name="Id">Stable step identifier.</param>
/// <param name="Description">Human-readable step description.</param>
/// <param name="ToolId">Selected tool identifier.</param>
/// <param name="InputBindings">Deterministic tool input bindings.</param>
/// <param name="Outputs">Declared tool outputs.</param>
public sealed record ToolBuildStep(
    string Id,
    string Description,
    ToolId ToolId,
    IReadOnlyDictionary<string, object?> InputBindings,
    IReadOnlyList<ToolOutputSpec> Outputs
) : BuildStep(Id, Description);
