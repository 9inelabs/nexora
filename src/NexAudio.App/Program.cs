using Avalonia;
using NexAudio.App.Services;
using Velopack;

namespace NexAudio.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install, update and uninstall hooks from the installer.
        VelopackApp.Build().Run();
        DeskPaths.Use();

        // Plugins each run in their own small process, so a crashing plugin cannot stop the sound.
        if (args.Contains("--plugin-host")) return Nexora.Audio.Plugins.PluginHost.RunProcess(args);
        var scan = Array.IndexOf(args, "--plugin-scan");
        if (scan >= 0 && scan + 1 < args.Length) return Nexora.Audio.Plugins.PluginScanner.RunScanProcess(args[scan + 1]);
        if (args.Contains("--audio-engine"))
        {
            // The sound engine: no window, runs until asked to stop.
            AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Audio engine unhandled: " + e.ExceptionObject);
            return Nexora.Audio.Engine.EngineProcess.Run(Nexora.Core.Config.AppPaths.Root, m => AppLog.Write(m));
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved: " + e.Exception);
            e.SetObserved();
        };
        DeskPaths.BringAcrossFromNexora(AppLog.Write);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Write("Fatal: " + ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
