using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
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
            Assert.Equal("tool.network_disabled", result.Outputs["error.code"]);
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
            Assert.True(outputs.Length >= 1);
            Assert.All(inputs, input =>
            {
                Assert.False(string.IsNullOrWhiteSpace(input.GetProperty("name").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(input.GetProperty("type").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(input.GetProperty("description").GetString()));
                Assert.True(input.TryGetProperty("required", out var required));
                Assert.True(required.ValueKind is JsonValueKind.True or JsonValueKind.False);
            });
            Assert.All(outputs, output =>
            {
                Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("name").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("type").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("description").GetString()));
            });
        });
    }

    [Fact]
    public void Catalog_hash_is_reported_for_release_notes()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json");
        var bytes = File.ReadAllBytes(catalogPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        Assert.Matches(@"^[0-9a-f]{64}$", hash);
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
    public void Registry_does_not_expose_handler_ids_missing_from_catalog()
    {
        var entries = LinuxToolCatalog.LoadEntries(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "etc", "tools.catalog.json"));
        var catalogIds = entries.Select(e => e.Spec.ToolId.Value).ToHashSet(StringComparer.Ordinal);
        var registry = LinuxToolHandlerRegistry.CreateDefault();

        var handlersField = typeof(LinuxToolHandlerRegistry).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handlersField);
        var handlers = Assert.IsType<Dictionary<string, IToolHandler>>(handlersField!.GetValue(registry));

        Assert.All(handlers.Keys, id => Assert.Contains(id, catalogIds));
    }

    [Fact]
    public void Registry_constructor_rejects_duplicate_ids()
    {
        var handlers = new IToolHandler[]
        {
            new LinuxFsExistsHandler(),
            new LinuxFsExistsHandler()
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new LinuxToolHandlerRegistry(handlers));
        Assert.StartsWith("duplicate tool handler id:", ex.Message, StringComparison.Ordinal);
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


    [Fact]
    public void Batch7_patch_and_marker_tools_work()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n", Encoding.UTF8);
            var diff = "--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-hello\n+world\n";
            var patched = new LinuxTextApplyUnifiedDiffHandler().Execute(new ToolInvocation(new ToolId("linux.text.apply_unified_diff.v1"), new Dictionary<string, object?>
            {
                ["base_dir_rel"] = ".",
                ["diff_text"] = diff
            }, wo), ctx);
            Assert.True(patched.Success);
            Assert.Equal("world\n", File.ReadAllText(Path.Combine(root, "a.txt"), Encoding.UTF8));

            var escape = new LinuxTextApplyUnifiedDiffHandler().Execute(new ToolInvocation(new ToolId("linux.text.apply_unified_diff.v1"), new Dictionary<string, object?>
            {
                ["base_dir_rel"] = ".",
                ["diff_text"] = "--- a/../../x\n+++ b/../../x\n@@ -0,0 +1 @@\n+x\n"
            }, wo), ctx);
            Assert.False(escape.Success);
            Assert.Equal("text.patch_path_escape", escape.Outputs["error.code"]);

            var invalid = new LinuxTextApplyUnifiedDiffHandler().Execute(new ToolInvocation(new ToolId("linux.text.apply_unified_diff.v1"), new Dictionary<string, object?> { ["base_dir_rel"] = ".", ["diff_text"] = "not a patch" }, wo), ctx);
            Assert.False(invalid.Success);
            Assert.Equal("text.patch_invalid", invalid.Outputs["error.code"]);

            File.WriteAllText(Path.Combine(root, "m.txt"), "A<start>mid<end>Z", Encoding.UTF8);
            var extract = new LinuxTextExtractBetweenMarkersHandler().Execute(new ToolInvocation(new ToolId("linux.text.extract_between_markers.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "m.txt", ["start_marker"] = "<start>", ["end_marker"] = "<end>"
            }, wo), ctx);
            Assert.True(extract.Success);
            Assert.Equal("mid", extract.Outputs["text"]);
            var missing = new LinuxTextExtractBetweenMarkersHandler().Execute(new ToolInvocation(new ToolId("linux.text.extract_between_markers.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "m.txt", ["start_marker"] = "<none>", ["end_marker"] = "<end>"
            }, wo), ctx);
            Assert.False(missing.Success);
            Assert.Equal("text.marker_not_found", missing.Outputs["error.code"]);

            File.WriteAllText(Path.Combine(root, "i.txt"), "XMARKY", Encoding.UTF8);
            var ins1 = new LinuxTextInsertAfterMarkerHandler().Execute(new ToolInvocation(new ToolId("linux.text.insert_after_marker.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "i.txt", ["marker"] = "MARK", ["insert_text"] = "-I", ["once"] = true
            }, wo), ctx);
            var ins2 = new LinuxTextInsertAfterMarkerHandler().Execute(new ToolInvocation(new ToolId("linux.text.insert_after_marker.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "i.txt", ["marker"] = "MARK", ["insert_text"] = "-I", ["once"] = true
            }, wo), ctx);
            Assert.True(ins1.Success);
            Assert.True(ins2.Success);
            Assert.Equal(0, ins2.Outputs["count"]);

            File.WriteAllText(Path.Combine(root, "eol.txt"), "a\r\nb\r\n", Encoding.UTF8);
            var norm1 = new LinuxTextLineEndingNormalizeHandler().Execute(new ToolInvocation(new ToolId("linux.text.line_ending_normalize.v1"), new Dictionary<string, object?> { ["path_rel"] = "eol.txt", ["mode"] = "lf" }, wo), ctx);
            var norm2 = new LinuxTextLineEndingNormalizeHandler().Execute(new ToolInvocation(new ToolId("linux.text.line_ending_normalize.v1"), new Dictionary<string, object?> { ["path_rel"] = "eol.txt", ["mode"] = "lf" }, wo), ctx);
            Assert.True(norm1.Success);
            Assert.True((bool)norm1.Outputs["changed"]);
            Assert.False((bool)norm2.Outputs["changed"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Batch8_9_10_new_tools_work_core_paths()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            Assert.True(new LinuxFsTouchHandler().Execute(new ToolInvocation(new ToolId("linux.fs.touch.v1"), new Dictionary<string, object?> { ["path_rel"] = "t.txt" }, wo), ctx).Success);
            var ensure1 = new LinuxFsEnsureFileHandler().Execute(new ToolInvocation(new ToolId("linux.fs.ensure_file.v1"), new Dictionary<string, object?> { ["path_rel"] = "e.txt", ["content"] = "one" }, wo), ctx);
            var ensure2 = new LinuxFsEnsureFileHandler().Execute(new ToolInvocation(new ToolId("linux.fs.ensure_file.v1"), new Dictionary<string, object?> { ["path_rel"] = "e.txt" }, wo), ctx);
            Assert.True(ensure1.Success);
            Assert.False((bool)ensure2.Outputs["created"]);
            var chmodInvalid = new LinuxFsChmodHandler().Execute(new ToolInvocation(new ToolId("linux.fs.chmod.v1"), new Dictionary<string, object?> { ["path_rel"] = "e.txt", ["mode_octal"] = "88" }, wo), ctx);
            Assert.False(chmodInvalid.Success);
            Assert.Equal("fs.chmod_failed", chmodInvalid.Outputs["error.code"]);
            Assert.True(new LinuxFsSymlinkHandler().Execute(new ToolInvocation(new ToolId("linux.fs.symlink.v1"), new Dictionary<string, object?> { ["link_path_rel"] = "l.txt", ["target_rel"] = "e.txt" }, wo), ctx).Success);
            var readLink = new LinuxFsReadlinkHandler().Execute(new ToolInvocation(new ToolId("linux.fs.readlink.v1"), new Dictionary<string, object?> { ["path_rel"] = "l.txt" }, wo), ctx);
            Assert.True(readLink.Success);
            Assert.Equal("e.txt", readLink.Outputs["target"]);
            var real = new LinuxFsRealpathHandler().Execute(new ToolInvocation(new ToolId("linux.fs.realpath.v1"), new Dictionary<string, object?> { ["path_rel"] = "./e.txt" }, wo), ctx);
            Assert.True(real.Success);
            Assert.DoesNotContain("..", Convert.ToString(real.Outputs["real_rel"]));

            var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo); Run("git", $"init {repo}", root);
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a"); Run("git", $"-C {repo} add .", root); Run("git", $"-C {repo} -c user.email=a@b -c user.name=n commit -m init", root);
            var amend = new LinuxGitCommitAmendHandler().Execute(new ToolInvocation(new ToolId("linux.git.commit_amend.v1"), new Dictionary<string, object?> { ["cwd"] = "repo", ["no_edit"] = true }, wo), ctx);
            Assert.True(amend.Success);
            Assert.True(new LinuxGitRemoteSetUrlHandler().Execute(new ToolInvocation(new ToolId("linux.git.remote_set_url.v1"), new Dictionary<string, object?> { ["cwd"] = "repo", ["name"] = "origin", ["url"] = repo }, wo), ctx).Success);
            var sub = new LinuxGitSubmoduleUpdateHandler().Execute(new ToolInvocation(new ToolId("linux.git.submodule_update.v1"), new Dictionary<string, object?> { ["cwd"] = "repo" }, wo), ctx);
            Assert.False(sub.Success);
            var localSource = Path.Combine(root, "local-src");
            Directory.CreateDirectory(localSource);
            Run("git", $"init {localSource}", root);
            File.WriteAllText(Path.Combine(localSource, "z.txt"), "z");
            Run("git", $"-C {localSource} add .", root);
            Run("git", $"-C {localSource} -c user.email=a@b -c user.name=n commit -m init", root);
            var localClone = new LinuxGitCloneDepthHandler().Execute(new ToolInvocation(new ToolId("linux.git.clone_depth.v1"), new Dictionary<string, object?> { ["url"] = localSource, ["dest_rel"] = "local-clone" }, wo), ToolExecutionContext.Create(root, CancellationToken.None, allowNetwork: false));
            Assert.True(localClone.Success);

            var cloneBlocked = new LinuxGitCloneDepthHandler().Execute(new ToolInvocation(new ToolId("linux.git.clone_depth.v1"), new Dictionary<string, object?> { ["url"] = "https://example.com/x.git", ["dest_rel"] = "clone" }, wo), ToolExecutionContext.Create(root, CancellationToken.None, allowNetwork: false));
            Assert.False(cloneBlocked.Success);
            Assert.Equal("tool.network_disabled", cloneBlocked.Outputs["error.code"]);

            Assert.True(new LinuxSysUnameHandler().Execute(new ToolInvocation(new ToolId("linux.sys.uname.v1"), new Dictionary<string, object?>(), wo), ctx).Success);
            var disk = new LinuxSysDiskFreeHandler().Execute(new ToolInvocation(new ToolId("linux.sys.disk_free.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(disk.Success);
            Assert.True(Convert.ToInt64(disk.Outputs["bytes_total"]) > 0);
            Environment.SetEnvironmentVariable("SHOOTS_ENV_DUMP_TEST", "ok");
            var env = new LinuxSysEnvDumpSafeHandler().Execute(new ToolInvocation(new ToolId("linux.sys.env_dump_safe.v1"), new Dictionary<string, object?> { ["allowlist"] = "HOME\nSHOOTS_ENV_DUMP_TEST" }, wo), ctx);
            Assert.True(env.Success);
            var envText = Convert.ToString(env.Outputs["env"]) ?? string.Empty;
            Assert.Contains("SHOOTS_ENV_DUMP_TEST=ok", envText);
        }
        finally { Directory.Delete(root, true); }
    }


    [Fact]
    public void Batch23_sys_command_exists_and_tool_versions_shapes_are_stable()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var exists = new LinuxSysCommandExistsHandler().Execute(new ToolInvocation(new ToolId("linux.sys.command_exists.v1"), new Dictionary<string, object?>
            {
                ["command"] = "bash"
            }, wo), ctx);
            Assert.True(exists.Success);
            Assert.True(Convert.ToBoolean(exists.Outputs["exists"]));

            var missing = new LinuxSysCommandExistsHandler().Execute(new ToolInvocation(new ToolId("linux.sys.command_exists.v1"), new Dictionary<string, object?>
            {
                ["command"] = "definitely-not-a-real-command-xyz"
            }, wo), ctx);
            Assert.True(missing.Success);
            Assert.False(Convert.ToBoolean(missing.Outputs["exists"]));

            var versions = new LinuxSysToolVersionsHandler().Execute(new ToolInvocation(new ToolId("linux.sys.tool_versions.v1"), new Dictionary<string, object?>
            {
                ["tools"] = new object?[] { "bash", "git", "definitely-not-a-real-command-xyz" }
            }, wo), ctx);

            Assert.True(versions.Success);
            Assert.Equal(3, versions.Outputs["count"]);
            var payload = Convert.ToString(versions.Outputs["versions"]) ?? string.Empty;
            var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var sorted = lines.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, lines);
            Assert.Contains(lines, static l => l.StartsWith("definitely-not-a-real-command-xyz=not_found", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch24_pkg_tools_network_gate_and_schema_are_stable()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var offline = ToolExecutionContext.Create(root, CancellationToken.None, allowNetwork: false);

            var blockedUpdate = new LinuxPkgUpdateIndexesHandler().Execute(new ToolInvocation(new ToolId("linux.pkg.update_indexes.v1"), new Dictionary<string, object?>(), wo), offline);
            Assert.False(blockedUpdate.Success);
            Assert.Equal("tool.network_disabled", blockedUpdate.Outputs["error.code"]);

            var blockedInstall = new LinuxPkgInstallHandler().Execute(new ToolInvocation(new ToolId("linux.pkg.install.v1"), new Dictionary<string, object?>
            {
                ["packages"] = new object?[] { "git" }
            }, wo), offline);
            Assert.False(blockedInstall.Success);
            Assert.Equal("tool.network_disabled", blockedInstall.Outputs["error.code"]);

            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            var detect = new LinuxPkgDetectManagerHandler().Execute(new ToolInvocation(new ToolId("linux.pkg.detect_manager.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(detect.Success);
            Assert.True(detect.Outputs.ContainsKey("manager"));
            Assert.True(detect.Outputs.ContainsKey("detected"));

            var query = new LinuxPkgQueryInstalledHandler().Execute(new ToolInvocation(new ToolId("linux.pkg.query_installed.v1"), new Dictionary<string, object?>
            {
                ["prefix"] = "git"
            }, wo), ctx);

            if (query.Success)
            {
                Assert.True(query.Outputs.ContainsKey("packages"));
                var list = (Convert.ToString(query.Outputs["packages"]) ?? string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var sorted = list.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
                Assert.Equal(sorted, list);
            }
            else
            {
                Assert.Equal("tool.not_available", query.Outputs["error.code"]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch25_cpp_tools_report_not_available_or_build_with_root_confinement()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var cmd = new LinuxSysCommandExistsHandler().Execute(new ToolInvocation(new ToolId("linux.sys.command_exists.v1"), new Dictionary<string, object?> { ["command"] = "gcc" }, wo), ctx);
            var gccAvailable = cmd.Success && Convert.ToBoolean(cmd.Outputs["exists"]);

            File.WriteAllText(Path.Combine(root, "hello.c"), "int main(){return 0;}
", Encoding.UTF8);

            var compile = new LinuxCppCompileGccHandler().Execute(new ToolInvocation(new ToolId("linux.cpp.compile_gcc.v1"), new Dictionary<string, object?>
            {
                ["source_rel"] = "hello.c",
                ["output_rel"] = "obj/hello.o"
            }, wo), ctx);

            if (!gccAvailable)
            {
                Assert.False(compile.Success);
                Assert.Equal("tool.not_available", compile.Outputs["error.code"]);
            }
            else
            {
                Assert.True(compile.Success);
                Assert.True(File.Exists(Path.Combine(root, "obj", "hello.o")));

                var link = new LinuxCppLinkGccHandler().Execute(new ToolInvocation(new ToolId("linux.cpp.link_gcc.v1"), new Dictionary<string, object?>
                {
                    ["inputs_rel"] = new object?[] { "obj/hello.o" },
                    ["output_rel"] = "bin/hello"
                }, wo), ctx);
                Assert.True(link.Success);

                var escape = new LinuxCppLinkGccHandler().Execute(new ToolInvocation(new ToolId("linux.cpp.link_gcc.v1"), new Dictionary<string, object?>
                {
                    ["inputs_rel"] = new object?[] { "obj/hello.o" },
                    ["output_rel"] = "../escape-bin"
                }, wo), ctx);
                Assert.False(escape.Success);
            }

            var pkgConfigAvailable = new LinuxSysCommandExistsHandler().Execute(new ToolInvocation(new ToolId("linux.sys.command_exists.v1"), new Dictionary<string, object?> { ["command"] = "pkg-config" }, wo), ctx);
            var pkg = new LinuxCppPkgConfigHandler().Execute(new ToolInvocation(new ToolId("linux.cpp.pkg_config.v1"), new Dictionary<string, object?>
            {
                ["package"] = "definitely-not-real-pkg"
            }, wo), ctx);

            if (!Convert.ToBoolean(pkgConfigAvailable.Outputs["exists"]))
            {
                Assert.False(pkg.Success);
                Assert.Equal("tool.not_available", pkg.Outputs["error.code"]);
            }
            else
            {
                Assert.False(pkg.Success);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch26_text_verify_tools_return_not_available_or_stable_shape()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);
            File.WriteAllText(Path.Combine(root, "a.cpp"), "int main(){return 0;}\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "bad.txt"), "line with space \n", Encoding.UTF8);

            var clangFormat = new LinuxTextClangFormatVerifyHandler().Execute(new ToolInvocation(new ToolId("linux.text.clang_format_verify.v1"), new Dictionary<string, object?> { ["path_rel"] = "a.cpp" }, wo), ctx);
            if (!clangFormat.Success)
                Assert.Equal("tool.not_available", clangFormat.Outputs["error.code"]);

            var clangTidy = new LinuxTextClangTidyVerifyHandler().Execute(new ToolInvocation(new ToolId("linux.text.clang_tidy_verify.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "a.cpp",
                ["build_dir_rel"] = "."
            }, wo), ctx);
            if (!clangTidy.Success)
                Assert.Equal("tool.not_available", clangTidy.Outputs["error.code"]);

            var prettier = new LinuxTextPrettierVerifyHandler().Execute(new ToolInvocation(new ToolId("linux.text.prettier_verify.v1"), new Dictionary<string, object?> { ["path_rel"] = "a.cpp" }, wo), ctx);
            if (!prettier.Success)
                Assert.Equal("tool.not_available", prettier.Outputs["error.code"]);

            var editor = new LinuxTextEditorconfigCheckHandler().Execute(new ToolInvocation(new ToolId("linux.text.editorconfig_check.v1"), new Dictionary<string, object?>
            {
                ["path_rel"] = "bad.txt",
                ["line_ending"] = "lf"
            }, wo), ctx);
            Assert.True(editor.Success);
            Assert.Equal(true, editor.Outputs["trailing_whitespace"]);
            Assert.Equal(false, editor.Outputs["verified"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch41_privileged_gate_denies_system_service_tools_when_disabled()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None, allowPrivileged: false);

            var status = new LinuxSysSystemctlStatusHandler().Execute(new ToolInvocation(new ToolId("linux.sys.systemctl_status.v1"), new Dictionary<string, object?>
            {
                ["unit"] = "sshd.service"
            }, wo), ctx);
            Assert.False(status.Success);
            Assert.Equal("tool.privileged_disabled", status.Outputs["error.code"]);

            var restart = new LinuxSysSystemctlRestartHandler().Execute(new ToolInvocation(new ToolId("linux.sys.systemctl_restart.v1"), new Dictionary<string, object?>
            {
                ["unit"] = "sshd.service"
            }, wo), ctx);
            Assert.False(restart.Success);
            Assert.Equal("tool.privileged_disabled", restart.Outputs["error.code"]);

            var journal = new LinuxSysJournalctlTailHandler().Execute(new ToolInvocation(new ToolId("linux.sys.journalctl_tail.v1"), new Dictionary<string, object?>
            {
                ["lines"] = 10
            }, wo), ctx);
            Assert.False(journal.Success);
            Assert.Equal("tool.privileged_disabled", journal.Outputs["error.code"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Batch31_temp_dir_primitives_are_deterministic_and_confined()
    {
        var root = Directory.CreateTempSubdirectory("tools-linux-").FullName;
        try
        {
            var wo = new WorkOrderId("wo-temp");
            var ctx = ToolExecutionContext.Create(root, CancellationToken.None);

            var create = new LinuxFsTempDirCreateHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir_create.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(create.Success);
            var pathRel = Convert.ToString(create.Outputs["path_rel"]) ?? string.Empty;
            Assert.StartsWith(".shoots/tmp/", pathRel, StringComparison.Ordinal);

            var list1 = new LinuxFsTempDirListHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir_list.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(list1.Success);
            Assert.Contains(pathRel, Convert.ToString(list1.Outputs["dirs"]) ?? string.Empty, StringComparison.Ordinal);

            var escapeDelete = new LinuxFsTempDirDeleteHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir_delete.v1"), new Dictionary<string, object?> { ["path_rel"] = "../bad" }, wo), ctx);
            Assert.False(escapeDelete.Success);
            Assert.Equal("fs.path_escape", escapeDelete.Outputs["error.code"]);

            var del = new LinuxFsTempDirDeleteHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir_delete.v1"), new Dictionary<string, object?> { ["path_rel"] = pathRel }, wo), ctx);
            Assert.True(del.Success);

            var list2 = new LinuxFsTempDirListHandler().Execute(new ToolInvocation(new ToolId("linux.fs.temp_dir_list.v1"), new Dictionary<string, object?>(), wo), ctx);
            Assert.True(list2.Success);
            Assert.DoesNotContain(pathRel, Convert.ToString(list2.Outputs["dirs"]) ?? string.Empty, StringComparison.Ordinal);
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
