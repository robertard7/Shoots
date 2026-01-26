using System.Collections.ObjectModel;
using System.Linq;

namespace Shoots.UI.Startup;

public sealed record StartupStructureItem(string RelativePath, bool IsDirectory, string? Content = null);

public sealed record StartupLanguageDefinition(string Name, IReadOnlyList<StartupStructureItem> Structure);

public static class StartupLanguageRegistry
{
    private static readonly ReadOnlyCollection<StartupLanguageDefinition> Definitions =
        new(new[]
        {
            new StartupLanguageDefinition(
                "C#",
                new[]
                {
                    new StartupStructureItem("src", true),
                    new StartupStructureItem("src/Program.cs", false, "Console.WriteLine(\"Hello from Shoots.\");\n")
                }),
            new StartupLanguageDefinition(
                "Python",
                new[]
                {
                    new StartupStructureItem("src", true),
                    new StartupStructureItem("src/main.py", false, "print(\"Hello from Shoots.\")\n")
                }),
            new StartupLanguageDefinition(
                "JavaScript",
                new[]
                {
                    new StartupStructureItem("src", true),
                    new StartupStructureItem("src/index.js", false, "console.log(\"Hello from Shoots.\");\n")
                })
        });

    public static IReadOnlyList<StartupLanguageDefinition> All => Definitions;

    public static StartupLanguageDefinition? Find(string name) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
}
