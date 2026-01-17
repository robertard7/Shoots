using System.Text;
using System.Text.Json;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Loader;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: shoots-runtime --plan <path>");
    return 1;
}

string? planPath = null;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (string.Equals(arg, "--plan", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("missing value for --plan");
            return 1;
        }

        planPath = args[++i];
        continue;
    }

    Console.Error.WriteLine($"unknown argument: {arg}");
    return 1;
}

if (string.IsNullOrWhiteSpace(planPath))
{
    Console.Error.WriteLine("--plan is required");
    return 1;
}

if (!File.Exists(planPath))
{
    Console.Error.WriteLine($"plan file not found: {planPath}");
    return 1;
}

var json = File.ReadAllText(planPath, Encoding.UTF8);
var plan = JsonSerializer.Deserialize<BuildPlan>(json);

if (plan is null)
{
    Console.Error.WriteLine("invalid plan payload");
    return 1;
}

if (!AiStepValidator.TryValidate(plan, out var aiError))
{
    Console.Error.WriteLine(aiError?.ToString() ?? "invalid ai step");
    return 1;
}

var authorityError = ValidateAuthority(plan.Authority);
if (authorityError is not null)
{
    Console.Error.WriteLine(authorityError);
    return 1;
}

string computedHash;
try
{
    computedHash = BuildPlanHasher.ComputePlanId(plan.Request, plan.Authority, plan.Steps, plan.Artifacts);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (!string.Equals(plan.PlanId, computedHash, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("plan hash mismatch");
    return 1;
}

Console.WriteLine("plan_validated");
return 0;

static string? ValidateAuthority(DelegationAuthority authority)
{
    if (authority is null)
        return "missing authority";

    if (string.IsNullOrWhiteSpace(authority.ProviderId.Value))
        return "authority provider id is required";

    if (string.IsNullOrWhiteSpace(authority.PolicyId))
        return "delegation policy id is required";

    if (authority.Kind == ProviderKind.Delegated && !authority.AllowsDelegation)
        return "delegation authority rejected";

    return null;
}
