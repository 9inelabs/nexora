using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// The Camera Switcher. Two ways of working:
/// <list type="bullet">
/// <item>Cameras on this PC (capture cards, webcams, NDI cameras): a multiview, Preview and Program, Cut, Auto
/// and a T-bar. The Program picture goes out over NDI as "NEXORA Program" and Live Output shows it.</item>
/// <item>Cameras on the Live Output PC (command only): this PC just tells Live Output which camera to show.</item>
/// </list>
/// Keys: 1–8 choose the next camera, Space cuts, Enter mixes (Auto).
/// </summary>
public sealed partial class CameraSectionViewModel : SectionBase, IKeyHandler
{
    public const string ProgramName = "NEXORA Program";
    private static string ConfigFile => Path.Combine(AppPaths.LocalDir, "cameras.json");

    private readonly DispatcherTimer _status;
    private DispatcherTimer? _auto;
    private DispatcherTimer? _ndiTimer;
    private Ndi.Sender? _sender;
    private RenderTargetBitmap? _rtb;
    private byte[]? _ndiBuffer;
    private DateTime _autoStart;

    public CameraSectionViewModel() : base(SectionKind.Camera)
    {
        LoadConfig();
        _status = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatus());
        _status.Start();
        RefreshDetected();
        OpenInputs();
        if (Inputs.Count > 0)
        {
            ProgramInput = Inputs[0];
            PreviewInput = Inputs.Count > 1 ? Inputs[1] : Inputs[0];
        }
    }

    public ObservableCollection<CameraInput> Inputs { get; } = [];
    public ObservableCollection<DetectedSource> Detected { get; } = [];
    public static IReadOnlyList<TransitionKind> Transitions { get; } = [TransitionKind.Fade, TransitionKind.Cut];

    [ObservableProperty] private bool _commandOnly;
    [ObservableProperty] private CameraInput? _programInput;
    [ObservableProperty] private CameraInput? _previewInput;
    [ObservableProperty] private VideoFeed? _programFeed;
    [ObservableProperty] private VideoFeed? _incomingFeed;
    [ObservableProperty] private double _mix;            // 0 = program only, 1 = incoming fully
    [ObservableProperty] private double _autoMs = 600;
    [ObservableProperty] private bool _sendingNdi;
    [ObservableProperty] private string _ndiStatus = "";
    [ObservableProperty] private bool _setupOpen;
    [ObservableProperty] private DetectedSource? _selectedDetected;
    [ObservableProperty] private string _newLabel = "";
    [ObservableProperty] private bool _onLiveOutput;

    public bool LocalMode => !CommandOnly;
    public bool HasInputs => Inputs.Count > 0;
    public bool NdiAvailable => Ndi.Available;
    public string NdiProblem => Ndi.Problem ?? "";
    public string ProgramLabel => ProgramInput?.Label ?? "No camera";
    public string PreviewLabel => PreviewInput?.Label ?? "No camera";

    /// <summary>The control the Program picture is drawn from (set by the view), for the NDI output.</summary>
    public Control? ProgramSurface { get; set; }

    // ---------- Inputs ----------

    private sealed record SavedInput(string Label, CameraSourceKind Kind, string Source);
    private sealed record SavedConfig(bool CommandOnly, List<SavedInput> Inputs, double AutoMs);

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile) && JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(ConfigFile), Wire.Options) is { } c)
            {
                CommandOnly = c.CommandOnly;
                AutoMs = c.AutoMs <= 0 ? 600 : c.AutoMs;
                var n = 1;
                foreach (var i in c.Inputs.Take(8)) Inputs.Add(new CameraInput(n++, i.Label, i.Kind, i.Source));
            }
        }
        catch (Exception ex) { AppLog.Write("Cameras: " + ex.Message); }
    }

    private void SaveConfig()
    {
        try
        {
            var c = new SavedConfig(CommandOnly, Inputs.Select(i => new SavedInput(i.Label, i.Kind, i.Source)).ToList(), AutoMs);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(c, Wire.Options));
        }
        catch (Exception ex) { AppLog.Write("Cameras: " + ex.Message); }
    }

    partial void OnCommandOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalMode));
        SaveConfig();
        CloseInputs();
        OpenInputs();
        RefreshDetected();
        if (value) StopNdi();
    }

    partial void OnAutoMsChanged(double value) => SaveConfig();

    [RelayCommand]
    private void UseLocal() => CommandOnly = false;

    [RelayCommand]
    private void UseCommandOnly() => CommandOnly = true;

    /// <summary>Cameras and NDI sources found on this PC (or on the Live Output PC in command-only mode).</summary>
    [RelayCommand]
    private void RefreshDetected()
    {
        Detected.Clear();
        if (CommandOnly)
        {
            var e = Live.Engine;
            foreach (var d in e.CameraDevices ?? []) Detected.Add(new DetectedSource(d, CameraSourceKind.Device, d, "Capture card on Live Output"));
            foreach (var d in e.NdiSources ?? []) Detected.Add(new DetectedSource(d, CameraSourceKind.Ndi, d, "NDI"));
        }
        else
        {
            foreach (var d in CameraFeeds.Devices()) Detected.Add(new DetectedSource(d, CameraSourceKind.Device, d, "Capture card on this PC"));
            foreach (var d in Ndi.Sources().Where(n => !n.Contains(ProgramName))) Detected.Add(new DetectedSource(d, CameraSourceKind.Ndi, d, "NDI"));
        }
        OnPropertyChanged(nameof(NdiAvailable));
        OnPropertyChanged(nameof(NdiProblem));
    }

    [RelayCommand]
    private void AddInput()
    {
        if (SelectedDetected is not { } d || Inputs.Count >= 8) return;
        var label = NewLabel.Trim().Length > 0 ? NewLabel.Trim() : $"Camera {Inputs.Count + 1}";
        var input = new CameraInput(Inputs.Count + 1, label, d.Kind, d.Source);
        Inputs.Add(input);
        Open(input);
        NewLabel = "";
        SaveConfig();
        OnPropertyChanged(nameof(HasInputs));
        ProgramInput ??= input;
        PreviewInput ??= input;
    }

    /// <summary>A video file played over and over as a camera, for practice without cameras.</summary>
    public void AddTestFile(string path)
    {
        if (Inputs.Count >= 8) return;
        var input = new CameraInput(Inputs.Count + 1, Path.GetFileNameWithoutExtension(path), CameraSourceKind.File, path);
        Inputs.Add(input);
        Open(input);
        SaveConfig();
        OnPropertyChanged(nameof(HasInputs));
        ProgramInput ??= input;
        PreviewInput ??= input;
    }

    [RelayCommand]
    private void RemoveInput(CameraInput input)
    {
        Close(input);
        Inputs.Remove(input);
        for (var i = 0; i < Inputs.Count; i++) Inputs[i].Number = i + 1;
        if (ProgramInput == input) ProgramInput = Inputs.FirstOrDefault();
        if (PreviewInput == input) PreviewInput = Inputs.FirstOrDefault();
        SaveConfig();
        OnPropertyChanged(nameof(HasInputs));
    }

    private void OpenInputs()
    {
        foreach (var i in Inputs) Open(i);
        UpdateProgramFeed();
    }

    private void Open(CameraInput i)
    {
        // In command-only mode the cameras are not on this PC: the tiles show names only.
        if (CommandOnly) { i.Feed = null; return; }
        i.Feed = CameraFeeds.Acquire(new CameraFeed(i.Label, i.Kind, i.Source));
    }

    private void Close(CameraInput i)
    {
        if (i.Feed is not null) CameraFeeds.Release(new CameraFeed(i.Label, i.Kind, i.Source));
        i.Feed = null;
    }

    private void CloseInputs()
    {
        foreach (var i in Inputs) Close(i);
        ProgramFeed = IncomingFeed = null;
    }

    private void RefreshStatus()
    {
        foreach (var i in Inputs)
        {
            i.HasSignal = CommandOnly || i.Feed?.HasSignal == true;
            i.Problem = CommandOnly ? "" : i.Feed?.Problem ?? (i.Feed?.HasSignal == true ? "" : "No signal");
        }
        if (CommandOnly && Detected.Count == 0) RefreshDetected();
        var src = Live.Snapshot.Sources.FirstOrDefault(s => s.Key == SourceKey);
        OnLiveOutput = src?.Current?.Camera is not null;
        if (SendingNdi) NdiStatus = $"Sending \"{ProgramName}\" over NDI from {Environment.MachineName}";
    }

    // ---------- Switching ----------

    partial void OnProgramInputChanged(CameraInput? value)
    {
        foreach (var i in Inputs) i.IsProgram = i == value;
        OnPropertyChanged(nameof(ProgramLabel));
        UpdateProgramFeed();
    }

    partial void OnPreviewInputChanged(CameraInput? value)
    {
        foreach (var i in Inputs) i.IsPreview = i == value;
        OnPropertyChanged(nameof(PreviewLabel));
        IncomingFeed = value?.Feed;
    }

    private void UpdateProgramFeed()
    {
        ProgramFeed = ProgramInput?.Feed;
        IncomingFeed = PreviewInput?.Feed;
    }

    [RelayCommand]
    private void PickPreview(CameraInput input) => PreviewInput = input;

    /// <summary>Straight to Program (a click on a multiview tile with Ctrl, or the number key twice).</summary>
    [RelayCommand]
    private async Task Take(CameraInput input)
    {
        PreviewInput = input;
        await Cut();
    }

    [RelayCommand]
    private async Task Cut()
    {
        if (PreviewInput is not { } next) return;
        StopAuto();
        var old = ProgramInput;
        ProgramInput = next;
        PreviewInput = old ?? next;
        Mix = 0;
        await SendCommandIfNeeded(TransitionKind.Cut, 0);
    }

    [RelayCommand]
    private async Task Auto()
    {
        if (PreviewInput is not { } next || next == ProgramInput) { await Cut(); return; }
        if (CommandOnly)
        {
            var old = ProgramInput;
            ProgramInput = next;
            PreviewInput = old ?? next;
            await SendCommandIfNeeded(TransitionKind.Fade, (int)AutoMs);
            return;
        }
        StopAuto();
        _autoStart = DateTime.UtcNow;
        _auto = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = (DateTime.UtcNow - _autoStart).TotalMilliseconds / Math.Max(50, AutoMs);
            if (t >= 1) { FinishMix(); return; }
            Mix = t;
        });
        _auto.Start();
    }

    private void StopAuto()
    {
        _auto?.Stop();
        _auto = null;
    }

    private void FinishMix()
    {
        StopAuto();
        var old = ProgramInput;
        ProgramInput = PreviewInput;
        PreviewInput = old;
        Mix = 0;
    }

    /// <summary>The T-bar: dragging all the way across completes the mix.</summary>
    partial void OnMixChanged(double value)
    {
        if (_auto is null && value >= 0.999) FinishMix();
    }

    /// <summary>In command-only mode every switch goes to Live Output. With NDI, Live Output shows the Program stream.</summary>
    private async Task SendCommandIfNeeded(TransitionKind kind, int ms)
    {
        if (CommandOnly)
        {
            if (ProgramInput is { } p)
                await SendAsync(new SlideContent(p.Label, p.Source, p.Label, Camera: new CameraFeed(p.Label, p.Kind, p.Source, kind, ms)));
        }
        else if (OnLiveOutput)
        {
            // The Program stream already carries the change; keep the label current for Live Output.
            await SendProgramAsync();
        }
    }

    // ---------- Program out ----------

    private static string ProgramSourceName => $"{Environment.MachineName.ToUpperInvariant()} ({ProgramName})";

    [RelayCommand]
    private async Task GoLive()
    {
        if (CommandOnly)
        {
            await SendCommandIfNeeded(TransitionKind.Cut, 0);
            return;
        }
        StartNdi();
        if (!SendingNdi) return;
        await SendProgramAsync();
    }

    private Task SendProgramAsync() =>
        SendAsync(new SlideContent(ProgramLabel, ProgramSourceName, "Cameras", Camera: new CameraFeed("Cameras · " + ProgramLabel, CameraSourceKind.Ndi, ProgramSourceName)));

    [RelayCommand]
    private async Task TakeOff()
    {
        await ClearAsync();
        StopNdi();
    }

    private void StartNdi()
    {
        if (SendingNdi) return;
        if (!Ndi.Available)
        {
            Live.RaiseToast(Ndi.Problem ?? "NDI is not available on this PC.", true);
            return;
        }
        _sender = new Ndi.Sender(ProgramName);
        if (!_sender.Ok)
        {
            Live.RaiseToast("The NDI output could not start.", true);
            _sender.Dispose();
            _sender = null;
            return;
        }
        SendingNdi = true;
        _ndiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 30), DispatcherPriority.Render, (_, _) => SendFrame());
        _ndiTimer.Start();
    }

    private void StopNdi()
    {
        _ndiTimer?.Stop();
        _ndiTimer = null;
        _sender?.Dispose();
        _sender = null;
        SendingNdi = false;
        NdiStatus = "";
    }

    private unsafe void SendFrame()
    {
        if (_sender is null || ProgramSurface is not { } surface) return;
        try
        {
            const int w = 1920, h = 1080;
            _rtb ??= new RenderTargetBitmap(new PixelSize(w, h), new Vector(96.0 * w / ScreenView.W, 96.0 * h / ScreenView.H));
            _rtb.Render(surface);
            _ndiBuffer ??= new byte[w * h * 4];
            fixed (byte* p = _ndiBuffer)
            {
                _rtb.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, _ndiBuffer.Length, w * 4);
                _sender.Send((IntPtr)p, w, h, w * 4);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("NDI send: " + ex.Message);
        }
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (key is >= Key.D1 and <= Key.D8 or >= Key.NumPad1 and <= Key.NumPad8)
        {
            var n = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
            if (n < Inputs.Count)
            {
                if (mods.HasFlag(KeyModifiers.Shift)) _ = Take(Inputs[n]);
                else PreviewInput = Inputs[n];
            }
            return true;
        }
        switch (key)
        {
            case Key.Space: _ = Cut(); return true;
            case Key.Enter: _ = Auto(); return true;
        }
        return false;
    }

    protected override void OnLive(string? property)
    {
        if (property == nameof(Live.Snapshot) && CommandOnly && Detected.Count == 0) RefreshDetected();
    }

    public override void OnLeave()
    {
        base.OnLeave();
        _status.Stop();
        StopAuto();
        StopNdi();
        CloseInputs();
        _rtb?.Dispose();
    }
}

public sealed partial class CameraInput(int number, string label, CameraSourceKind kind, string source) : ObservableObject
{
    [ObservableProperty] private int _number = number;
    public string Label => label;
    public CameraSourceKind Kind => kind;
    public string Source => source;
    public string KindText => kind switch { CameraSourceKind.Ndi => "NDI", CameraSourceKind.Device => "Capture", _ => "Test file" };
    [ObservableProperty] private VideoFeed? _feed;
    [ObservableProperty] private bool _isProgram;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private bool _hasSignal;
    [ObservableProperty] private string _problem = "";
    public bool HasProblem => Problem.Length > 0;
    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));
}

public sealed record DetectedSource(string Name, CameraSourceKind Kind, string Source, string Where)
{
    public override string ToString() => $"{Name}  ·  {Where}";
}
