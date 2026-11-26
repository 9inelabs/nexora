using System.Diagnostics;
using System.Text.Json;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Core.Config;

namespace Nexora.App.Services;

/// <summary>
/// Keeps NEX-Audio's sound engine running as its own process (NEXORA started with --audio-engine).
/// If the engine ever stops while the sound is meant to be on, this starts it again within a few
/// seconds, and the engine reopens the X32 driver with the saved patch by itself.
/// </summary>
public sealed class AudioEngineHost : IDisposable
{
    private readonly Timer _timer;
    private bool _keep;
    private DateTime _lastStart = DateTime.MinValue;

    public AudioEngineHost()
    {
        _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string PipeName => EngineNames.For(AppPaths.Root);
    public int Restarts { get; private set; }
    public bool IsRunning => EngineProcess.IsRunning(AppPaths.Root);

    /// <summary>True when the sound was left running, so NEXORA should bring the engine back at start-up.</summary>
    public static bool SoundWasOn
    {
        get
        {
            try
            {
                var file = Path.Combine(AppPaths.Root, "audio", "engine.json");
                if (!File.Exists(file)) return false;
                return JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(file), AudioMessage.Json)?.Running == true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Starts the engine if it is not running, and watches it from now on.</summary>
    public void Start()
    {
        _keep = true;
        try { File.Delete(EngineProcess.StopFlag(AppPaths.Root)); }
        catch (Exception) { }
        Check();
        _timer.Change(2000, 2000);
    }

    /// <summary>Closes the engine before an update is installed. The sound comes back after the restart.</summary>
    public void StopForUpdate()
    {
        _keep = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            Directory.CreateDirectory(Path.Combine(AppPaths.Root, "audio"));
            File.WriteAllText(EngineProcess.StopFlag(AppPaths.Root), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception) { }
        // Give it a moment to close the driver cleanly.
        for (var i = 0; i < 30 && IsRunning; i++) Thread.Sleep(100);
    }

    /// <summary>The engine was shut down on purpose from the NEX-Audio screen: stop watching.</summary>
    public void Forget()
    {
        _keep = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Check()
    {
        if (!_keep || IsRunning) return;
        if (DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(4)) return;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find NEXORA.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--audio-engine");
            Process.Start(psi);
            if (_lastStart != DateTime.MinValue) Restarts++;
            _lastStart = DateTime.UtcNow;
            AppLog.Write($"Audio engine started (restarts: {Restarts})");
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not start the audio engine: " + ex);
        }
    }

    public void Dispose() => _timer.Dispose();
}
