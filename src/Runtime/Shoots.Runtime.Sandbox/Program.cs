using System.Text.Json;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Shoots.Runtime.Language;

// -----------------------------
// Explicit runtime services
// -----------------------------
var narrator = new NullRuntimeNarrator();
var helper   = new NullRuntimeHelper();

// -----------------------------
// Load modules (sandbox only loads core)
// -----------------------------
var modules = new IRuntimeModule[]
{
    new CoreModule()
};

// -----------------------------
// Runtime engine (no defaults allowed)
// -----------------------------
var engine = new RuntimeEngine(
    modules,
    narrator,
    helper
);

// -----------------------------
// Execution context (host-owned)
// -----------------------------
var context = new RuntimeContext(
    SessionId: Guid.NewGuid().ToString("N"),
    CorrelationId: Guid.NewGuid().ToString("N"),
    Env: new Dictionary<string, string>(),
    Services: engine
);

// -----------------------------
// Console UI
// -----------------------------
var json = new JsonSerializerOptions
{
    WriteIndented = false
};

Console.WriteLine("Shoots Runtime Sandbox");
Console.WriteLine("Commands:");
Console.WriteLine("  core.help");
Console.WriteLine("  core.commands");
Console.WriteLine("  core.command id=<cmd>");
Console.WriteLine("  core.ping [msg=...]");
Console.WriteLine();
Console.WriteLine("Empty line exits.\n");

// -----------------------------
// REPL loop
// -----------------------------
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(line))
        break;

    try
    {
        var request = ShootsParser.Parse(line, context);
        var result  = engine.Execute(request);

        if (!result.Ok)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok      = false,
                error   = result.Error?.Code,
                message = result.Error?.Message,
                details = result.Error?.Details
            }, json));
            continue;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok     = true,
            output = result.Output
        }, json));
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok      = false,
            error   = "sandbox_error",
            message = ex.Message
        }, json));
    }
}
