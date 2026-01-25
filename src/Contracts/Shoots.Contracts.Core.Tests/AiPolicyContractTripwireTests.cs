using System;
using System.Linq;
using System.Reflection;
using Shoots.Contracts.Core.AI;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class AiPolicyContractTripwireTests
{
    [Fact]
    public void AiVisibilityMode_is_frozen()
    {
        var names = Enum.GetNames(typeof(AiVisibilityMode));
        Assert.Equal(new[] { "Visible", "HiddenForEndUsers", "AdminOnly" }, names);
    }

    [Fact]
    public void AiAccessRole_is_frozen()
    {
        var names = Enum.GetNames(typeof(AiAccessRole));
        Assert.Equal(new[] { "EndUser", "Developer", "Admin" }, names);
    }

    [Fact]
    public void AiPresentationPolicy_fields_are_frozen()
    {
        var properties = typeof(AiPresentationPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AllowAiPanelToggle",
                "AllowCopyExport",
                "EnterpriseMode",
                "Visibility"
            },
            properties);

        var constructor = typeof(AiPresentationPolicy)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var parameters = constructor
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Visibility",
                "AllowAiPanelToggle",
                "AllowCopyExport",
                "EnterpriseMode"
            },
            parameters);
    }

    [Fact]
    public void AiPresentationPolicy_version_is_frozen()
    {
        Assert.Equal(1, AiPresentationPolicy.ContractVersion);
        Assert.Equal(
            "Visibility|AllowAiPanelToggle|AllowCopyExport|EnterpriseMode",
            AiPresentationPolicy.ContractShape);
    }
}
