using System.IO.Compression;
using System.Text;
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
    public void Fs_guard_blocks_root_escape()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var result = new LinuxFsWriteTextHandler().Execute(
                new ToolInvocation(new ToolId("linux.fs.write_text.v1"), new Dictionary<string, object?>
                {
                    ["path"] = "../escape.txt",
                    ["text"] = "x"
                }, new WorkOrderId("wo")),
                ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.False(result.Success);
            Assert.Equal("fs.write_failed", result.Outputs["error.code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Fs_ls_is_stable_sorted_and_bounded()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            File.WriteAllText(Path.Combine(root, "a", "z.txt"), "x");
            File.WriteAllText(Path.Combine(root, "a", "b.txt"), "x");
            File.WriteAllText(Path.Combine(root, "a", ".hidden"), "x");

            var result = new LinuxFsLsHandler().Execute(new ToolInvocation(new ToolId("linux.fs.ls.v1"), new Dictionary<string, object?>
            {
                ["path"] = "a",
                ["recursive"] = true,
                ["max_entries"] = 2
            }, new WorkOrderId("wo")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(result.Success);
            Assert.Equal(2, result.Outputs["count"]);
            Assert.Equal("a/b.txt\na/z.txt", result.Outputs["entries"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Fs_stat_has_stable_missing_shape()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var result = new LinuxFsStatHandler().Execute(new ToolInvocation(new ToolId("linux.fs.stat.v1"), new Dictionary<string, object?>
            {
                ["path"] = "none.txt"
            }, new WorkOrderId("wo")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(result.Success);
            Assert.Equal(false, result.Outputs["exists"]);
            Assert.Equal("fs.not_found", result.Outputs["error.code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Fs_move_copy_append_and_rm_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "in.txt"), "a");
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            Assert.True(new LinuxFsCopyHandler().Execute(new ToolInvocation(new ToolId("linux.fs.copy.v1"), new Dictionary<string, object?>
            {
                ["from"] = "in.txt",
                ["to"] = "copy.txt",
                ["overwrite"] = true
            }, wo), ctx).Success);

            Assert.True(new LinuxFsMoveHandler().Execute(new ToolInvocation(new ToolId("linux.fs.move.v1"), new Dictionary<string, object?>
            {
                ["from"] = "copy.txt",
                ["to"] = "moved.txt"
            }, wo), ctx).Success);

            var append = new LinuxFsAppendTextHandler().Execute(new ToolInvocation(new ToolId("linux.fs.append_text.v1"), new Dictionary<string, object?>
            {
                ["path"] = "moved.txt",
                ["text"] = "b",
                ["newline"] = false
            }, wo), ctx);

            Assert.True(append.Success);
            Assert.Equal(1, append.Outputs["bytes_appended"]);
            Assert.Equal("ab", File.ReadAllText(Path.Combine(root, "moved.txt")));

            var rm = new LinuxFsRmHandler().Execute(new ToolInvocation(new ToolId("linux.fs.rm.v1"), new Dictionary<string, object?>
            {
                ["path"] = "moved.txt"
            }, wo), ctx);

            Assert.True(rm.Success);
            Assert.Equal(true, rm.Outputs["removed"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Replace_respects_count_and_idempotency()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "x x x", Encoding.UTF8);
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var first = new LinuxTextReplaceHandler().Execute(new ToolInvocation(new ToolId("linux.text.replace.v1"), new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["search"] = "x",
                ["replace"] = "y",
                ["count"] = 2
            }, wo), ctx);

            Assert.True(first.Success);
            Assert.Equal(2, first.Outputs["replacements"]);
            Assert.Equal(true, first.Outputs["written"]);

            var second = new LinuxTextReplaceHandler().Execute(new ToolInvocation(new ToolId("linux.text.replace.v1"), new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["search"] = "q",
                ["replace"] = "z"
            }, wo), ctx);

            Assert.True(second.Success);
            Assert.Equal(0, second.Outputs["replacements"]);
            Assert.Equal(false, second.Outputs["written"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Archive_zip_and_unzip_prevent_zip_slip()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "ok");
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var zipResult = new LinuxArchiveZipHandler().Execute(new ToolInvocation(new ToolId("linux.archive.zip.v1"), new Dictionary<string, object?>
            {
                ["source_dir"] = "src",
                ["zip_path"] = "a.zip"
            }, wo), ctx);

            Assert.True(zipResult.Success);
            Assert.Equal(1, zipResult.Outputs["files_count"]);

            var unzipResult = new LinuxArchiveUnzipHandler().Execute(new ToolInvocation(new ToolId("linux.archive.unzip.v1"), new Dictionary<string, object?>
            {
                ["zip_path"] = "a.zip",
                ["dest_dir"] = "out",
                ["overwrite"] = true
            }, wo), ctx);
            Assert.True(unzipResult.Success);

            var badZip = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }

            var bad = new LinuxArchiveUnzipHandler().Execute(new ToolInvocation(new ToolId("linux.archive.unzip.v1"), new Dictionary<string, object?>
            {
                ["zip_path"] = "bad.zip",
                ["dest_dir"] = "out2"
            }, wo), ctx);

            Assert.False(bad.Success);
            Assert.Equal("archive.zip_slip", bad.Outputs["error.code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Http_get_obeys_network_guard()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var result = new LinuxHttpGetTextHandler().Execute(new ToolInvocation(new ToolId("linux.net.http_get_text.v1"), new Dictionary<string, object?>
            {
                ["url"] = "https://example.com"
            }, new WorkOrderId("wo")), ToolExecutionContext.Create(root, CancellationToken.None, allowNetwork: false));

            Assert.False(result.Success);
            Assert.Equal("network_disabled", result.Outputs["error.code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Existing_tools_still_support_timeout_and_git()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var handler = new LinuxProcExecHandler();
            var procResult = handler.Execute(new ToolInvocation(handler.Id, new Dictionary<string, object?>
            {
                ["file"] = "sleep",
                ["args"] = new object?[] { "2" },
                ["timeout_ms"] = 10
            }, new WorkOrderId("wo-1")), ToolExecutionContext.Create(root, CancellationToken.None));

            Assert.True(procResult.Success);
            Assert.Equal(true, procResult.Outputs["timed_out"]);

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
