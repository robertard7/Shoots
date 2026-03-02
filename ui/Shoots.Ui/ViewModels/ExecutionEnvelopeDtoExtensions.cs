#nullable enable

using System;
using System.Reflection;

namespace Shoots.UI.ViewModels;

internal static class ExecutionEnvelopeDtoExtensions
{
    /// <summary>
    /// UI-safe helper: derives a stable "execution id" from an envelope-like object.
    /// Avoids hard references to runtime envelope types.
    /// </summary>
    public static string GetExecutionId(object? envelope)
    {
        if (envelope is null)
            return string.Empty;

        // Prefer PlanId, then WorkOrderId, then anything that looks like an Id.
        return GetStringProperty(envelope, "PlanId")
            ?? GetStringProperty(envelope, "WorkOrderId")
            ?? GetStringProperty(envelope, "ExecutionId")
            ?? GetStringProperty(envelope, "Id")
            ?? string.Empty;
    }

    private static string? GetStringProperty(object obj, string propertyName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

        var prop = obj.GetType().GetProperty(propertyName, flags);
        if (prop is null)
            return null;

        var value = prop.GetValue(obj);

        // Direct string
        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return s;

        // Common Value-object pattern: { Value = "..." }
        if (value is not null)
        {
            var valueProp = value.GetType().GetProperty("Value", flags);
            if (valueProp?.GetValue(value) is string v && !string.IsNullOrWhiteSpace(v))
                return v;
        }

        return null;
    }
}