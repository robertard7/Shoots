using System;
using System.IO;
using System.Runtime.InteropServices;

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
