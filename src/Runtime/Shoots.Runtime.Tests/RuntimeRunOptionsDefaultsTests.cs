using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Tests;

public sealed class RuntimeRunOptionsDefaultsTests
{
    [Fact]
    public void Defaults_are_deterministic()
    {
        var options = new RuntimeRunOptions();

        Assert.Equal(ResumeMode.None, options.ResumeMode);
        Assert.Equal(DecisionWaitMode.Halt, options.DecisionWaitMode);
        Assert.Equal(1, options.MaxDecisionWaits);
        Assert.False(options.DiscardWaiting);
        Assert.False(options.AllowPlanChangeOverride);
        Assert.Null(options.InjectedDecisionDigest);
    }
}
