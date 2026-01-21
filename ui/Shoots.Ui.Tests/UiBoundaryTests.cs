using System.Linq;
using Shoots.UI;
using Shoots.UI.Environment;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class UiBoundaryTests
{
    [Fact]
    public void UiDoesNotReferenceRuntimeCore()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Shoots.Runtime.Core");
    }

    [Fact]
    public void UiDoesNotReferenceRuntimeLoader()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Shoots.Runtime.Loader");
    }

    [Fact]
    public void EnvironmentNamespaceAvoidsRuntimeTypes()
    {
        var environmentTypes = typeof(EnvironmentProfileService).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Shoots.UI.Environment")
            .ToList();

        foreach (var type in environmentTypes)
        {
            var members = type.GetMembers();
            foreach (var member in members)
            {
                var memberType = member switch
                {
                    System.Reflection.PropertyInfo property => property.PropertyType,
                    System.Reflection.FieldInfo field => field.FieldType,
                    System.Reflection.MethodInfo method => method.ReturnType,
                    _ => null
                };

                if (memberType is null || memberType.Namespace is null)
                    continue;

                Assert.DoesNotContain("Shoots.Runtime", memberType.Namespace);
            }
        }
    }
}
