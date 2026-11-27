using Nexora.Core.Config;

namespace Nexora.App.Services;

/// <summary>Errors and start-up notes, one file per day in the logs folder.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"app-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss}  [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
