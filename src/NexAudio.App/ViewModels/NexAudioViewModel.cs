using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.Audio.Checks;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.X32;
using Nexora.Core.Config;

namespace NexAudio.App.ViewModels;

public enum NexAudioTab
{
    Mixer,
    Channel,
    Outputs,
    Routing,
    Groups,
    Stream,
    Scenes,
    Show,
    Soundcheck,
    Plugins,
    Failsafe,
    Meters,
    Patch,
    Latency,
    Driver,
    Console,
    PcCheck,
    Resources,
    Diagnostics,
    Bridge,
    Remote,
}

/// <summary>
/// NEX-Audio, Phase 1: run the X32's 32 channels through NEXORA and back, watch every level,
/// choose what each output plays, measure the real round-trip delay, link to the X32 over the
/// network and check this PC is fit for the job. The sound runs in its own engine process;
/// this screen only shows it and sends it instructions.
/// </summary>
public sealed partial class NexAudioViewModel : ViewModelBase
{
    private readonly EngineClient _engine;
    private readonly X32Client _x32 = new();
    private readonly DispatcherTimer _clock;
    private bool _applyingPatch;
    private bool _patchBuilt;
    private int _lastIn = -1, _lastOut = -1;

    public NexAudioViewModel()
    {
        // A laptop on the admin remote runs the sound PC's desk; any other PC runs its own engine.
        _engine = new EngineClient(StartTarget());
        if (!_engine.Target.IsRemote) S.Audio.Start();
        _engine.Status += s => Dispatcher.UIThread.Post(() => Apply(s));
        _engine.Meters += m => Dispatcher.UIThread.Post(() => ApplyMeters(m));
        _engine.Notice += n => Dispatcher.UIThread.Post(() => Notice = n.Text);
        _engine.ConnectionChanged += c => Dispatcher.UIThread.Post(() =>
        {
            EngineLinked = c;
            RaiseRemoteDesk();
            if (!c)
            {
                // On a laptop, nothing shown should look live while the link to the sound PC is down.
                if (IsRemoteDesk)
                {
                    Status = EngineStatus.Empty;
                    RaiseStatus();
                }
                InputMeters.Silence();
                OutputMeters.Silence();
            }
        });
        _x32.Changed += () => Dispatcher.UIThread.Post(ApplyConsole);
        HookDesk();
        HookShow();
        HookLaptops();
        WatchForUpdates();
        Live.PropertyChanged += OnAccountChanged;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Start();

        LoadPrefs();
        X32Address = LoadLocal().X32Address ?? "";
        // On a laptop the X32 address comes from the sound PC once it answers (see LinkRemoteX32).
        if (X32Address.Length > 0 && !IsRemoteDesk) _ = ConnectConsole();
        _ = RunPcCheck();
    }

