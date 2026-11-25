namespace Nexora.Server;

/// <summary>Who did what, when: one small text file per day, kept 90 days.</summary>
public sealed class ActivityLog
{
    private readonly string? _dir;
    private readonly object _gate = new();

    public ActivityLog(string? dir)
    {
        _dir = dir;
        if (_dir is null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir, "activity-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-90)) File.Delete(f);
        }
        catch (Exception) { }
    }

    public void Write(string line)
    {
        if (_dir is null) return;
        try
        {
            lock (_gate)
                File.AppendAllText(Path.Combine(_dir, $"activity-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
