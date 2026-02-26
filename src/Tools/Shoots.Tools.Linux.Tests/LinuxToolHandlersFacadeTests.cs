using Xunit;

namespace Shoots.Tools.Linux.Tests;

public sealed class LinuxToolHandlersFacadeTests
{
    [Fact]
    public void IsPathWithin_accepts_same_and_child_paths_and_rejects_escape()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shoots-root"));
        var child = Path.Combine(root, "child", "file.txt");
        var escaped = Path.GetFullPath(Path.Combine(root, "..", "outside.txt"));

        Assert.True(LinuxToolHandlers.IsPathWithin(root, root));
        Assert.True(LinuxToolHandlers.IsPathWithin(root, child));
        Assert.False(LinuxToolHandlers.IsPathWithin(root, escaped));
    }

    [Fact]
    public void IsPathWithin_does_not_match_prefix_sibling()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shoots"));
        var repo = Path.Combine(parent, "repo");
        var sibling = Path.Combine(parent, "repo2", "a.txt");

        Assert.False(LinuxToolHandlers.IsPathWithin(repo, sibling));
    }

    [Fact]
    public void TruncateUtf8_preserves_valid_boundaries()
    {
        var text = "a😀b";

        Assert.Equal("a", LinuxToolText.TruncateUtf8(text, 1));
        Assert.Equal("a", LinuxToolText.TruncateUtf8(text, 4));
        Assert.Equal("a😀", LinuxToolText.TruncateUtf8(text, 5));
        Assert.Equal(text, LinuxToolText.TruncateUtf8(text, 6));
    }
}