    // ---- Tabs ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnMeters), nameof(OnPatch), nameof(OnLatency), nameof(OnDriver), nameof(OnConsole), nameof(OnPcCheck), nameof(OnResources),
        nameof(OnMixer), nameof(OnChannel), nameof(OnOutputs), nameof(OnRouting), nameof(OnGroups), nameof(OnStream), nameof(OnScenes), nameof(OnShow), nameof(OnSoundcheck), nameof(OnPlugins), nameof(OnFailsafe), nameof(OnSetup), nameof(OnBridge), nameof(OnRemote), nameof(OnDiagnostics))]
    private NexAudioTab _tab = NexAudioTab.Mixer;

    public bool OnMixer => Tab == NexAudioTab.Mixer;
    public bool OnChannel => Tab == NexAudioTab.Channel;
    public bool OnOutputs => Tab == NexAudioTab.Outputs;
    public bool OnStream => Tab == NexAudioTab.Stream;
    public bool OnGroups => Tab == NexAudioTab.Groups;
    public bool OnScenes => Tab == NexAudioTab.Scenes;
    public bool OnShow => Tab == NexAudioTab.Show;
    public bool OnSoundcheck => Tab == NexAudioTab.Soundcheck;
    public bool OnPlugins => Tab == NexAudioTab.Plugins;
    public bool OnFailsafe => Tab == NexAudioTab.Failsafe;
    public bool OnRemote => Tab == NexAudioTab.Remote;
    /// <summary>The Phase 1 pages (meters, latency, driver, X32 link, PC check) sit together under Setup.</summary>
    public bool OnSetup => Tab is NexAudioTab.Meters or NexAudioTab.Patch or NexAudioTab.Latency or NexAudioTab.Driver or NexAudioTab.Console or NexAudioTab.PcCheck or NexAudioTab.Resources or NexAudioTab.Diagnostics;

    public bool OnMeters => Tab == NexAudioTab.Meters;
    public bool OnPatch => Tab == NexAudioTab.Patch;
    public bool OnLatency => Tab == NexAudioTab.Latency;
    public bool OnDriver => Tab == NexAudioTab.Driver;
    public bool OnConsole => Tab == NexAudioTab.Console;
    public bool OnPcCheck => Tab == NexAudioTab.PcCheck;
    public bool OnResources => Tab == NexAudioTab.Resources;

    [RelayCommand]
    private void ShowTab(string tab) => Tab = Enum.Parse<NexAudioTab>(tab);

    /// <summary>Trainees can look at everything but cannot change the sound.</summary>
    public bool CanControl => Live.User?.IsTrainee != true;

    // ---- Engine status ---------------------------------------------------------------------

    [ObservableProperty] private bool _engineLinked;
    [ObservableProperty] private EngineStatus _status = EngineStatus.Empty;
    [ObservableProperty] private string _notice = "";

    public bool IsRunning => Status.State == EngineState.Running;
    public bool IsStopped => Status.State == EngineState.Stopped;
    public bool HasProblem => Status.State == EngineState.Problem;
    public bool IsStarting => Status.State == EngineState.Starting;
    public bool SoundOn => Status.State is EngineState.Running or EngineState.Starting or EngineState.Problem;

    public string StateText => !EngineLinked ? (IsRemoteDesk ? "Not linked" : "Starting the sound engine…") : Status.State switch
    {
        EngineState.Running => "Running",
        EngineState.Starting => "Starting",
        EngineState.Problem => "Problem",
        _ => "Stopped",
    };

    public string MessageText => !EngineLinked
        ? IsRemoteDesk ? RemoteDeskText : "The sound engine runs on its own, so the sound carries on even if this screen closes."
        : Status.Message;
    public string RateText => Status.SampleRate > 0 ? $"{Status.SampleRate / 1000:0.#} kHz" : "—";
    public string BufferText => Status.BufferSize > 0 ? $"{Status.BufferSize} samples · {Status.BufferMs:0.0} ms" : "—";
    public string ChannelsText => Status.Inputs > 0 ? $"{Status.Inputs} in · {Status.Outputs} out" : "—";

    public string RoundTripText
    {
        get
        {
            if (Status.Latency?.Milliseconds is { } ms) return $"{ms:0.0} ms";
            return Status.SampleRate > 0 ? $"{Status.DriverRoundTripMs:0.0} ms" : "—";
        }
    }

    public string RoundTripNote => Status.Latency?.Milliseconds is not null ? "measured" : Status.SampleRate > 0 ? "driver's figure" : "";
    public string LoadText => IsRunning ? $"{Status.LoadAverage * 100:0}%" : "—";
    public string LoadNote => IsRunning ? $"peak {Status.LoadPeak * 100:0}%" : "";
    public string ClicksText => IsRunning || HasProblem ? (Status.LateBuffers + Status.DriverProblems).ToString() : "—";
    public string ClicksNote => Status.LateBuffers + Status.DriverProblems == 0 ? "none since start" : "late buffers or driver resyncs";
    public bool ClicksBad => Status.LateBuffers + Status.DriverProblems > 0;
    public bool LoadHigh => Status.LoadAverage > 0.6;

    public string UptimeText
    {
        get
        {
            if (Status.RunningSinceUtc == DateTime.MinValue || !IsRunning) return "";
            var t = DateTime.UtcNow - Status.RunningSinceUtc;
            var span = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : "under a minute";
            var restarts = Status.Restarts == 0 ? "" : $" · reopened {Status.Restarts}×";
            return $"Running for {span}{restarts}";
        }
    }

    partial void OnEngineLinkedChanged(bool value) => RaiseStatus();

    private void Apply(EngineStatus s)
    {
        var shapeChanged = s.Inputs != _lastIn || s.Outputs != _lastOut;
        Status = s;
        if (shapeChanged)
        {
            _lastIn = s.Inputs;
            _lastOut = s.Outputs;
            InputMeters.Resize(Math.Max(s.Inputs, 0));
            OutputMeters.Resize(Math.Max(s.Outputs, 0));
            RefreshLabels();
            BuildPatch();
            BuildLatencyChoices();
            if (_mixReceived) BuildOutputs();
        }
        else if (s.Patch.Count == PatchRows.Count) SyncPatch(s.Patch);
        else if (!_patchBuilt) BuildPatch();

        SyncDrivers(s);
        SyncRates(s);
        if (s.State == EngineState.Stopped)
        {
            InputMeters.Silence();
            OutputMeters.Silence();
        }
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        foreach (var p in new[]
                 {
                     nameof(IsRunning), nameof(IsStopped), nameof(HasProblem), nameof(IsStarting), nameof(SoundOn), nameof(StateText), nameof(MessageText),
                     nameof(RateText), nameof(BufferText), nameof(ChannelsText), nameof(RoundTripText), nameof(RoundTripNote), nameof(LoadText),
                     nameof(LoadNote), nameof(ClicksText), nameof(ClicksNote), nameof(ClicksBad), nameof(LoadHigh), nameof(UptimeText),
                     nameof(LatencyRunning), nameof(NoLatencyYet), nameof(LatencyResultText), nameof(LatencyDetailText), nameof(HasLatencyResult), nameof(LatencyFailed),
                     nameof(DriverLatencyText), nameof(HasDriverPanel),
                 })
            OnPropertyChanged(p);
        if (OnResources) RefreshHeadroom();
        BuildDiagnostics();
    }

    private void Tick()
    {
        OnPropertyChanged(nameof(UptimeText));
        WatchNexora();
    }

    // ---- Meters -------------------------------------------------------------------------

    public MeterSet InputMeters { get; } = new(32);
    public MeterSet OutputMeters { get; } = new(32);

    private void ApplyMeters(MetersMsg m)
    {
        InputMeters.Push(m.In, m.InClip);
        OutputMeters.Push(m.Out, m.OutClip);
    }

    [RelayCommand]
    private Task ClearClips() => _engine.SendAsync(new ResetClipsMsg());

    private void RefreshLabels()
    {
        var channels = _x32.Info is not null ? _x32.Channels : [];
        for (var i = 0; i < InputMeters.Count; i++)
        {
            var c = i < channels.Count ? channels[i] : null;
            InputMeters.SetLabel(i, c?.Label ?? $"In {i + 1}", c is null || c.Colour == 0 ? null : new SolidColorBrush(Color.Parse(c.ColourHex)));
        }
        for (var o = 0; o < OutputMeters.Count; o++)
            OutputMeters.SetLabel(o, $"Out {o + 1}", null);
        InputMeters.Push(new float[InputMeters.Count], new bool[InputMeters.Count]);
    }

    // ---- Patch: what each output plays ------------------------------------------------------

    public ObservableCollection<PatchChoice> PatchChoices { get; } = [];
    public ObservableCollection<PatchRow> PatchRows { get; } = [];

    private void BuildPatch()
    {
        _applyingPatch = true;
        PatchChoices.Clear();
        PatchChoices.Add(new PatchChoice(EngineCore.Off, "Off (silent)"));
        PatchChoices.Add(new PatchChoice(EngineCore.Tone, "Test tone, 1 kHz at -18 dB"));
        var channels = _x32.Info is not null ? _x32.Channels : [];
        for (var i = 0; i < Status.Inputs; i++)
        {
            var name = i < channels.Count && !string.IsNullOrWhiteSpace(channels[i].Name) ? $" · {channels[i].Name}" : "";
            PatchChoices.Add(new PatchChoice(i, $"Input {i + 1}{name}"));
        }
        PatchRows.Clear();
        for (var o = 0; o < Status.Outputs; o++)
        {
            var row = new PatchRow(o, this);
            var value = o < Status.Patch.Count ? Status.Patch[o] : EngineCore.Off;
            row.Selected = PatchChoices.FirstOrDefault(c => c.Value == value) ?? PatchChoices[0];
            PatchRows.Add(row);
        }
        _patchBuilt = Status.Outputs > 0;
        _applyingPatch = false;
        OnPropertyChanged(nameof(PatchSummary));
    }

    private void SyncPatch(IReadOnlyList<int> patch)
    {
        _applyingPatch = true;
        for (var o = 0; o < PatchRows.Count; o++)
            if (PatchRows[o].Selected?.Value != patch[o])
                PatchRows[o].Selected = PatchChoices.FirstOrDefault(c => c.Value == patch[o]) ?? PatchChoices[0];
        _applyingPatch = false;
        OnPropertyChanged(nameof(PatchSummary));
    }

    internal void PatchEdited()
    {
        if (_applyingPatch) return;
        if (!CanControl)
        {
            SyncPatch(Status.Patch);
            return;
        }
        _ = _engine.SendAsync(new SetPatchMsg(PatchRows.Select(r => r.Selected?.Value ?? EngineCore.Off).ToArray()));
        OnPropertyChanged(nameof(PatchSummary));
    }

    public string PatchSummary
    {
        get
        {
            if (PatchRows.Count == 0) return "Start the sound to see the outputs.";
            var straight = PatchRows.Count(r => r.Selected?.Value == r.Output);
            var off = PatchRows.Count(r => r.Selected?.Value == EngineCore.Off);
            var tone = PatchRows.Count(r => r.Selected?.Value == EngineCore.Tone);
            if (straight == PatchRows.Count) return "Every output plays the input with the same number: the X32's sound passes straight through NEX-Audio.";
            var parts = new List<string> { $"{straight} straight through" };
            if (PatchRows.Count - straight - off - tone > 0) parts.Add($"{PatchRows.Count - straight - off - tone} moved");
            if (off > 0) parts.Add($"{off} off");
            if (tone > 0) parts.Add($"{tone} on test tone");
            return string.Join(" · ", parts);
        }
    }

    [RelayCommand]
    private void PatchStraight() => SetAll(o => o < Status.Inputs ? o : EngineCore.Off);

    [RelayCommand]
    private void PatchAllOff() => SetAll(_ => EngineCore.Off);

    private void SetAll(Func<int, int> value)
    {
        if (!CanControl || PatchRows.Count == 0) return;
        var patch = Enumerable.Range(0, PatchRows.Count).Select(value).ToArray();
        SyncPatch(patch);
        _ = _engine.SendAsync(new SetPatchMsg(patch));
    }

    // ---- Latency test -------------------------------------------------------------------------

    public ObservableCollection<int> LatencyOutputs { get; } = [];
    public ObservableCollection<int> LatencyInputs { get; } = [];
    [ObservableProperty] private int _latencyOutput = 16;
    [ObservableProperty] private int _latencyInput = 32;

    private void BuildLatencyChoices()
    {
        LatencyOutputs.Clear();
        LatencyInputs.Clear();
        for (var o = 1; o <= Status.Outputs; o++) LatencyOutputs.Add(o);
        for (var i = 1; i <= Status.Inputs; i++) LatencyInputs.Add(i);
        if (!LatencyOutputs.Contains(LatencyOutput) && LatencyOutputs.Count > 0) LatencyOutput = Math.Min(16, LatencyOutputs.Count);
        if (!LatencyInputs.Contains(LatencyInput) && LatencyInputs.Count > 0) LatencyInput = LatencyInputs.Count;
        OnPropertyChanged(nameof(LatencyOutput));
        OnPropertyChanged(nameof(LatencyInput));
    }

    public bool LatencyRunning => Status.LatencyRunning;
    public bool NoLatencyYet => Status.Latency is null && !LatencyRunning;
    public bool HasLatencyResult => Status.Latency?.Milliseconds is not null && !LatencyRunning;
    public bool LatencyFailed => Status.Latency is { MedianSamples: null } && !LatencyRunning;
    public string LatencyResultText => Status.Latency?.Milliseconds is { } ms ? $"{ms:0.0} ms" : "";

    public string LatencyDetailText
    {
        get
        {
            if (Status.Latency is not { } r) return "";
            if (r.MedianSamples is null)
                return $"No click came back on input {r.Input + 1}. Check the cable runs from X32 output {r.Output + 1} into input {r.Input + 1}, " +
                       $"that output {r.Output + 1} plays card return {r.Output + 1}, and that input {r.Input + 1}'s gain is not at the very bottom.";
            var verdict = r.Milliseconds switch
            {
                <= 6 => "Excellent: fine for wedges and in-ears.",
                <= 10 => "Good: fine for the stream, mains and side fills. Check with singers before moving in-ears.",
                _ => "High for live sound. Try a smaller buffer in the Driver tab, then test again.",
            };
            return $"Out {r.Output + 1} to In {r.Input + 1} · {r.MedianSamples} samples at {r.SampleRate / 1000:0.#} kHz · {r.Heard} of {r.Rounds.Count} clicks heard. {verdict}";
        }
    }

    public string DriverLatencyText => Status.SampleRate > 0
        ? $"The driver says {Status.DriverRoundTripMs:0.0} ms ({Status.InputLatency} in + {Status.OutputLatency} out samples). The test measures the real figure, converters included."
        : "";

    [RelayCommand]
    private Task RunLatencyTest()
    {
        if (!CanControl || !IsRunning) return Task.CompletedTask;
        return _engine.SendAsync(new LatencyTestMsg(LatencyOutput - 1, LatencyInput - 1));
    }

    // ---- Driver ----------------------------------------------------------------------------

    public ObservableCollection<string> Drivers { get; } = [];
    public ObservableCollection<int> BufferSizes { get; } = [];
    [ObservableProperty] private string? _selectedDriver;
    [ObservableProperty] private int _selectedBuffer = 64;

    public bool HasDriverPanel => Status.HasSettingsPanel;
    public bool NoX32Driver => Drivers.Count > 0 && Drivers.All(d => d == SimulatedDevice.DeviceName);

    private void SyncDrivers(EngineStatus s)
    {
        if (!s.Drivers.SequenceEqual(Drivers))
        {
            Drivers.Clear();
            foreach (var d in s.Drivers) Drivers.Add(d);
            OnPropertyChanged(nameof(NoX32Driver));
            OnPropertyChanged(nameof(SelectedDriver));
        }
        SelectedDriver ??= s.Driver ?? Drivers.FirstOrDefault();
        var sizes = s.BufferSizes.Count > 0 ? s.BufferSizes : [32, 64, 128, 256, 512];
        if (!sizes.SequenceEqual(BufferSizes))
        {
            BufferSizes.Clear();
            foreach (var b in sizes) BufferSizes.Add(b);
            OnPropertyChanged(nameof(SelectedBuffer)); // the list was rebuilt: show the choice again
        }
        // Follow the driver's buffer whenever it changes (for example in the driver's own settings).
        if (s.BufferSize > 0 && s.BufferSize != _lastDriverBuffer)
        {
            _lastDriverBuffer = s.BufferSize;
            SelectedBuffer = s.BufferSize;
        }
    }

    private int _lastDriverBuffer;

    [RelayCommand]
    private Task StartSound()
    {
        if (!CanControl || SelectedDriver is not { } d) return Task.CompletedTask;
        Notice = "";
        return _engine.SendAsync(new OpenDriverMsg(d, SelectedBuffer, double.Parse(SelectedRate?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // ---- Sample rate -----------------------------------------------------------------------------

    public ObservableCollection<Choice> RateChoices { get; } = [new("0", "Driver's own")];
    [ObservableProperty] private Choice? _selectedRate;
    private string _ratesKey = "";

    public string RateNote => Status.SampleRates.Count == 0
        ? "Start the sound once to see which rates the driver can run at."
        : Status.SampleRates.Any(r => r > 50000)
            ? "96 kHz halves the delay of each buffer and costs about twice the processing. The stream still goes out at 48 kHz."
            : "This driver only runs at 44.1 or 48 kHz (the X32's USB card is one of these). Use 48 kHz.";

    private void SyncRates(EngineStatus s)
    {
        var key = string.Join(",", s.SampleRates);
        if (key != _ratesKey)
        {
            _ratesKey = key;
            RateChoices.Clear();
            RateChoices.Add(new Choice("0", "Driver's own"));
            foreach (var r in s.SampleRates)
                RateChoices.Add(new Choice(r.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{r / 1000:0.#} kHz"));
            SelectedRate = null;
        }
        SelectedRate ??= RateChoices.FirstOrDefault(c => c.Value == s.WantedRate.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? RateChoices[0];
        OnPropertyChanged(nameof(RateNote));
    }

    [RelayCommand]
    private Task StopSound() => CanControl ? _engine.SendAsync(new CloseDriverMsg()) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenDriverPanel()
    {
        if (IsRemoteDesk) Notice = $"The driver's own settings window opens on {RemoteDeskName}'s screen.";
        return _engine.SendAsync(new DriverPanelMsg());
    }

    [RelayCommand]
    private Task RefreshDrivers() => _engine.SendAsync(new RefreshDriversMsg());

    [RelayCommand]
    private async Task ShutDownEngine()
    {
        if (!CanControl) return;
        if (IsRemoteDesk)
        {
            Notice = "The sound engine can only be shut down at the sound PC itself.";
            return;
        }
        S.Audio.Forget();
        await _engine.SendAsync(new QuitMsg());
    }

    [RelayCommand]
    private void StartEngine()
    {
        if (!IsRemoteDesk) S.Audio.Start();
    }

    // ---- The X32 over the network ----------------------------------------------------------------

    [ObservableProperty] private string _x32Address = "";
    [ObservableProperty] private bool _x32Busy;
    [ObservableProperty] private string _x32Message = "Type the X32's network address (on the desk: Setup > Network), or press Find.";
    public ObservableCollection<X32Info> FoundConsoles { get; } = [];
    public ObservableCollection<ConsoleChannel> ConsoleChannels { get; } = [];

    public bool X32Connected => _x32.Connected;
    public string X32Title => _x32.Info is { } i ? $"{(i.Name.Length > 0 ? i.Name : i.Model)}" : "Not linked";
    public string X32Detail => _x32.Info is { } i ? $"{i.Model} · firmware {i.Firmware} · {_x32.Address}" : "";

    [RelayCommand]
    private async Task FindConsoles()
    {
        X32Busy = true;
        X32Message = "Looking for X32s on the church network…";
        FoundConsoles.Clear();
        var found = await X32Client.DiscoverAsync();
        foreach (var f in found) FoundConsoles.Add(f);
        X32Message = found.Count switch
        {
            0 => "No X32 answered. Check its network cable goes to the church switch, then type its address instead.",
            1 => "Found one X32. Press Link to use it.",
            _ => $"Found {found.Count} consoles. Pick yours.",
        };
        if (found.Count == 1) X32Address = found[0].Address;
        X32Busy = false;
    }

    [RelayCommand]
    private void UseFound(X32Info info)
    {
        X32Address = info.Address;
        _ = ConnectConsole();
    }

    [RelayCommand]
    private async Task ConnectConsole()
    {
        if (string.IsNullOrWhiteSpace(X32Address)) return;
        X32Busy = true;
        X32Message = $"Linking to {X32Address}…";
        X32Info? info = null;
        try { info = await _x32.ConnectAsync(X32Address); }
        catch (Exception ex) { X32Message = "Could not link: " + ex.Message; }
        if (info is not null)
        {
            X32Message = "Linked. Channel names come straight from the desk and update when they change there.";
            // Only tell the engine when the address is new to it, so opening a screen never blips its X32 link.
            if (CanMix && !string.Equals(X32Address.Trim(), DeskStatus.Settings.X32Host, StringComparison.OrdinalIgnoreCase))
                _ = Command("x32.connect", new() { ["host"] = X32Address.Trim() });
            SaveLocal(LoadLocal() with { X32Address = X32Address.Trim() });
        }
        else if (!X32Message.StartsWith("Could not"))
            X32Message = $"No X32 answered at {X32Address}. Check the address on the desk (Setup > Network) and that both are on the same network.";
        X32Busy = false;
        ApplyConsole();
    }

    [RelayCommand]
    private void DisconnectConsole()
    {
        _x32.Disconnect();
        X32Message = "Not linked. NEX-Audio still runs the sound; only the channel names are missing.";
        ApplyConsole();
    }

    private void ApplyConsole()
    {
        OnPropertyChanged(nameof(X32Connected));
        OnPropertyChanged(nameof(X32Title));
        OnPropertyChanged(nameof(X32Detail));
        var channels = _x32.Info is not null ? _x32.Channels : [];
        RememberNames(channels);
        ConsoleChannels.Clear();
        foreach (var c in channels) ConsoleChannels.Add(new ConsoleChannel(c.Number, c.Label, new SolidColorBrush(Color.Parse(c.ColourHex)), c.Name.Length > 0));
        RefreshLabels();
        if (_patchBuilt) RelabelPatch(channels);
        QueueRebuild();
    }

    private void RelabelPatch(IReadOnlyList<X32Channel> channels)
    {
        foreach (var choice in PatchChoices)
            if (choice.Value >= 0)
            {
                var name = choice.Value < channels.Count && !string.IsNullOrWhiteSpace(channels[choice.Value].Name) ? $" · {channels[choice.Value].Name}" : "";
                choice.Label = $"Input {choice.Value + 1}{name}";
            }
    }

    // ---- PC check ------------------------------------------------------------------------------

    public ObservableCollection<CheckRow> Checks { get; } = [];
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private string _checkSummary = "Checking this PC…";

    [RelayCommand]
    private async Task RunPcCheck()
    {
        Checking = true;
        var items = await Task.Run(() => PcCheck.Run(S.IsLiveOutputPc, AppPaths.Root));
        Checks.Clear();
        foreach (var i in items) Checks.Add(new CheckRow(i));
        var fails = items.Count(i => i.Level == CheckLevel.Fail);
        var warns = items.Count(i => i.Level == CheckLevel.Warn);
        CheckSummary = fails > 0 ? $"{fails} must be fixed before a service, {warns} worth improving."
            : warns > 0 ? $"Ready to test. {warns} worth improving."
            : "This PC is ready for NEX-Audio.";
        Checking = false;
    }

    // ---- Settings only this PC needs ----------------------------------------------------------------

    /// <summary>The X32's address, and its channel names as last seen, so names still show offline.</summary>
    /// <summary>The X32's address and names, and (on a laptop) the sound PC it runs on the admin remote.</summary>
    private sealed record LocalSettings(string? X32Address, string[]? X32Names = null, string? DeskHost = null, int? DeskPort = null, string? DeskPin = null);

    private string[] _cachedNames = LoadLocal().X32Names ?? [];

    private void RememberNames(IReadOnlyList<X32Channel> channels)
    {
        if (channels.Count == 0 || channels.All(c => c.Name.Length == 0)) return;
        var names = channels.Select(c => c.Name).ToArray();
        if (names.SequenceEqual(_cachedNames)) return;
        _cachedNames = names;
        SaveLocal(LoadLocal() with { X32Names = names });
    }

    /// <summary>The X32's name for a channel as last seen, when the X32 is not linked now (offline, or before it answers).</summary>
    private string? CachedName(int number) => number >= 1 && number <= _cachedNames.Length && _cachedNames[number - 1].Length > 0 ? _cachedNames[number - 1] : null;

    private static string LocalFile => Path.Combine(AppPaths.LocalDir, "nex-audio.json");

    private static LocalSettings LoadLocal()
    {
        try
        {
            if (File.Exists(LocalFile)) return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(LocalFile)) ?? new LocalSettings((string?)null);
        }
        catch (Exception) { }
        return new LocalSettings((string?)null);
    }

    private static void SaveLocal(LocalSettings s)
    {
        try { File.WriteAllText(LocalFile, JsonSerializer.Serialize(s)); }
        catch (Exception ex) { AppLog.Write("NEX-Audio settings: " + ex.Message); }
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnAccountChanged;
        _clock.Stop();
        _flush?.Stop();
        _engine.Dispose();
        _x32.Dispose();
    }
}

public sealed partial class PatchChoice(int value, string label) : ObservableObject
{
    public int Value { get; } = value;
    [ObservableProperty] private string _label = label;
    public override string ToString() => Label;
}

public sealed partial class PatchRow(int output, NexAudioViewModel owner) : ObservableObject
{
    public int Output { get; } = output;
    public string Number => (Output + 1).ToString();
    public string Name => $"Card return {Output + 1}";
    [ObservableProperty] private PatchChoice? _selected;

    partial void OnSelectedChanged(PatchChoice? value) => owner.PatchEdited();
}

public sealed record ConsoleChannel(int Number, string Label, IBrush Colour, bool Named);

public sealed class CheckRow(CheckItem item)
{
    public string Title => item.Title;
    public string Found => item.Found;
    public string Advice => item.Advice;
    public bool Pass => item.Level == CheckLevel.Pass;
    public bool Warn => item.Level == CheckLevel.Warn;
    public bool Fail => item.Level == CheckLevel.Fail;
    public string LevelText => item.Level switch { CheckLevel.Pass => "OK", CheckLevel.Warn => "Improve", _ => "Fix" };
}
