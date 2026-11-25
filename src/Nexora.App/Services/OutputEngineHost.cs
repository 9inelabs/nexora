using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nexora.Core.Config;

namespace Nexora.App.Services;

/// <summary>
/// Runs the audience output engine as its own process, apart from the control screen.
/// If the control screen freezes or closes, the audience screens keep running.
/// A watchdog restarts the engine within about two seconds if it ever stops.
/// </summary>
public sealed class OutputEngineHost : IDisposable
{
    private readonly Timer _timer;
    private bool _stopped = true;
    private DateTime _lastStart = DateTime.MinValue;

    public OutputEngineHost()
    {
        _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string MutexName =>
        "Global\\NEXORA.OutputEngine." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..12];

    public static string PidFile => Path.Combine(AppPaths.Ensure(AppPaths.Root), "output-engine.pid");

    public static string StopFlagFile => Path.Combine(AppPaths.Ensure(AppPaths.Root), "output-engine.stop");

    public int Restarts { get; private set; }
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        _stopped = false;
        try { File.Delete(StopFlagFile); } catch { }
        Check();
        _timer.Change(1000, 1000);
    }

    /// <summary>Restart the output windows (for example after moving screens to other displays).</summary>
    public void Restart()
    {
        StopEngine();
        // The engine sees the stop flag within half a second; the watchdog then starts a fresh one.
        Task.Delay(900).ContinueWith(_ => Start());
    }

    /// <summary>Turns the audience screens off on purpose. The watchdog stays quiet until Start.</summary>
    public void StopEngine()
    {
        _stopped = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        try { File.WriteAllText(StopFlagFile, DateTime.UtcNow.ToString("O")); } catch { }
    }

    public static bool EngineRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch (Exception) { }
        // Second check: the engine writes its process id when it starts.
        try
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited && p.ProcessName.Contains("Nexora", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception) { }
        return false;
    }

    private void Check()
    {
        if (_stopped) return;
        var running = EngineRunning();
        if (!running && DateTime.UtcNow - _lastStart > TimeSpan.FromSeconds(3))
        {
            try
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find NEXORA.exe");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--output-engine");
                Process.Start(psi);
                if (_lastStart != DateTime.MinValue) Restarts++;
                _lastStart = DateTime.UtcNow;
                LastError = null;
                AppLog.Write($"Output engine started (restarts: {Restarts})");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Write("Could not start output engine: " + ex);
            }
        }
        if (running != IsRunning)
        {
            IsRunning = running;
            Changed?.Invoke();
        }
    }

    public void Dispose() => _timer.Dispose();
}
