using System.Text.Json.Serialization;

namespace Shoots.Runtime.Abstractions;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BuildPlan))]
internal partial class BuildPlanJsonContext : JsonSerializerContext
{
}
