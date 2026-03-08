using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

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
            return Task.CompletedTask;

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) && !File.Exists(full))
            return Task.CompletedTask;

        NativeMethods.ShellExecute(IntPtr.Zero, "open", full, null, null, 1);
        return Task.CompletedTask;
    }


    // Clipboard is only supported for active Windows UI sessions.
    public Task CopyTextAsync(string text, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        var app = Application.Current;
        if (app?.Dispatcher is null)
            return Task.CompletedTask;

        if (app.Dispatcher.CheckAccess())
        {
            Clipboard.SetText(text);
            return Task.CompletedTask;
        }

        return app.Dispatcher.InvokeAsync(() => Clipboard.SetText(text)).Task;
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
