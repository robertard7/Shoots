using System.Diagnostics;
using System.IO;

namespace Shoots.UI;

internal static class FatalErrorReport
{
    private const string FileName = "fatal-error.log";

    public static string? Write(System.Exception exception)
    {
        try
        {
            var basePath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Shoots");

            Directory.CreateDirectory(basePath);

            var logPath = Path.Combine(basePath, FileName);
            File.WriteAllText(logPath, exception.ToString());

            return logPath;
        }
        catch (System.Exception ex)
        {
            Trace.WriteLine($"[Shoots.UI] Failed to write fatal error report: {ex.Message}");
            return null;
        }
    }
}
