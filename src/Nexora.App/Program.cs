using Avalonia;
using Nexora.App.Output;
using Nexora.App.Services;
using Velopack;

namespace Nexora.App;

internal static class Program
{
    public static bool IsOutputEngine { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install, update and uninstall hooks from the installer.
        VelopackApp.Build().Run();

        IsOutputEngine = args.Contains("--output-engine");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved: " + e.Exception);
            e.SetObserved();
        };

        if (IsOutputEngine && !OutputEngine.TryClaimSingleInstance())
            return 0; // already running: nothing to do

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
