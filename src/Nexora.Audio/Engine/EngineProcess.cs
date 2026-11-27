using System.Diagnostics;
using System.Runtime;
using Nexora.Audio.Ipc;

namespace Nexora.Audio.Engine;

/// <summary>
/// The NEX-Audio sound engine as its own process (NEXORA started with --audio-engine).
/// It has no window. The NEX-Audio screen connects to it, and if the screen freezes or closes
/// the sound carries on. Only one runs per PC (per data folder).
/// </summary>
public static class EngineProcess
{
    public static string MutexName(string dataRoot) => "Global\\NEXORA." + EngineNames.For(dataRoot);

    public static bool IsRunning(string dataRoot)
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName(dataRoot), out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>A file the screen writes to ask the engine to close (before NEXORA installs an update).</summary>
    public static string StopFlag(string dataRoot) => Path.Combine(dataRoot, "audio", "engine.stop");

    public static int Run(string dataRoot, Action<string> log)
    {
        using var mutex = new Mutex(true, MutexName(dataRoot), out var mine);
        if (!mine) return 0; // already running

        // Keep the memory clean-up from pausing the program while sound is running.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        if (OperatingSystem.IsWindows())
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch (Exception) { }
        }

        var quit = new ManualResetEventSlim();
        try { File.Delete(StopFlag(dataRoot)); }
        catch (Exception) { }
        log($"Audio engine process started (pid {Environment.ProcessId}).");
        var audioDir = Path.Combine(dataRoot, "audio");
        using var desk = new Desk(audioDir, log);
        using (var engine = new AudioEngine(audioDir, log, desk: desk))
        using (var server = new EngineServer(engine, EngineNames.For(dataRoot), log))
        {
            server.QuitRequested += quit.Set;
            while (!quit.Wait(500))
                if (File.Exists(StopFlag(dataRoot)))
                {
                    log("Audio engine asked to close (update).");
                    break;
                }
            Thread.Sleep(200); // let the last status reach the screen
        }
        log("Audio engine process stopped.");
        return 0;
    }
}
