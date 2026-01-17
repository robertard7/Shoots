using System;
using System.Collections.Generic;
using System.Threading;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class CoreModule : IRuntimeModule
{
    public string ModuleId => "core";
    public RuntimeVersion ModuleVersion => RuntimeVersion.Parse("0.1.0");
    public RuntimeVersion MinRuntimeVersion => RuntimeVersion.Parse("0.1.0");
    public RuntimeVersion MaxRuntimeVersion => RuntimeVersion.Parse("0.x");

    public IReadOnlyList<RuntimeCommandSpec> Describe()
    {
        return new[]
        {
            new RuntimeCommandSpec(
                "core.ping",
                "Health check. Returns pong=true and echoes msg if provided.",
                new[]
                {
                    new RuntimeArgSpec("msg", "string", false, "Optional message")
                }
            ),
            new RuntimeCommandSpec(
                "core.commands",
                "Lists all registered commands.",
                Array.Empty<RuntimeArgSpec>()
            ),
            new RuntimeCommandSpec(
                "core.command",
                "Describes a single command by id.",
                new[]
                {
                    new RuntimeArgSpec("id", "string", true, "Command id to describe")
                }
            ),
            new RuntimeCommandSpec(
                "core.help",
                "Shows basic usage and where to look next.",
                Array.Empty<RuntimeArgSpec>()
            )
        };
    }

    public RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default)
    {
        return request.CommandId switch
        {
            "core.ping" => Ping(request),
            "core.commands" => Commands(request),
            "core.command" => Command(request),
            "core.help" => Help(request),
            _ => RuntimeResult.Fail(RuntimeError.UnknownCommand(request.CommandId))
        };
    }

    private static RuntimeResult Ping(RuntimeRequest request)
    {
        request.Args.TryGetValue("msg", out var msg);
        return RuntimeResult.Success(new { pong = true, echo = msg?.ToString() ?? "" });
    }

    private static RuntimeResult Help(RuntimeRequest request)
    {
        return RuntimeResult.Success(new
        {
            usage = "command.id arg=value arg=\"value with spaces\"",
            next = new[] { "core.commands", "core.command id=<cmd>", "core.ping msg=hello" }
        });
    }

    private static RuntimeResult Commands(RuntimeRequest request)
    {
        var services = request.Context.Services;
        if (services is null)
            return RuntimeResult.Fail(RuntimeError.Internal("Runtime services unavailable"));

        var cmds = services.GetAllCommands();
        return RuntimeResult.Success(new { commands = cmds.Select(c => c.CommandId).ToArray() });
    }

    private static RuntimeResult Command(RuntimeRequest request)
    {
        var services = request.Context.Services;
        if (services is null)
            return RuntimeResult.Fail(RuntimeError.Internal("Runtime services unavailable"));

        if (!request.Args.TryGetValue("id", out var idObj) || string.IsNullOrWhiteSpace(idObj?.ToString()))
            return RuntimeResult.Fail(RuntimeError.InvalidArguments("Missing required arg: id"));

        var id = idObj!.ToString()!;
        var spec = services.GetCommand(id);
        if (spec is null)
            return RuntimeResult.Fail(RuntimeError.UnknownCommand(id));

        return RuntimeResult.Success(new
        {
            spec.CommandId,
            spec.Description,
            args = spec.Args.Select(a => new { a.Name, a.Type, a.Required, a.Description }).ToArray()
        });
    }
}
