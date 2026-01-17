using Shoots.Runtime.Abstractions;

namespace Shoots.Modules.Core;

public sealed class CoreModule : IRuntimeModule
{
    public string ModuleId => "core";

    public RuntimeVersion ModuleVersion => new(1, 0, 0);
    public RuntimeVersion MinRuntimeVersion => new(1, 0, 0);
    public RuntimeVersion MaxRuntimeVersion => new(1, 9, 0);

    public IReadOnlyList<RuntimeCommandSpec> Describe()
    {
        return new[]
        {
            new RuntimeCommandSpec(
                "core.ping",
                "Ping command",
                Array.Empty<RuntimeArgSpec>()
            )
        };
    }

    public RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default)
    {
        if (request.CommandId != "core.ping")
        {
            return RuntimeResult.Fail(
                new RuntimeError("unknown_command", request.CommandId)
            );
        }

        return RuntimeResult.Success("pong");
    }
}
