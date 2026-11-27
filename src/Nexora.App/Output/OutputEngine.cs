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
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _frameTimer;
    private readonly Recorder _recorder = new();
    private readonly NexAudioLink _nexAudio = new();
    private StageLayout _stage = StageLayout.Default;
    private DateTime _framesStart;
    private long _framesSent;
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAt = DateTime.UtcNow;
    private double _cpu;
    private double _renderFps;
    private int _renderCount;
    private DateTime _renderCountAt = DateTime.UtcNow;
    private IReadOnlyList<string> _devices = [];
    private IReadOnlyList<string> _audio = [];
    private IReadOnlyList<string> _ndi = [];
    private DateTime _devicesAt = DateTime.MinValue;

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
        _client.MessageReceived += m => Dispatcher.UIThread.Post(() => OnMessage(m));
        _client.Start();

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SendStatus());
        _statusTimer.Start();
        Power.KeepAwake(true);

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
            if (_windows.TryGetValue(screen.Id, out var w) && (w.Display != screen.Display || w.Placement != OutputWindow.PlacementOf(screen)))
            {
                w.CloseQuietly();
                _windows.Remove(screen.Id);
                _closedByUser.Remove(screen.Id);
                w = null;
            }
            if (w is null)
            {
                if (_closedByUser.Contains(screen.Id)) continue;
                w = new OutputWindow(screen.Display, screen.Kind == ScreenKind.Broadcast);
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
            var (cw, ch) = screen.Canvas();
            w.View.Shape = screen.Kind == ScreenKind.Broadcast ? default : new Size(cw, ch);
            w.View.TestPattern = screen.TestPattern && screen.Kind != ScreenKind.Broadcast;
            w.View.StretchToFill = screen.StretchToFill && screen.Kind != ScreenKind.Broadcast;
            w.View.StageLayout = _stage;
            var frame = _snapshot.FrameFor(screen.Id);
            w.View.Frame = frame;
            if (w.CleanView is { } clean)
            {
                clean.Kind = ScreenKind.Broadcast;
                clean.Frame = frame;
            }
        }
    }

    private OutputWindow? BroadcastWindow => _windows.Values.FirstOrDefault(w => w.View.Kind == ScreenKind.Broadcast);

    // ---------------- Messages from the Live Output app ----------------

    private void OnMessage(Message m)
    {
        switch (m)
        {
            case EngineSettings es:
                _recorder.Settings = es.Settings;
                _nexAudio.Apply(es.Settings);
                _recorder.Relay = _nexAudio.Relay;
                _stage = es.Stage;
                foreach (var w in _windows.Values) w.View.StageLayout = _stage;
                break;
            case EngineCommand ec:
                string? problem = null;
                switch (ec.Action)
                {
                    case EngineAction.StartRecording: problem = _recorder.StartRecording(ec.ServiceName); break;
                    case EngineAction.StopRecording: _recorder.StopRecording(); break;
                    case EngineAction.StartStream: problem = _recorder.StartStream(); break;
                    case EngineAction.StopStream: _recorder.StopStream(); break;
                }
                if (problem is not null) AppLog.Write(problem);
                UpdateFrameTimer();
                SendStatus();
                break;
        }
    }

    // ---------------- Recording: draw the Broadcast picture into frames ----------------

    private void UpdateFrameTimer()
    {
        if (_recorder.Active && _frameTimer is null)
        {
            _framesStart = DateTime.UtcNow;
            _framesSent = 0;
            _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / _recorder.Fps / 2), DispatcherPriority.Render, (_, _) => CaptureFrame());
            _frameTimer.Start();
        }
        else if (!_recorder.Active && _frameTimer is not null)
        {
            _frameTimer.Stop();
            _frameTimer = null;
        }
    }

    private Avalonia.Media.Imaging.RenderTargetBitmap? _rtb;
    private Avalonia.Media.Imaging.RenderTargetBitmap? _rtbClean;

    private void CaptureFrame()
    {
        // Keep the number of frames in step with the wall clock, so the sound never drifts away from the picture.
        var due = (long)((DateTime.UtcNow - _framesStart).TotalSeconds * _recorder.Fps);
        var count = due - _framesSent;
        if (count <= 0) return;
        if (count > 30) { _framesSent = due - 1; count = 1; }
        var w = _recorder.Width;
        var h = _recorder.Height;
        var view = BroadcastWindow?.View;
        try
        {
            var frame = Render(view?.Canvas, ref _rtb, w, h);
            Frame? clean = null;
            if (_recorder.CleanWanted) clean = Render(BroadcastWindow?.CleanView?.Canvas, ref _rtbClean, w, h);
            for (var i = 0; i < count; i++)
            {
                _recorder.Push(frame);
                if (clean is not null) _recorder.PushClean(clean);
            }
            frame.Release();
            clean?.Release();
            _framesSent += count;
            _renderCount++;
        }
        catch (Exception ex)
        {
            AppLog.Write("Capture: " + ex.Message);
            _framesSent = due;
        }
    }

    private static Frame Render(Control? canvas, ref Avalonia.Media.Imaging.RenderTargetBitmap? rtb, int w, int h)
    {
        var frame = Frame.Rent(w * h * 4);
        if (canvas is null)
        {
            Array.Clear(frame.Data);
            return frame;
        }
        if (rtb is null || rtb.PixelSize.Width != w || rtb.PixelSize.Height != h)
        {
            rtb?.Dispose();
            rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h), new Vector(96.0 * w / 1920, 96.0 * h / 1080));
        }
        rtb.Render(canvas);
        unsafe
        {
            fixed (byte* p = frame.Data)
                rtb.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, frame.Data.Length, w * 4);
        }
        return frame;
    }

    // ---------------- Status for the Live Output app ----------------

    private void SendStatus()
    {
        try
        {
            _recorder.Watch();
            UpdateFrameTimer();
            var now = DateTime.UtcNow;
            using (var me = System.Diagnostics.Process.GetCurrentProcess())
            {
                var cpu = me.TotalProcessorTime;
                var span = (now - _lastCpuAt).TotalMilliseconds;
                if (span > 0) _cpu = (cpu - _lastCpu).TotalMilliseconds / span / Environment.ProcessorCount * 100;
                _lastCpu = cpu;
                _lastCpuAt = now;
            }
            var secs = (now - _renderCountAt).TotalSeconds;
            if (secs >= 1)
            {
                _renderFps = _renderCount / secs;
                _renderCount = 0;
                _renderCountAt = now;
            }
            if (now - _devicesAt > TimeSpan.FromSeconds(10))
            {
                _devicesAt = now;
                Task.Run(() =>
                {
                    _devices = CameraFeeds.Devices();
                    _audio = OperatingSystem.IsWindows() ? DirectShow.AudioDevices() : [];
                    _ndi = Ndi.Sources();
                });
            }
            var problem = _recorder.RecordProblem;
            var free = _recorder.DiskFreeGb;
            if (_recorder.Recording && free is > 0 and < 5) problem = $"Low disk space: {free:0.0} GB left on the recording drive.";
            var nex = _nexAudio.Describe();
            var status = new EngineStatus(
                Recording: _recorder.Recording,
                RecordingFile: _recorder.RecordingFile is { } f ? Path.GetFileName(f) : null,
                RecordingSeconds: _recorder.RecordingSeconds,
                RecordProblem: problem,
                Streaming: _recorder.Streaming,
                StreamState: _recorder.StreamState,
                StreamKbps: _recorder.StreamKbps,
                DroppedFrames: _recorder.Dropped,
                StreamProblem: _recorder.StreamProblem,
                Fps: _recorder.Active ? _renderFps : 0,
                CpuPercent: Math.Round(_cpu, 1),
                DiskFreeGb: Math.Round(free, 1),
                CameraDevices: _devices,
                AudioDevices: _audio,
                NdiSources: _ndi,
                NdiAvailable: Ndi.Available,
                VideoAvailable: VlcFeed.Vlc is not null,
                FfmpegAvailable: Recorder.FfmpegPath is not null,
                CameraProblem: Ndi.Available ? null : Ndi.Problem,
                NexAudioSound: nex.Sound,
                NexAudioFailsafe: nex.Failsafe,
                NexAudioArmed: nex.Armed,
                NexAudioHandedOver: nex.HandedOver);
            _ = _client?.SendAsync(new EngineStatusMessage(status));
        }
        catch (Exception ex)
        {
            AppLog.Write("Engine status: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        _stopWatch?.Stop();
        _statusTimer?.Stop();
        _frameTimer?.Stop();
        AppLog.Write("Output engine stopping on request");
        _recorder.Dispose();
        _nexAudio.Dispose();
        Power.KeepAwake(false);
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

    public OutputWindow(int display, bool broadcast = false)
    {
        Display = display;
        Broadcast = broadcast;
        View = new ScreenView { LiveVideo = true, IsOutput = true };
        if (broadcast)
        {
            // The camera-only picture for the clean recording sits under the main one.
            CleanView = new ScreenView { LiveVideo = true, IsOutput = true, Clean = true, Kind = ScreenKind.Broadcast, Animate = false };
            Content = new Grid { Children = { CleanView, View } };
        }
        else Content = View;
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
    /// <summary>Where on its display the window was put, to notice a change.</summary>
    public string Placement { get; private set; } = "";
    public static string PlacementOf(ScreenInfo s) => s.HasArea ? $"{s.AreaX},{s.AreaY},{s.AreaWidth},{s.AreaHeight}" : "full";
    public bool Broadcast { get; }
    public ScreenView View { get; }
    public ScreenView? CleanView { get; }
    public event Action? ClosedByUser;

    /// <summary>Shows the window on its display, full screen, or as a normal window for testing.</summary>
    public void ShowOn(ScreenInfo screen)
    {
        var displays = OrderedDisplays(Screens.All);
        Placement = PlacementOf(screen);
        if (screen.Display > 0 && screen.Display <= displays.Count && screen.HasArea)
        {
            // Only part of the display: the LED processor reads this area of the signal.
            var d = displays[screen.Display - 1];
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(d.Bounds.X + screen.AreaX, d.Bounds.Y + screen.AreaY);
            Width = screen.AreaWidth / d.Scaling;
            Height = screen.AreaHeight / d.Scaling;
            Topmost = true;
            CanResize = false;
            Cursor = new Cursor(StandardCursorType.None);
            Show();
            Position = new PixelPoint(d.Bounds.X + screen.AreaX, d.Bounds.Y + screen.AreaY);
        }
        else if (screen.Display > 0 && screen.Display <= displays.Count)
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
        else if (Broadcast)
        {
            // Not on any display: drawn off screen for the recording and the stream.
            SystemDecorations = SystemDecorations.None;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 480;
            Height = 270;
            Position = new PixelPoint(-20000, -20000);
            Show();
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
