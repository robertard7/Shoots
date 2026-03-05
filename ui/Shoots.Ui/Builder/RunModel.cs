using System;
using System.Collections.Generic;

namespace Shoots.UI.Builder;

public sealed record RunStep(
    string StepId,
    string ToolId,
    string Status,
    string? OutputPath,
    string? Error
);

public sealed record RunModel(
    string RunId,
    string ProjectId,
    string PlanId,
    string PlanHash,
    string ToolCatalogHash,
    DateTimeOffset CreatedUtc,
    string Status,
    IReadOnlyList<RunStep> Steps
);

public sealed record BuilderExecutionResult(
    RunModel Run,
    string RunPath,
    string RunJsonPath,
    string ArtifactJsonPath
);
