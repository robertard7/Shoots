using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class RuntimeEngine :
    IRuntimeServices,
    IRuntimeAssist,
    IRuntimeHost
{
    private readonly Dictionary<string, (IRuntimeModule Module, RuntimeCommandSpec Spec)> _index;

    private readonly IRuntimeNarrator _narrator;
    private readonly IRuntimeHelper _helper;

    public IRuntimeNarrator Narrator => _narrator;
    public IRuntimeHelper Helper => _helper;
    public RuntimeVersion Version { get; } = RuntimeVersion.Parse("0.1.0");

    public RuntimeEngine(
        IEnumerable<IRuntimeModule> modules,
        IRuntimeNarrator narrator,
        IRuntimeHelper helper)
    {
        if (modules is null) throw new ArgumentNullException(nameof(modules));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));

        _index = new Dictionary<string, (IRuntimeModule, RuntimeCommandSpec)>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in modules)
        {
            foreach (var spec in m.Describe())
            {
                if (_index.ContainsKey(spec.CommandId))
                    throw new InvalidOperationException($"Command '{spec.CommandId}' already registered");

                _index[spec.CommandId] = (m, spec);
            }
        }
    }

    public RuntimeResult Execute(RuntimeRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return RuntimeResult.Fail(RuntimeError.Internal("Null request"));

        var authorityError = ValidateAuthority(request.Context);
        if (authorityError is not null)
        {
            _narrator.OnError(authorityError);
            return RuntimeResult.Fail(authorityError);
        }

        if (!_index.TryGetValue(request.CommandId, out var hit))
        {
            var err = RuntimeError.UnknownCommand(request.CommandId);
            _narrator.OnError(err);
            return RuntimeResult.Fail(err);
        }
		_narrator.OnPlan(
			$"Resolved command '{hit.Spec.CommandId}' " +
			$"via module '{hit.Module.ModuleId}' " +
			$"(session={request.Context.SessionId}, correlation={request.Context.CorrelationId})"
		);

        _narrator.OnCommand(hit.Spec, request);

        try
        {
            var result = hit.Module.Execute(request, ct);

            if (!result.Ok && result.Error is not null)
                _narrator.OnError(result.Error);

            _narrator.OnResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var err = RuntimeError.Internal("Execution cancelled");
            _narrator.OnError(err);
            return RuntimeResult.Fail(err);
        }
        catch (Exception ex)
        {
            var err = RuntimeError.Internal("Unhandled exception", ex);
            _narrator.OnError(err);
            return RuntimeResult.Fail(err);
        }
    }

    public IReadOnlyList<RuntimeCommandSpec> GetAllCommands() =>
        _index.Values
              .Select(v => v.Spec)
              .OrderBy(s => s.CommandId, StringComparer.OrdinalIgnoreCase)
              .ToArray();

    public RuntimeCommandSpec? GetCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return null;

        return _index.TryGetValue(commandId, out var hit)
            ? hit.Spec
            : null;
    }

    private static RuntimeError? ValidateAuthority(RuntimeContext context)
    {
        if (context.Env is null)
            return null;

        if (!context.Env.TryGetValue("authority.kind", out var kindValue))
            return null;

        if (!Enum.TryParse<ProviderKind>(kindValue, true, out var kind))
        {
            return RuntimeError.InvalidArguments(
                "Invalid authority kind",
                kindValue);
        }

        if (!context.Env.TryGetValue("authority.allows_delegation", out var allowsValue) ||
            !bool.TryParse(allowsValue, out var allowsDelegation))
        {
            return RuntimeError.InvalidArguments(
                "Invalid authority delegation flag",
                allowsValue);
        }

        if (kind == ProviderKind.Delegated && !allowsDelegation)
        {
            return RuntimeError.InvalidArguments(
                "Delegation authority rejected",
                new
                {
                    kind = kind.ToString(),
                    allowsDelegation
                });
        }

        return null;
    }
}
