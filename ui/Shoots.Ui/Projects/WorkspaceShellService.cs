using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class WorkspaceShellService : IWorkspaceShellService
{
    public bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!Directory.Exists(path))
            return false;

        var result = NativeMethods.ShellExecute(IntPtr.Zero, "open", path, null, null, 1);
        return result.ToInt64() > 32;
    }

    public Task OpenFolderAsync(string path, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        if (!OperatingSystem.IsWindows())
        {
            Trace.WriteLine("ui.shell.unsupported_os");
            return Task.CompletedTask;
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) && !File.Exists(full))
            return Task.CompletedTask;

        if (File.Exists(full))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true
            });

            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{full}\"",
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr ShellExecute(
            IntPtr hwnd,
            string lpOperation,
            string lpFile,
            string? lpParameters,
            string? lpDirectory,
            int nShowCmd);
    }
}
