using System.IO.Compression;
using System.Text;
using System.Text.Json;
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


    [Fact]
    public void Git_batch3_tools_work_non_interactive()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            Run("git", $"init {repo}", root);
            File.WriteAllText(Path.Combine(repo, "a.txt"), "hello");
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            ctx.EnvOverlay["GIT_AUTHOR_NAME"] = "A";
            ctx.EnvOverlay["GIT_AUTHOR_EMAIL"] = "a@example.com";
            ctx.EnvOverlay["GIT_COMMITTER_NAME"] = "A";
            ctx.EnvOverlay["GIT_COMMITTER_EMAIL"] = "a@example.com";

            Assert.True(new LinuxGitAddHandler().Execute(new ToolInvocation(new ToolId("linux.git.add.v1"), new Dictionary<string, object?>
            {
                ["paths"] = new object?[] { "a.txt" },
                ["cwd"] = "repo"
            }, wo), ctx).Success);

            Assert.True(new LinuxGitCommitHandler().Execute(new ToolInvocation(new ToolId("linux.git.commit.v1"), new Dictionary<string, object?>
            {
                ["message"] = "init",
                ["cwd"] = "repo"
            }, wo), ctx).Success);

            var rev = new LinuxGitRevParseHandler().Execute(new ToolInvocation(new ToolId("linux.git.rev_parse.v1"), new Dictionary<string, object?>
            {
                ["args"] = new object?[] { "--abbrev-ref", "HEAD" },
                ["cwd"] = "repo"
            }, wo), ctx);
            Assert.True(rev.Success);
            Assert.Equal("master", rev.Outputs["stdout"]);

            var log = new LinuxGitLogHandler().Execute(new ToolInvocation(new ToolId("linux.git.log.v1"), new Dictionary<string, object?>
            {
                ["max"] = 5,
                ["cwd"] = "repo"
            }, wo), ctx);
            Assert.True(log.Success);
            var hashes = Convert.ToString(log.Outputs["hashes"]) ?? string.Empty;
            Assert.Matches(@"^[0-9a-f]{40}$", hashes.Split('\n')[0]);

            File.WriteAllText(Path.Combine(repo, "b.txt"), "x");
            var diff = new LinuxGitDiffNamesHandler().Execute(new ToolInvocation(new ToolId("linux.git.diff_names.v1"), new Dictionary<string, object?>
            {
                ["cwd"] = "repo"
            }, wo), ctx);
            Assert.True(diff.Success);
            Assert.Equal("b.txt", Convert.ToString(diff.Outputs["paths"]));

            Assert.True(new LinuxGitCheckoutHandler().Execute(new ToolInvocation(new ToolId("linux.git.checkout.v1"), new Dictionary<string, object?>
            {
                ["ref"] = "HEAD",
                ["cwd"] = "repo"
            }, wo), ctx).Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Env_overlay_affects_proc_and_not_host()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            var original = Environment.GetEnvironmentVariable("SHOOTS_TMP_ENV");

            var set = new LinuxEnvSetLocalHandler().Execute(new ToolInvocation(new ToolId("linux.env.set_local.v1"), new Dictionary<string, object?>
            {
                ["name"] = "SHOOTS_TMP_ENV",
                ["value"] = "local"
            }, wo), ctx);
            Assert.True(set.Success);

            var get = new LinuxEnvGetHandler().Execute(new ToolInvocation(new ToolId("linux.env.get.v1"), new Dictionary<string, object?>
            {
                ["name"] = "SHOOTS_TMP_ENV"
            }, wo), ctx);
            Assert.True(get.Success);
            Assert.Equal("local", get.Outputs["value"]);

            var proc = new LinuxProcExecHandler().Execute(new ToolInvocation(new ToolId("linux.proc.exec.v1"), new Dictionary<string, object?>
            {
                ["file"] = "bash",
                ["args"] = new object?[] { "-lc", "printf %s "$SHOOTS_TMP_ENV"" }
            }, wo), ctx);
            Assert.True(proc.Success);
            Assert.Equal("local", Convert.ToString(proc.Outputs["stdout"]));
            Assert.Equal(original, Environment.GetEnvironmentVariable("SHOOTS_TMP_ENV"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Proc_which_finds_git()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var result = new LinuxProcWhichHandler().Execute(new ToolInvocation(new ToolId("linux.proc.which.v1"), new Dictionary<string, object?>
            {
                ["name"] = "git"
            }, new WorkOrderId("wo")), ToolExecutionContext.Create(root, CancellationToken.None));
            Assert.True(result.Success);
            Assert.NotEmpty(Convert.ToString(result.Outputs["path"]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Catalog_contract_guard_has_no_duplicate_input_names_and_outputs()
    {
        var entries = LinuxToolCatalog.LoadEntries(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json"));
        foreach (var entry in entries)
        {
            Assert.NotEmpty(entry.Spec.ToolId.Value);
            Assert.NotNull(entry.Spec.Inputs);
            Assert.NotNull(entry.Spec.Outputs);
            var inputNames = entry.Spec.Inputs.Select(i => i.Name).ToArray();
            Assert.Equal(inputNames.Length, inputNames.Distinct(StringComparer.Ordinal).Count());
            Assert.All(entry.Spec.Outputs, output => Assert.False(string.IsNullOrWhiteSpace(output.Name)));
        }
    }

    [Fact]
    public void Catalog_has_unique_sorted_linux_versioned_ids()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var ids = tools.Select(t => t.GetProperty("id").GetString() ?? string.Empty).ToArray();
        var sorted = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(@"^linux\.[a-z0-9_]+\.[a-z0-9_]+\.v[0-9]+$", id));

        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));

            var requiredAuthority = tool.GetProperty("requiredAuthority");
            Assert.Equal("Embedded", requiredAuthority.GetProperty("providerKind").GetString());
            var capabilities = requiredAuthority.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("ToolExecution", capabilities);

            var tags = tool.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? string.Empty).ToArray();
            Assert.Contains("linux", tags);

            var inputs = tool.GetProperty("inputs").EnumerateArray().ToArray();
            var outputs = tool.GetProperty("outputs").EnumerateArray().ToArray();
            Assert.All(inputs, input => Assert.False(string.IsNullOrWhiteSpace(input.GetProperty("name").GetString())));
            Assert.All(outputs, output => Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("name").GetString())));
        });
    }

    [Fact]
    public void Registry_has_unique_tool_ids()
    {
        var entries = LinuxToolCatalog.LoadEntries(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json"));
        var registry = LinuxToolHandlerRegistry.CreateDefault();
        var ids = entries.Select(e => e.Spec.ToolId.Value).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.NotNull(registry.Resolve(new ToolId(id))));
    }

    [Fact]
    public void Batch4_git_init_branch_and_clean_tools_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            Assert.True(new LinuxGitInitHandler().Execute(new ToolInvocation(new ToolId("linux.git.init.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx).Success);

            var branches = new LinuxGitBranchListHandler().Execute(new ToolInvocation(new ToolId("linux.git.branch_list.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.True(branches.Success);
            Assert.True(Convert.ToInt32(branches.Outputs["count"]) >= 1);

            File.WriteAllText(Path.Combine(repo, "temp.txt"), "x");
            Assert.True(new LinuxGitAddHandler().Execute(new ToolInvocation(new ToolId("linux.git.add.v1"), new Dictionary<string, object?>
            {
                ["paths"] = new object?[] { "temp.txt" },
                ["cwd"] = "repo"
            }, wo), ctx).Success);
            var commit = new LinuxGitCommitHandler().Execute(new ToolInvocation(new ToolId("linux.git.commit.v1"), new Dictionary<string, object?>
            {
                ["message"] = "first",
                ["cwd"] = "repo"
            }, wo), ctx);
            Assert.True(commit.Success);

            var clean = new LinuxGitCleanFdHandler().Execute(new ToolInvocation(new ToolId("linux.git.clean_fd.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.True(clean.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Batch4_fs_text_archive_hash_tools_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            Assert.True(new LinuxFsEnsureDirHandler().Execute(new ToolInvocation(new ToolId("linux.fs.ensure_dir.v1"), new Dictionary<string, object?> { ["path"] = "d" }, wo), ctx).Success);
            Assert.True(new LinuxFsExistsHandler().Execute(new ToolInvocation(new ToolId("linux.fs.exists.v1"), new Dictionary<string, object?> { ["path"] = "d" }, wo), ctx).Success);

            File.WriteAllText(Path.Combine(root, "d", "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(root, "d", "b.md"), "beta");

            var find = new LinuxFsFindFilesHandler().Execute(new ToolInvocation(new ToolId("linux.fs.find_files.v1"), new Dictionary<string, object?> { ["path"] = "d", ["pattern"] = "*.txt" }, wo), ctx);
            Assert.True(find.Success);
            Assert.Equal(1, find.Outputs["count"]);

            var glob = new LinuxFsGlobHandler().Execute(new ToolInvocation(new ToolId("linux.fs.glob.v1"), new Dictionary<string, object?> { ["path"] = "d", ["pattern"] = "*.md" }, wo), ctx);
            Assert.True(glob.Success);
            Assert.Equal(1, glob.Outputs["count"]);

            var rg = new LinuxTextRgHandler().Execute(new ToolInvocation(new ToolId("linux.text.rg.v1"), new Dictionary<string, object?> { ["pattern"] = "alpha", ["path"] = "d" }, wo), ctx);
            Assert.True(rg.Success);

            var replace = new LinuxTextReplaceInFilesHandler().Execute(new ToolInvocation(new ToolId("linux.text.replace_in_files.v1"), new Dictionary<string, object?> { ["path"] = "d", ["search"] = "alpha", ["replace"] = "ALPHA" }, wo), ctx);
            Assert.True(replace.Success);

            var tar = new LinuxArchiveTarGzHandler().Execute(new ToolInvocation(new ToolId("linux.archive.tar_gz.v1"), new Dictionary<string, object?> { ["source_dir"] = "d", ["tar_path"] = "x.tar.gz" }, wo), ctx);
            Assert.True(tar.Success);

            var untar = new LinuxArchiveUntarGzHandler().Execute(new ToolInvocation(new ToolId("linux.archive.untar_gz.v1"), new Dictionary<string, object?> { ["tar_path"] = "x.tar.gz", ["dest_dir"] = "out" }, wo), ctx);
            Assert.True(untar.Success);

            var hash = new LinuxArtifactsHashTreeHandler().Execute(new ToolInvocation(new ToolId("linux.artifacts.hash_tree.v1"), new Dictionary<string, object?> { ["path"] = "out" }, wo), ctx);
            Assert.True(hash.Success);
            Assert.True(Convert.ToInt32(hash.Outputs["count"]) >= 1);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch5_git_status_and_branch_tools_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            Run("git", $"init {repo}", root);
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var branch = new LinuxGitCurrentBranchHandler().Execute(new ToolInvocation(new ToolId("linux.git.current_branch.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.True(branch.Success);

            var remotes = new LinuxGitRemoteListHandler().Execute(new ToolInvocation(new ToolId("linux.git.remote_list.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.True(remotes.Success);
            Assert.Equal(0, remotes.Outputs["count"]);

            File.WriteAllText(Path.Combine(repo, "a.txt"), "x");
            var status = new LinuxGitStatusPorcelainHandler().Execute(new ToolInvocation(new ToolId("linux.git.status_porcelain.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.True(status.Success);
            Assert.Equal(false, status.Outputs["clean"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Batch5_pure_and_diag_tools_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var join = new LinuxFsPathJoinHandler().Execute(new ToolInvocation(new ToolId("linux.fs.path_join.v1"), new Dictionary<string, object?> { ["parts"] = new object?[] { "a", "b", "c.txt" } }, wo), ctx);
            Assert.True(join.Success);
            Assert.Equal("a/b/c.txt", join.Outputs["path"]);

            var temp = new LinuxFsTempDirHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir.v1"), new Dictionary<string, object?> { ["seed"] = "seed", ["root"] = "tmp" }, wo), ctx);
            Assert.True(temp.Success);

            var trim = new LinuxTextTrimHandler().Execute(new ToolInvocation(new ToolId("linux.text.trim.v1"), new Dictionary<string, object?> { ["text"] = "  hello  " }, wo), ctx);
            Assert.True(trim.Success);
            Assert.Equal("hello", trim.Outputs["text"]);

            var toJson = new LinuxTextToJsonHandler().Execute(new ToolInvocation(new ToolId("linux.text.to_json.v1"), new Dictionary<string, object?> { ["map"] = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 } }, wo), ctx);
            Assert.True(toJson.Success);

            var fromJson = new LinuxTextFromJsonHandler().Execute(new ToolInvocation(new ToolId("linux.text.from_json.v1"), new Dictionary<string, object?> { ["json"] = "{\"x\":1,\"y\":2}" }, wo), ctx);
            Assert.True(fromJson.Success);
            Assert.Equal(2, fromJson.Outputs["count"]);

            var ps = new LinuxProcPsHandler().Execute(new ToolInvocation(new ToolId("linux.proc.ps.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(ps.Success);
            Assert.True(Convert.ToInt32(ps.Outputs["count"]) >= 1);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Batch6_archive_and_tag_tools_present()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "x");

            var zip = new LinuxArchiveZipDirHandler().Execute(new ToolInvocation(new ToolId("linux.archive.zip_dir.v1"), new Dictionary<string, object?> { ["source_dir"] = "src", ["zip_path"] = "a.zip" }, wo), ctx);
            Assert.True(zip.Success);

            var unzip = new LinuxArchiveUnzipToDirHandler().Execute(new ToolInvocation(new ToolId("linux.archive.unzip_to_dir.v1"), new Dictionary<string, object?> { ["zip_path"] = "a.zip", ["dest_dir"] = "out", ["overwrite"] = true }, wo), ctx);
            Assert.True(unzip.Success);

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            Run("git", $"init {repo}", root);
            File.WriteAllText(Path.Combine(repo, "a.txt"), "x");
            Run("git", $"-C {repo} add .", root);
            Run("git", $"-C {repo} -c user.email=a@b -c user.name=n commit -m init", root);

            var tag = new LinuxGitTagAnnotatedHandler().Execute(new ToolInvocation(new ToolId("linux.git.tag_annotated.v1"), new Dictionary<string, object?> { ["cwd"] = "repo", ["tag"] = "v1", ["message"] = "m" }, wo), ctx);
            Assert.True(tag.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch7_git_changed_files_and_is_clean_are_deterministic()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            Run("git", $"init {repo}", root);
            File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a");

            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var changed = new LinuxGitChangedFilesHandler().Execute(new ToolInvocation(new ToolId("linux.git.changed_files.v1"), new Dictionary<string, object?>
            {
                ["cwd"] = "repo"
            }, wo), ctx);

            Assert.True(changed.Success);
            Assert.Equal("a.txt
b.txt", changed.Outputs["files"]);
            Assert.Equal(2, changed.Outputs["count"]);

            var clean = new LinuxGitIsCleanHandler().Execute(new ToolInvocation(new ToolId("linux.git.is_clean.v1"), new Dictionary<string, object?>
            {
                ["cwd"] = "repo"
            }, wo), ctx);

            Assert.True(clean.Success);
            Assert.Equal(false, clean.Outputs["is_clean"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Batch8_hash_and_sys_tools_return_stable_shapes()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "x.txt"), "x");
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var fileHash = new LinuxHashFileSha256Handler().Execute(new ToolInvocation(new ToolId("linux.hash.file_sha256.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "x.txt"
            }, wo), ctx);

            Assert.True(fileHash.Success);
            Assert.Matches("^[0-9a-f]{64}$", Convert.ToString(fileHash.Outputs["sha256"]) ?? string.Empty);

            var manifest = new LinuxHashDirManifestHandler().Execute(new ToolInvocation(new ToolId("linux.hash.dir_manifest.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "."
            }, wo), ctx);

            Assert.True(manifest.Success);
            Assert.True(Convert.ToInt32(manifest.Outputs["count"]) >= 1);

            var meminfo = new LinuxSysMemInfoHandler().Execute(new ToolInvocation(new ToolId("linux.sys.meminfo.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(meminfo.Success);
            Assert.True(Convert.ToInt32(meminfo.Outputs["count"]) >= 1);
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
