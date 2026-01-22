using System;
using System.IO;
using Shoots.Runtime.Loader.Toolpacks;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ToolpackDiscoveryTests
{
    [Fact]
    public void MalformedToolpackManifestFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "toolpack.json");
        File.WriteAllText(path, "{\"toolpackId\":123}");

        try
        {
            var discovery = new ToolpackDiscovery();
            Assert.Throws<ArgumentException>(() => discovery.Discover(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
