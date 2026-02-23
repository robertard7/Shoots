using Shoots.Contracts.Core;
using Shoots.Tools.Abstractions;

namespace Shoots.Tools.Linux.Tests;

public sealed class LinuxToolsTests
{
    [Fact]
    public void Catalog_load_is_stable_sorted()
    {
        var entries = LinuxToolCatalog.LoadEntries(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json"));
        var ids = entries.Select(e => e.Spec.ToolId.Value).ToArray();
        var sorted = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, ids);
    }

    [Fact]
    public void Write_and_read_text_respect_truncation()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None, 4);
            var workOrderId = new WorkOrderId("wo-1");

            var write = new LinuxFsWriteTextHandler().Execute(
                new ToolInvocation(new ToolId("linux.fs.write_text.v1"), new Dictionary<string, object?>
                {
                    ["path"] = "a.txt",
                    ["text"] = "abcdef"
                }, workOrderId),
                ctx);

            Assert.True(write.Success);

            var read = new LinuxFsReadTextHandler().Execute(
                new ToolInvocation(new ToolId("linux.fs.read_text.v1"), new Dictionary<string, object?>
                {
                    ["path"] = "a.txt"
                }, workOrderId),
                ctx);

            Assert.True(read.Success);
            Assert.Equal(true, read.Outputs["truncated"]);
            Assert.Equal("abcd", read.Outputs["text"]);
            Assert.Equal(6, read.Outputs["bytes_read"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Proc_exec_times_out()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var handler = new LinuxProcExecHandler();
            var result = handler.Execute(new ToolInvocation(handler.Id, new Dictionary<string, object?>
            {
                ["file"] = "sleep",
                ["args"] = new object?[] { "2" },
                ["timeout_ms"] = 10
            }, new WorkOrderId("wo-1")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(result.Success);
            Assert.Equal(true, result.Outputs["timed_out"]);
            Assert.Equal(-1, result.Outputs["exit_code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Git_tools_work_against_local_repo()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            Run("git", $"init {source}", root);
            File.WriteAllText(Path.Combine(source, "readme.md"), "x");
            Run("git", $"-C {source} add .", root);
            Run("git", $"-C {source} -c user.email=a@b -c user.name=n commit -m init", root);

            var clone = new LinuxGitCloneHandler().Execute(new ToolInvocation(new ToolId("linux.git.clone.v1"), new Dictionary<string, object?>
            {
                ["url"] = source,
                ["dest_path"] = "dest",
                ["depth"] = 1
            }, new WorkOrderId("wo-1")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(clone.Success);
            Assert.Equal(true, clone.Outputs["cloned"]);

            File.WriteAllText(Path.Combine(root, "dest", "dirty.txt"), "dirty");
            var status = new LinuxGitStatusHandler().Execute(new ToolInvocation(new ToolId("linux.git.status.v1"), new Dictionary<string, object?>
            {
                ["repo_path"] = "dest"
            }, new WorkOrderId("wo-1")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(status.Success);
            Assert.Equal(false, status.Outputs["clean"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Run(string file, string args, string cwd)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }
}
