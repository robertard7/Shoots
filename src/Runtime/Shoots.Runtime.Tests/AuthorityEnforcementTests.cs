using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AuthorityEnforcementTests
{
    [Fact]
    public void Runtime_rejects_delegated_authority_without_permission()
    {
        var narrator = new NullRuntimeNarrator();
        var helper = new NullRuntimeHelper();
        var modules = new[] { new CoreModule() };
        var engine = new RuntimeEngine(modules, narrator, helper);

        var env = new Dictionary<string, string>
        {
            ["authority.kind"] = ProviderKind.Delegated.ToString(),
            ["authority.allows_delegation"] = "false"
        };

        var context = new RuntimeContext(
            SessionId: "session",
            CorrelationId: "correlation",
            Env: env,
            Services: engine);

        var request = new RuntimeRequest(
            CommandId: "core.ping",
            Args: new Dictionary<string, object?>(),
            Context: context);

        var result = engine.Execute(request);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal("invalid_arguments", result.Error!.Code);
    }
}
