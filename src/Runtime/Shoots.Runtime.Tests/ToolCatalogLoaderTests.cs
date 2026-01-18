using System.Linq;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ToolCatalogLoaderTests
{
    [Fact]
    public void Loader_builds_snapshot_with_hash()
    {
        var json = """
        {
          "tools": [
            {
              "toolId": "tools.sample",
              "description": "Sample tool.",
              "authority": {
                "providerKind": "Local",
                "capabilities": ["Plan"]
              },
              "inputs": [
                { "name": "input", "type": "string", "description": "Input", "required": true }
              ],
              "outputs": [
                { "name": "output", "type": "string", "description": "Output" }
              ]
            }
          ]
        }
        """;

        var loader = new ToolCatalogLoader();
        var snapshot = loader.Load(json);

        Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
        Assert.Single(snapshot.Entries);
        Assert.Equal("tools.sample", snapshot.Entries.First().Spec.ToolId.Value);
    }
}
