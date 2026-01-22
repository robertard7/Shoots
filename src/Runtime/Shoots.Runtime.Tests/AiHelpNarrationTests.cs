using Shoots.Runtime.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AiHelpNarrationTests
{
    // Architecture guard: narration summaries are deterministic and do not alter execution flow.
    [Fact]
    public void DeterministicNarratorSummaryIsStable()
    {
        var summary = DeterministicRuntimeNarratorSummary.Instance;
        var version = new RuntimeVersion(1, 2, 3);

        var first = summary.DescribeRuntime(version);
        var second = summary.DescribeRuntime(version);

        Assert.Equal(first, second);
    }
}
