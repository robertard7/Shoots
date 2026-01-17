using Shoots.Builder.Core;

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

var kernel = new BuilderKernel(modulesDir);

// Execute
var result = kernel.Run(input);

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
