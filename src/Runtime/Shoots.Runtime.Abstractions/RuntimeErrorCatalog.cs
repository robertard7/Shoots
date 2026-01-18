using System;
using System.Collections.Generic;

namespace Shoots.Runtime.Abstractions;

public static class RuntimeErrorCatalog
{
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ai_prompt_missing",
        "ai_schema_missing",
        "incompatible_major",
        "incompatible_minor",
        "internal_error",
        "invalid_arguments",
        "missing_authority",
        "route_decision_unexpected",
        "route_intent_mismatch",
        "route_owner_invalid",
        "route_rule_missing",
        "route_rule_mismatch",
        "route_rules_missing",
        "route_start_terminate",
        "route_state_final",
        "route_step_invalid",
        "route_step_out_of_range",
        "route_terminal_missing",
        "route_terminate_not_terminal",
        "route_tool_invocation_conflict",
        "route_tool_invocation_invalid",
        "route_tool_invocation_mismatch",
        "route_workorder_missing",
        "route_workorder_mismatch",
        "route_workorder_step_mismatch",
        "tool_authority_denied",
        "tool_authority_missing",
        "tool_binding_missing",
        "tool_binding_unknown",
        "tool_bindings_missing",
        "tool_missing",
        "unknown_command"
    };

    public static bool IsKnown(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        return Codes.Contains(code);
    }
}
