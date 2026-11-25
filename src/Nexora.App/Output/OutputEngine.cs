using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;

namespace Nexora.App.Output;

/// <summary>
/// The audience output engine (NEXORA.exe --output-engine). Its own process, so the
/// screens keep showing the last picture whatever happens to the control screen.
/// It opens one window per named screen, full screen on the display chosen in Settings.
/// </summary>
public sealed class OutputEngine
{
    private static Mutex? _instance;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Dictionary<string, OutputWindow> _windows = new();
    private readonly HashSet<string> _closedByUser = new();
    private NexoraClient? _client;
    private LiveSnapshot _snapshot = LiveSnapshot.Empty;
    private Branding _branding = Branding.Default;
    private DispatcherTimer? _stopWatch;

    public OutputEngine(IClassicDesktopStyleApplicationLifetime desktop) => _desktop = desktop;

    public static bool TryClaimSingleInstance()
    {
        if (OutputEngineHost.EngineRunning()) return false;
        try
        {
            _instance = new Mutex(true, OutputEngineHost.MutexName, out var createdNew);
            if (!createdNew)
            {
                _instance.Dispose();
                _instance = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Output engine mutex unavailable: " + ex.Message);
        }
        try { File.WriteAllText(OutputEngineHost.PidFile, Environment.ProcessId.ToString()); } catch { }
        return true;
    }

    public void Start()
    {
        var config = ThisPcConfig.Load();
        AppLog.Write("Output engine running");
        try { File.Delete(OutputEngineHost.StopFlagFile); } catch { }

        _client = new NexoraClient(
            () => new Uri($"ws://127.0.0.1:{config.Port}{Wire.Path}"),
            () => new Hello(ThisPcConfig.Load().PairingCode, config.PcId + "-output", config.PcName, ClientKind.OutputEngine, AppServices.Version));
        _client.SnapshotChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _snapshot = _client.Snapshot;
            Reconcile();
        });
        _client.PublicInfoChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _branding = _client.PublicInfo?.Branding ?? Branding.Default;
            foreach (var w in _windows.Values) w.View.Branding = _branding;
        });
        _client.Start();

        // Stop only when the Live Output app asks on purpose (it writes a stop flag).
        _stopWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            if (File.Exists(OutputEngineHost.StopFlagFile)) Shutdown();
        });
        _stopWatch.Start();
    }

    private void Reconcile()
    {
        var screens = _snapshot.Screens;
        foreach (var id in _windows.Keys.Where(id => screens.All(s => s.Id != id)).ToList())
        {
            _windows[id].CloseQuietly();
            _windows.Remove(id);
        }

        foreach (var screen in screens)
        {
            if (_windows.TryGetValue(screen.Id, out var w) && w.Display != screen.Display)
            {
                w.CloseQuietly();
                _windows.Remove(screen.Id);
                _closedByUser.Remove(screen.Id);
                w = null;
            }
            if (w is null)
            {
                if (_closedByUser.Contains(screen.Id)) continue;
                w = new OutputWindow(screen.Display);
                var id = screen.Id;
                w.ClosedByUser += () =>
                {
                    _closedByUser.Add(id);
                    _windows.Remove(id);
                };
                _windows[screen.Id] = w;
                w.View.Branding = _branding;
                w.ShowOn(screen);
            }
            w.Title = $"NEXORA · {screen.Name}";
            w.View.Kind = screen.Kind;
            w.View.ScreenName = screen.Name;
            w.View.Frame = _snapshot.FrameFor(screen.Id);
        }
    }

    private void Shutdown()
    {
        _stopWatch?.Stop();
        AppLog.Write("Output engine stopping on request");
        foreach (var w in _windows.Values) w.CloseQuietly();
        _windows.Clear();
        try { File.Delete(OutputEngineHost.StopFlagFile); } catch { }
        try { File.Delete(OutputEngineHost.PidFile); } catch { }
        _ = _client?.DisposeAsync();
        try { _instance?.ReleaseMutex(); } catch { }
        _desktop.Shutdown();
    }
}

public sealed class OutputWindow : Window
{
    private bool _quiet;

    public OutputWindow(int display)
    {
        Display = display;
        View = new ScreenView();
        Content = View;
        Background = Brushes.Black;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Nexora/Assets/nexora.ico")));
        ShowActivated = false;
        ShowInTaskbar = display == 0;
        Closing += (_, _) =>
        {
            if (!_quiet) ClosedByUser?.Invoke();
        };
    }

    public int Display { get; }
    public ScreenView View { get; }
    public event Action? ClosedByUser;

    /// <summary>Shows the window on its display, full screen, or as a normal window for testing.</summary>
    public void ShowOn(ScreenInfo screen)
    {
        var displays = OrderedDisplays(Screens.All);
        if (screen.Display > 0 && screen.Display <= displays.Count)
        {
            var d = displays[screen.Display - 1];
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = d.Bounds.Position;
            Width = d.Bounds.Width / d.Scaling;
            Height = d.Bounds.Height / d.Scaling;
            Topmost = true;
            Cursor = new Cursor(StandardCursorType.None);
            Show();
            WindowState = WindowState.FullScreen;
        }
        else
        {
            Width = 960;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Show();
        }
    }

    public static IReadOnlyList<Avalonia.Platform.Screen> OrderedDisplays(IReadOnlyList<Avalonia.Platform.Screen> all) =>
        all.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    public void CloseQuietly()
    {
        _quiet = true;
        Close();
    }
}
