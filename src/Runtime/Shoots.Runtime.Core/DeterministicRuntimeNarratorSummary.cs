using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class DeterministicRuntimeNarratorSummary : IRuntimeNarratorSummary
{
    public static readonly DeterministicRuntimeNarratorSummary Instance = new();

    public string DescribeRuntime(RuntimeVersion version)
    {
        return $"Explanatory runtime version: {version.Major}.{version.Minor}.{version.Patch}.";
    }
}
