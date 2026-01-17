#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

public sealed class BuilderKernel
{
    private readonly IRuntimeHost _runtimeHost;
    private readonly IRuntimeServices _runtimeServices;

    public BuilderKernel(IRuntimeHost runtimeHost, IRuntimeServices runtimeServices)
    {
        _runtimeHost = runtimeHost ?? throw new ArgumentNullException(nameof(runtimeHost));
        _runtimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
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

    public BuildRunResult Run(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("input is required", nameof(input));

        // --- Command resolution ---
        var commandId = input.Trim();

        // --- Deterministic plan + hash ---
        var planText = $"COMMAND:\n{commandId}\n";
        var hash = Sha256Hex(planText);

        // --- Artifact root (method-scoped, authoritative) ---
        var artifactsRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            hash
        );

        Directory.CreateDirectory(artifactsRoot);

        File.WriteAllText(
            Path.Combine(artifactsRoot, "plan.txt"),
            planText,
            Encoding.UTF8
        );

        // --- Runtime context ---
        var context = new RuntimeContext(
            SessionId: hash,
            CorrelationId: Guid.NewGuid().ToString("n"),
            Env: new Dictionary<string, string>(),
            Services: _runtimeServices
        );

        var request = new RuntimeRequest(
            CommandId: commandId,
            Args: new Dictionary<string, object?>(),
            Context: context
        );

        // --- Execute ---
        var result = _runtimeHost.Execute(request);

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
			result.Error?.Code
		);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record BuildRunResult(
    string Hash,
    string Folder,
    RunState State,
    string? Reason = null
);
