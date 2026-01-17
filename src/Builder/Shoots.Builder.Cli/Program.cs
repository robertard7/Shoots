using Shoots.Builder.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Shoots.Runtime.Loader;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: shoots <command>");
    return 1;
}

var input = string.Join(" ", args);

// Modules directory is always relative to execution root
var modulesDir = Path.Combine(
    Environment.CurrentDirectory,
    "modules"
);

// CLI owns runtime execution wiring: loader + engine construction live only here.
var loader = new DefaultRuntimeLoader();
IReadOnlyList<IRuntimeModule> modules =
    Directory.Exists(modulesDir)
        ? loader.LoadFromDirectory(modulesDir)
        : Array.Empty<IRuntimeModule>();

Console.WriteLine($"[builder] modulesDir = {modulesDir}");
Console.WriteLine($"[builder] loaded modules = {modules.Count}");

foreach (var module in modules)
{
    Console.WriteLine($"[builder] module: {module.ModuleId} v{module.ModuleVersion}");
    foreach (var cmd in module.Describe())
        Console.WriteLine($"[builder]   command: {cmd.CommandId}");
}

var narrator = new TextRuntimeNarrator(Console.WriteLine);
var helper = new DeterministicRuntimeHelper();
var engine = new RuntimeEngine(modules, narrator, helper);

var buildRequest = new BuildRequest(
    CommandId: input,
    Args: new Dictionary<string, object?>()
);
var planner = new DeterministicBuildPlanner(engine);
var kernel = new BuilderKernel(engine, engine, planner);

// Execute
var result = kernel.Run(buildRequest);

Console.WriteLine("[plan:text]");
Console.WriteLine(BuildPlanRenderer.RenderText(result.Plan));
Console.WriteLine("[plan:json]");
Console.WriteLine(BuildPlanRenderer.RenderJson(result.Plan));

// Emit authoritative result
Console.WriteLine($"state={result.State}");
Console.WriteLine($"hash={result.Hash}");
Console.WriteLine($"folder={result.Folder}");

if (!string.IsNullOrEmpty(result.Reason))
    Console.WriteLine($"reason={result.Reason}");

// Law #2: exit code reflects final state
return result.State switch
{
    RunState.Success => 0,
    RunState.Blocked => 2,
    RunState.Invalid => 1,
    _ => 1
};
