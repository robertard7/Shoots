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
    }
}
