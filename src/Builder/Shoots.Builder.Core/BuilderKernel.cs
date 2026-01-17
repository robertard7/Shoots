#nullable enable
using System.Text;
using System.Text.Json;
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

public sealed class BuilderKernel
{
    private readonly IRuntimeHost _runtimeHost;
    private readonly IRuntimeServices _runtimeServices;
    private readonly IBuildPlanner _planner;

    public BuilderKernel(IRuntimeHost runtimeHost, IRuntimeServices runtimeServices, IBuildPlanner planner)
    {
        _runtimeHost = runtimeHost ?? throw new ArgumentNullException(nameof(runtimeHost));
        _runtimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    }

	private static RunState Classify(RuntimeResult result)
	{
		if (result.Ok)
			return RunState.Success;

		if (result.Error is null)
			return RunState.Invalid;

		return result.Error.Code switch
		{
			"missing_file" => RunState.Blocked,
			"permission_denied" => RunState.Blocked,
			"tool_not_found" => RunState.Blocked,
			_ => RunState.Invalid
		};
	}

	private static object BuildResolution(
		RunState state,
		RuntimeResult result
	)
	{
		if (result.Error is null)
		{
			return new
			{
				state = state.ToString().ToLowerInvariant(),
				error = new
				{
					code = "unknown",
					message = "Unknown failure",
					details = (object?)null
				},
				required = Array.Empty<object>()
			};
		}

		return new
		{
			state = state.ToString().ToLowerInvariant(),
			error = new
			{
				code = result.Error.Code,
				message = result.Error.Message,
				details = result.Error.Details
			},
			required = RequiredActions(result.Error)
		};
	}

	private static object[] RequiredActions(RuntimeError error)
	{
		return error.Code switch
		{
			"missing_file" => new[]
			{
				new
				{
					actor = "user",
					action = "provide_file",
					target = error.Details?.ToString() ?? "unknown"
				}
			},

			"permission_denied" => new[]
			{
				new
				{
					actor = "system",
					action = "grant_permission",
					target = error.Details?.ToString() ?? "unknown"
				}
			},

			"tool_not_found" => new[]
			{
				new
				{
					actor = "system",
					action = "install_tool",
					target = error.Details?.ToString() ?? "unknown"
				}
			},

			_ => new[]
			{
				new
				{
					actor = "user",
					action = "fix_input",
					target = "command_arguments"
				}
			}
		};
	}

    public BuildRunResult Run(BuildRequest request, string artifactsRoot)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.CommandId))
            throw new ArgumentException("command id is required", nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));
        if (string.IsNullOrWhiteSpace(artifactsRoot))
            throw new ArgumentException("artifacts root is required", nameof(artifactsRoot));

        // --- Deterministic plan + hash ---
        var plan = _planner.Plan(request);
        var hash = plan.PlanId;
        var planText = BuildPlanRenderer.RenderText(plan);
        var planJson = BuildPlanRenderer.RenderJson(plan);

        // --- Artifact root (method-scoped, authoritative) ---
        Directory.CreateDirectory(artifactsRoot);

        File.WriteAllText(
            Path.Combine(artifactsRoot, "plan.txt"),
            planText,
            Encoding.UTF8
        );
        File.WriteAllText(
            Path.Combine(artifactsRoot, "plan.json"),
            planJson,
            Encoding.UTF8
        );

        // --- Runtime context ---
        var env = new Dictionary<string, string>
        {
            ["authority.provider"] = plan.Authority.ProviderId.Value,
            ["authority.kind"] = plan.Authority.Kind.ToString(),
            ["authority.policy"] = plan.Authority.PolicyId,
            ["authority.allows_delegation"] = plan.Authority.AllowsDelegation.ToString()
        };

        var context = new RuntimeContext(
            SessionId: hash,
            CorrelationId: Guid.NewGuid().ToString("n"),
            Env: env,
            Services: _runtimeServices
        );

        var runtimeRequest = new RuntimeRequest(
            CommandId: plan.Request.CommandId,
            Args: new Dictionary<string, object?>(plan.Request.Args),
            Context: context
        );

        // --- Execute ---
        var result = _runtimeHost.Execute(runtimeRequest);

        // --- Persist result ---
        var resultPayload = new
        {
            ok = result.Ok,
            error = result.Error?.ToString(),
            output = result.Output
        };

        File.WriteAllText(
            Path.Combine(artifactsRoot, "result.json"),
            JsonSerializer.Serialize(
                resultPayload,
                new JsonSerializerOptions { WriteIndented = true }
            ),
            Encoding.UTF8
        );

        // --- Law #2: final run state ---
		var state = Classify(result);

		// Always write resolution on non-success
		if (state != RunState.Success)
		{
			var resolution = BuildResolution(state, result);

			File.WriteAllText(
				Path.Combine(artifactsRoot, "resolution.json"),
				JsonSerializer.Serialize(
					resolution,
					new JsonSerializerOptions { WriteIndented = true }
				),
				Encoding.UTF8
			);
		}

        return new BuildRunResult(
            hash,
            artifactsRoot,
            state,
            plan,
            result.Error?.Code
        );
    }

}

public sealed record BuildRunResult(
    string Hash,
    string Folder,
    RunState State,
    BuildPlan Plan,
    string? Reason = null
);
