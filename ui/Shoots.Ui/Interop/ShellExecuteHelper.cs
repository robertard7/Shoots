using System.Runtime.InteropServices;

namespace Shoots.UI.Interop;

internal static class ShellExecuteHelper
{
    private const int ShowNormal = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint ShellExecuteW(nint hwnd, string? lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

    public static bool OpenPath(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return false;

        var result = ShellExecuteW(nint.Zero, "open", path, null, null, ShowNormal);
        return result.ToInt64() > 32;
    }
}
