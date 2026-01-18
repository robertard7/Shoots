using System.Text.Json.Serialization;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BuildPlan))]
[JsonSerializable(typeof(AiBuildStep))]
[JsonSerializable(typeof(ToolBuildStep))]
[JsonSerializable(typeof(RouteStep))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(ToolResult))]
internal partial class BuildPlanJsonContext : JsonSerializerContext
{
}
