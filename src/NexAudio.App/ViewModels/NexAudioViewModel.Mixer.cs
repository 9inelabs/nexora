using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Core.Model;

namespace NexAudio.App.ViewModels;

/// <summary>
/// NEX-Audio, Phases 2 to 5: the mixer, a channel's processing, the outputs, the stream mix, scenes,
/// plugins and the failsafe. The mix itself lives in the sound engine; this keeps a copy to show,
/// and sends each change back as a path and a value.
/// </summary>
public sealed partial class NexAudioViewModel
{
    private MixState _mix = MixState.CreateDefault();
    private long _mixRev;
    private readonly Dictionary<string, object> _pending = [];
    private readonly Dictionary<string, DateTime> _recent = [];
    private DispatcherTimer? _flush;
    private bool _mixReceived;

    private void HookDesk()
    {
        _engine.MixState += m => Dispatcher.UIThread.Post(() => ApplyMix(m.Mix, m.Rev));
        _engine.MixChanged += m => Dispatcher.UIThread.Post(() => ApplyChange(m));
        _engine.Desk += d => Dispatcher.UIThread.Post(() => ApplyDesk(d));
        _engine.MixMeters += m => Dispatcher.UIThread.Post(() => ApplyMixMeters(m));
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => Flush());
        _flush.Start();
        BuildAddChoices();
    }

    // ---- Who may change what ---------------------------------------------------------------

    /// <summary>Sound engineers (and admins) change everything; trainees only look.</summary>
    public bool CanMix => CanControl && (Live.User is not { } u || u.IsAdmin || u.Roles.HasFlag(Role.Audio));

    /// <summary>The stream volunteer can change the stream mix and nothing else.</summary>
    public bool CanStream => CanControl && (Live.User is not { } u || u.IsAdmin || u.Roles.HasFlag(Role.Audio) || u.Roles.HasFlag(Role.AudioStream));

    public bool StreamOnly => CanStream && !CanMix;

    private static bool IsStreamPath(string path) =>
        path.StartsWith("stream.", StringComparison.Ordinal) || path.StartsWith("streamset.", StringComparison.Ordinal) ||
        path.EndsWith(".streamLevel", StringComparison.Ordinal) || path.EndsWith(".streamOn", StringComparison.Ordinal);

    // ---- Sending changes -------------------------------------------------------------------

    /// <summary>Queues a change; changes go to the engine about 30 times a second, the latest value for each setting.</summary>
    public void Queue(string path, object value)
    {
        if (!(CanMix || CanStream && IsStreamPath(path)))
        {
            Notice = StreamOnly ? "You can change the stream mix only." : "Your account can look at NEX-Audio but not change the sound.";
            Dispatcher.UIThread.Post(() => ApplyMix(_mix, _mixRev));
            return;
        }
        _pending[path] = value;
        // Show it here at once.
        if (MixParams.Apply(_mix, path, value)) RefreshFor(path);
    }

    private void Flush()
    {
        if (_pending.Count == 0) return;
        var items = _pending.ToList();
        _pending.Clear();
        var now = DateTime.UtcNow;
        foreach (var (path, value) in items)
        {
            _recent[path] = now;
            _ = _engine.SendAsync(new SetMixMsg(path, JsonSerializer.SerializeToElement(value, value.GetType(), MixState.Json)));
        }
    }

    internal Task Command(string cmd, Dictionary<string, string>? args = null, object? data = null)
    {
        if (!CanMix && !(StreamOnly && cmd == "loudness.reset"))
        {
            Notice = "Your account can look at NEX-Audio but not change the sound.";
            return Task.CompletedTask;
        }
        Notice = "";
        var el = data is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(data, data.GetType(), MixState.Json);
        return _engine.SendAsync(new DeskCommandMsg(cmd, args, el));
    }

    // ---- Receiving the mix ---------------------------------------------------------------------

    private void ApplyMix(MixState mix, long rev)
    {
        _mix = mix;
        _mixRev = rev;
        _mixReceived = true;
        // Anything still waiting to go out is re-applied, so it does not flick back.
        foreach (var (p, v) in _pending) MixParams.Apply(_mix, p, v);
        var key = StructureKey(mix);
        if (key == _structure) RefreshValues();
        else
        {
            _structure = key;
            RebuildAll();
        }
    }

    private string _structure = "";

    /// <summary>What decides the screen's layout: the strips, their names, racks and groups. Levels are not part of it.</summary>
    private static string StructureKey(MixState m) =>
        string.Join("|", m.AllStrips().Select(s => $"{s.Id}:{s.Name}:{s.Stereo}:{string.Join(",", s.Rack.Select(r => r.Id))}:{s.Eq.Bands.Count}")) + "|" + m.Outputs.Count +
        "|" + string.Join(",", m.Controls.Select(g => $"{g.Id}:{g.Mode}"));

    /// <summary>Updates every value on screen without rebuilding (a scene fading in, for example).</summary>
    private void RefreshValues()
    {
        foreach (var v in AllStripViews()) if (_mix.Find(v.Id) is { } st) v.Update(st);
        UpdateBusTags();
        RefreshChannel();
        foreach (var r in OutputRows) r.Refresh(_mix);
        foreach (var p in StreamParams) p.Refresh(_mix);
        foreach (var p in StreamChannelParams) p.Refresh(_mix);
        RefreshRouting();
        RefreshGroups();
        OnPropertyChanged(nameof(AnySolo));
        OnPropertyChanged(nameof(StreamOwnMix));

        foreach (var p in SpeechParams) p.Refresh(_mix);
        OnPropertyChanged(nameof(SpeechFeedText));
    }

    public ObservableCollection<MixParam> SpeechParams { get; } = [];

    public string SpeechFeedText => _mix.Speech.Channel is not { Length: > 0 } id
        ? "Off. Pick the pastor's channel to let NEXORA's Scripture PC listen for Bible references."
        : DeskStatus.Settings.Offline
            ? "Offline preparation: nothing is sent."
            : DeskStatus.SpeechPackets > 0
                ? $"Sending {(_mix.Find(id) is { } st ? NameOf(st) : id)} to every Scripture PC on the network · {DeskStatus.SpeechPackets:N0} packets"
                : $"Will send {(_mix.Find(id) is { } st2 ? NameOf(st2) : id)} once the sound is running.";

    private bool _rebuildQueued;

    /// <summary>Rebuilds once, shortly, however many times it is asked (the X32 sends names one by one).</summary>
    internal void QueueRebuild()
    {
        if (_rebuildQueued || !_mixReceived) return;
        _rebuildQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _rebuildQueued = false;
            RebuildAll();
        }, TimeSpan.FromMilliseconds(400));
    }

    private void ApplyChange(MixChangedMsg m)
    {
        _mixRev = m.Rev;
        // An echo of something this screen is still moving: leave it, the screen is ahead.
        if (_pending.ContainsKey(m.Path) || _recent.TryGetValue(m.Path, out var t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(400)) return;
        if (MixParams.Apply(_mix, m.Path, m.Value)) RefreshFor(m.Path);
    }

    /// <summary>Brings whatever shows a setting up to date after it changed.</summary>
    private void RefreshFor(string path)
    {
        var id = path.Split('.')[0];
        if (id == "out")
        {
            if (int.TryParse(path.Split('.')[1], out var n) && n >= 1 && n <= OutputRows.Count) OutputRows[n - 1].Refresh(_mix);
            if (path.EndsWith(".source", StringComparison.Ordinal) || path.EndsWith(".name", StringComparison.Ordinal)) RefreshRouting();
            return;
        }
        if (id == "tone")
        {
            RefreshTone();
            return;
        }
        if (id == "cue")
        {
            foreach (var p in CueParams) p.Refresh(_mix);
            return;
        }
        if (id == "solo")
        {
            foreach (var p in SoloParams) p.Refresh(_mix);
            foreach (var v in AllStripViews()) v.Update(_mix.Find(v.Id)!);
            OnPropertyChanged(nameof(AnySolo));
            return;
        }
        if (_mix.Control(id) is { } cg)
        {
            if (ControlGroups.FirstOrDefault(v => v.Id == id) is { } gv) gv.Update(cg);
            // Members' chips, and their level after the master.
            foreach (var v in AllStripViews().Where(v => cg.Members.Contains(v.Id) || path.Contains(".member.", StringComparison.Ordinal) || path.Contains(".safe.", StringComparison.Ordinal)))
                if (_mix.Find(v.Id) is { } ms) v.Update(ms);
            if (path.EndsWith(".name", StringComparison.Ordinal)) OnPropertyChanged(nameof(SpillTitle));
            if (SpillId == id && (path.Contains(".member.", StringComparison.Ordinal))) FillVisible();
            if (SelectedId is not null) RefreshChannel();
            return;
        }
        if (id == "mainset")
        {
            // The centre strip comes or goes: the faders, routing grid and output list change shape.
            RebuildAll();
            return;
        }
        if (id is "streamset")
        {
            foreach (var p in StreamParams) p.Refresh(_mix);
            OnPropertyChanged(nameof(StreamOwnMix));
            return;
        }
        if (path == id + ".colour")
        {
            // Every place that shows the strip takes its new colour.
            RebuildAll();
            return;
        }
        if (_mix.Find(id) is { } s)
        {
            RefreshUtilities(id);
            foreach (var v in AllStripViews().Where(v => v.Id == id)) v.Update(s);
            // A stereo partner or linked faders moved too.
            foreach (var other in MixParams.AlsoChanges(_mix, path).ToList())
                if (_mix.Find(other) is { } os) foreach (var v in AllStripViews().Where(v => v.Id == other)) v.Update(os);
            if (path.EndsWith(".linked", StringComparison.Ordinal))
            {
                foreach (var r in PairRows) r.Refresh(_mix);
                foreach (var v in ControlGroups) if (_mix.Control(v.Id) is { } g) v.Update(g);
            }
            if (BusTags.FirstOrDefault(t => t.Id == id) is { } tag) tag.Update(s, tag.IsCurrent, NameOf(s));
            if (path.Contains(".send.", StringComparison.Ordinal) || path.EndsWith(".solo", StringComparison.Ordinal)) foreach (var v in AllStripViews()) v.Update(_mix.Find(v.Id)!);
            if (SelectedId == id) RefreshChannel();
            if (IsRoutingPath(path)) RefreshRouting();
            if (id == "stream" || path.EndsWith(".streamLevel", StringComparison.Ordinal) || path.EndsWith(".streamOn", StringComparison.Ordinal))
                foreach (var p in StreamParams) p.Refresh(_mix);
        }
        OnPropertyChanged(nameof(AnySolo));
    }

    private static bool IsRoutingPath(string path) =>
        path.Contains(".send.", StringComparison.Ordinal) || path.Contains(".group.", StringComparison.Ordinal) ||
        path.EndsWith(".toMain", StringComparison.Ordinal) || path.EndsWith(".toCentre", StringComparison.Ordinal) || path.EndsWith(".input", StringComparison.Ordinal) ||
        path.EndsWith(".altInput", StringComparison.Ordinal) || path.EndsWith(".useAlt", StringComparison.Ordinal) ||
        path.EndsWith(".directTap", StringComparison.Ordinal) || path.EndsWith(".delayMs", StringComparison.Ordinal) ||
        path.EndsWith(".name", StringComparison.Ordinal);

    private void RebuildAll()
    {
        if (OnResources) RefreshPools();
        BuildStrips();
        BuildLayers();
        BuildBusTags();
        BuildChannel();
        BuildOutputs();
        BuildStream();
        BuildRouting();
        BuildGroups();
        BuildWorkflow();
        OnPropertyChanged(nameof(AnySolo));
        OnPropertyChanged(nameof(LatencyMixText));
    }

    // ---- Mixer ---------------------------------------------------------------------------------

    /// <summary>Levels for every strip: left sides first, then right sides.</summary>
    public MeterSet StripMeters { get; } = new(0);

    public ObservableCollection<StripView> ChannelStrips { get; } = [];
    public ObservableCollection<StripView> BusStrips { get; } = [];
    public ObservableCollection<StripView> VisibleStrips { get; } = [];
    [ObservableProperty] private StripView? _mainStrip;
    /// <summary>The centre of a left-centre-right main, beside the main fader.</summary>
    [ObservableProperty] private StripView? _centreStrip;
    [ObservableProperty] private StripView? _layerStrip;

    private IEnumerable<StripView> AllStripViews()
    {
        foreach (var v in ChannelStrips) yield return v;
        foreach (var v in BusStrips) yield return v;
        if (MainStrip is { } m) yield return m;
        if (CentreStrip is { } c) yield return c;
        if (LayerStrip is { } l) yield return l;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BankLow), nameof(BankHigh), nameof(BankBuses), nameof(BankGroups), nameof(ShowStrips))]
    private int _bank;

    public bool BankLow => Bank == 0;
    public bool BankHigh => Bank == 1;
    public bool BankBuses => Bank == 2;
    /// <summary>The ordinary faders are showing (not the control group masters).</summary>
    public bool ShowStrips => Bank != 3;

    /// <summary>The mix as this screen has it.</summary>
    internal MixState Mix => _mix;

    [RelayCommand]
    private void ShowBank(string bank)
    {
        Bank = int.Parse(bank, CultureInfo.InvariantCulture);
        if (SpillId is not null)
        {
            SpillId = null;
            foreach (var v in ControlGroups) v.IsSpilled = false;
        }
        FillVisible();
    }

    public string NameOf(Strip s)
    {
        if (s.Name.Length > 0) return s.Name;
        if (s.Kind == StripKind.Channel && _x32.Info is not null)
        {
            var ch = _x32.Channels;
            if (s.Number >= 1 && s.Number <= ch.Count && ch[s.Number - 1].Name.Length > 0) return ch[s.Number - 1].Name;
        }
        else if (s.Kind == StripKind.Channel && CachedName(s.Number) is { } cached) return cached;
        return MixState.Label(s);
    }

    private void BuildStrips()
    {
        var all = _mix.AllStrips().ToList();
        var n = all.Count;
        StripMeters.Resize(n * 2);
        int Index(Strip s) => all.IndexOf(s);
        ChannelStrips.Clear();
        foreach (var c in _mix.Channels) ChannelStrips.Add(new StripView(this, c, Index(c), -1));
        BusStrips.Clear();
        foreach (var kind in new[] { StripKind.Group, StripKind.Fx, StripKind.Mix, StripKind.Matrix })
            foreach (var b in _mix.BusesOf(kind)) BusStrips.Add(new StripView(this, b, Index(b), b.Stereo ? Index(b) + n : -1));
        BusStrips.Add(new StripView(this, _mix.Mono, Index(_mix.Mono), -1));
        BusStrips.Add(new StripView(this, _mix.Stream, Index(_mix.Stream), Index(_mix.Stream) + n));
        MainStrip = new StripView(this, _mix.Main, Index(_mix.Main), Index(_mix.Main) + n);
        CentreStrip = _mix.IsLcr ? new StripView(this, _mix.Centre, Index(_mix.Centre), -1) : null;
        LayerStrip = LayerBus is { } lb ? new StripView(this, lb, Index(lb), lb.Stereo ? Index(lb) + n : -1) : null;
        foreach (var v in AllStripViews()) v.IsSelected = v.Id == SelectedId;
        FillVisible();
    }

    private void FillVisible()
    {
        VisibleStrips.Clear();
        var spill = SpillId is { } sid ? _mix.Control(sid) : null;
        IEnumerable<StripView> src = spill is not null
            ? ChannelStrips.Concat(BusStrips).Where(v => spill.Members.Contains(v.Id))
            : Bank switch
            {
                0 => ChannelStrips.Take(16),
                1 => ChannelStrips.Skip(16),
                3 => [],
                >= 10 => UserBankStrips(Bank - 10),
                _ => BusStrips,
            };
        foreach (var v in src) VisibleStrips.Add(v);
    }

    private void ApplyMixMeters(MixMetersMsg m)
    {
        var n = m.L.Length;
        if (StripMeters.Count != n * 2) StripMeters.Resize(n * 2);
        var levels = new float[n * 2];
        Array.Copy(m.L, levels, n);
        Array.Copy(m.R, 0, levels, n, n);
        StripMeters.Push(levels, new bool[n * 2]);
        if (m.Phase is { Length: >= 2 } ph)
        {
            MainPhase = ph[0];
            StreamPhase = ph[1];
        }
        UpdateUtilityMeters(m.L, m.R);
        UpdateBusMeters(m.L, m.R);
        if (m.Cost is { } cost) ApplyCosts(cost);
        foreach (var v in VisibleStrips.Append(MainStrip).Append(CentreStrip))
        {
            if (v is null || v.MeterIndex < 0 || v.MeterIndex >= n) continue;
            v.GateGr = m.Gate[v.MeterIndex];
            v.CompGr = m.Comp[v.MeterIndex];
            var hit = m.Trigger is { } tr && v.MeterIndex < tr.Length ? tr[v.MeterIndex] : 0;
            v.TriggerFlash = hit > 0 ? hit : v.TriggerFlash < 0.05 ? 0 : v.TriggerFlash * 0.6;
        }
        if (SelectedId is { } id)
        {
            var i = _mix.AllStrips().Select(s => s.Id).ToList().IndexOf(id);
            if (i >= 0 && i < n)
            {
                SelectedGate = m.Gate[i];
                SelectedComp = m.Comp[i];
                SelectedRack = m.Rack[i];
                SelectedDynEq = m.DynEq is { } dq && i < dq.Length ? dq[i] : 0;
                SelectedIn = m.In[i] <= 0 ? -60 : Math.Max(-60, 20 * Math.Log10(m.In[i]));
                PushHistory(m.In[i] <= 0 ? -90 : 20 * Math.Log10(m.In[i]), m.Gate[i], m.Comp[i]);
                ApplyTriggerLive(m, i);
            }
        }
        MonitorLevel = Math.Max(m.MonitorL, m.MonitorR);
        if (m.Loudness is { } l) ApplyLoudness(l);
        if (m.Spectrum is { } sp) Spectrum = sp;
    }

    [ObservableProperty] private float _monitorLevel;
    [ObservableProperty] private float[]? _spectrum;

    public bool AnySolo => _mix.AllStrips().Any(s => s.Solo);

    [RelayCommand]
    private Task ClearSolo() => Command("solo.clear");

    // Sends on faders: the faders show each strip's send to one bus.

    public ObservableCollection<LayerChoice> Layers { get; } = [];
    [ObservableProperty] private LayerChoice? _layer;
    private bool _buildingLayers;

    public Strip? LayerBus => Layer?.BusId is { } id ? _mix.Find(id) : null;
    public bool OnSendsLayer => LayerBus is not null;
    public string LayerTitle => LayerBus is { } b ? $"Faders show the sends to {NameOf(b)}" : "Faders show the main mix";

    private void BuildLayers()
    {
        _buildingLayers = true;
        var keep = Layer?.BusId;
        Layers.Clear();
        Layers.Add(new LayerChoice(null, "Main mix"));
        foreach (var kind in new[] { StripKind.Mix, StripKind.Fx })
            foreach (var b in _mix.BusesOf(kind)) Layers.Add(new LayerChoice(b.Id, $"Sends to {NameOf(b)}"));
        Layer = Layers.FirstOrDefault(l => l.BusId == keep) ?? Layers[0];
        _buildingLayers = false;
        OnPropertyChanged(nameof(OnSendsLayer));
        OnPropertyChanged(nameof(LayerTitle));
    }

    partial void OnLayerChanged(LayerChoice? value)
    {
        if (_buildingLayers) return;
        BuildStrips();
        UpdateBusTags();
        OnPropertyChanged(nameof(OnSendsLayer));
        OnPropertyChanged(nameof(LayerTitle));
        OnPropertyChanged(nameof(LayerTone));
    }

    /// <summary>The colour of the bus on the faders (gold for the main mix).</summary>
    public IBrush LayerTone => new SolidColorBrush(ColourOf(LayerBus ?? _mix.Main));

    public string LatencyMixText => DeskStatus.MixLatencyMs > 0.05 ? $"{DeskStatus.MixLatencyMs:0.0} ms added by effects and plugins (lined up automatically)" : "";

    // ---- Channel page ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedId = "ch1";

    public bool HasSelection => SelectedStrip is not null;
    public Strip? SelectedStrip => SelectedId is { } id ? _mix.Find(id) : null;
    public string SelectedTitle => SelectedStrip is { } s ? $"{NameOf(s)}" : "";
    public string SelectedKind => SelectedStrip is { } s ? s.Kind == StripKind.Channel ? $"Channel {s.Number}" : MixState.KindLabel(s.Kind) : "";
    public FilterSettings? SelectedFilters => SelectedStrip is { } s ? FilterSettings.From(s) : null;
    public ObservableCollection<ParamSection> ChannelSections { get; } = [];
    public ObservableCollection<RackItemView> Rack { get; } = [];
    public ObservableCollection<Choice> CopyTargets { get; } = [];
    [ObservableProperty] private Choice? _copyTarget;
    [ObservableProperty] private double _selectedGate;
    [ObservableProperty] private double _selectedComp;
    [ObservableProperty] private double _selectedRack;
    [ObservableProperty] private double _selectedIn = -60;
    public ObservableCollection<StripView> SelectBar { get; } = [];

    public void SelectStrip(string id, bool open)
    {
        SelectedId = id;
        foreach (var v in AllStripViews()) v.IsSelected = v.Id == id;
        BuildChannel();
        _ = _engine.SendAsync(new DeskCommandMsg("analyse", new() { ["strip"] = id }));
        TellSurfaces(id);
        AutoSolo(id);
        if (open) Tab = NexAudioTab.Channel;
    }

    [RelayCommand]
    private void SelectFromBar(string id) => SelectStrip(id, false);

    [RelayCommand]
    private void StepSelection(string dir)
    {
        var all = _mix.AllStrips().ToList();
        var i = all.FindIndex(s => s.Id == SelectedId);
        var next = Math.Clamp(i + (dir == "-1" ? -1 : 1), 0, all.Count - 1);
        SelectStrip(all[next].Id, false);
    }

    private void BuildChannel()
    {
        ChannelSections.Clear();
        Rack.Clear();
        CopyTargets.Clear();
        OnPropertyChanged(nameof(SelectedStrip));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(SelectedFilters));
        OnPropertyChanged(nameof(HasSelection));
        if (SelectedStrip is not { } s) return;
        var id = s.Id;
        Strip? S(MixState m) => m.Find(id);
        Action<string, object> q = Queue;

        // Where it goes.
        var route = new ParamSection("Routing");
        if (s.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group)
            route.Items.Add(new ToggleParam($"{id}.toMain", "To the main mix", m => S(m)?.ToMain ?? false, q));
        if (s.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group && _mix.IsLcr && !s.Stereo)
            route.Items.Add(new ToggleParam($"{id}.toCentre", "Pan across left, centre and right (off: left and right only)", m => S(m)?.ToCentre ?? false, q));
        if (s.Kind == StripKind.Main)
            route.Items.Add(new ChoiceParam("mainset.layout", "Main speakers", [new("Lr", "Left and right"), new("Lcr", "Left, centre and right (a centre cluster)")],
                m => m.MainSettings.Layout.ToString(), q));
        if (s.Kind is StripKind.Channel or StripKind.Fx)
            foreach (var g in _mix.BusesOf(StripKind.Group))
            {
                var gid = g.Id;
                route.Items.Add(new ToggleParam($"{id}.group.{gid}", $"Through {NameOf(g)}", m => S(m)?.Groups.Contains(gid) ?? false, q));
            }
        if (s.Kind is StripKind.Mix or StripKind.Fx or StripKind.Matrix)
        {
            var taps = MixState.Taps.Select(t => new Choice(t.Tap.ToString(), t.Name)).ToList();
            route.Items.Add(new ChoiceParam($"{id}.sendsFrom", "Every send to this bus is taken", taps, m => S(m) is { } b ? MixState.DefaultTap(b).ToString() : null, q));
        }
        if (s.Kind is StripKind.Mix or StripKind.Fx or StripKind.Group or StripKind.Matrix)
            route.Items.Add(new ToggleParam($"{id}.stereo", "Stereo (left and right)", m => S(m)?.Stereo ?? false, q));
        route.Items.Add(new ChoiceParam($"{id}.colour", "Colour",
        [
            new("", s.Kind == StripKind.Channel ? "As on the X32" : "Its usual colour"), new("#E5484D", "Red"), new("#F59E5B", "Orange"), new("#F2D06B", "Yellow"),
            new("#9BD66B", "Green"), new("#4FD1C5", "Cyan"), new("#6CB4F5", "Blue"), new("#A78BFA", "Purple"), new("#F472B6", "Pink"), new("#C9CDD4", "White"),
        ], m => S(m)?.Colour ?? "", q));
        route.Items.Add(new ToggleParam($"{id}.recallSafe", "Scenes leave this strip alone", m => S(m)?.RecallSafe ?? false, q));
        route.Items.Add(new ChoiceParam($"{id}.safe", "Or keep safe only",
        [
            new("None", "Nothing: scenes change all of it"), new("Input", "Its input (gain, trim, phantom)"), new("Eq, Dynamics", "Its EQ and dynamics"),
            new("Input, Eq, Dynamics, Inserts", "All its processing"), new("Fader, Mute", "Its fader and mute"), new("Sends", "Its sends (monitor mixes)"),
            new("Input, Eq, Dynamics, Inserts, Fader, Mute", "Processing, fader and mute (a guest's mic)"),
        ], m => S(m)?.Safe.ToString(), q));
        if (s.Kind == StripKind.Channel && s.Number % 2 == 1 && s.Number < MixState.ChannelCount)
            route.Items.Add(new ToggleParam($"{id}.linked", $"Stereo pair with channel {s.Number + 1} (changes go to both)", m => S(m)?.Linked ?? false, q));
        else if (s.Kind == StripKind.Channel && ControlRules.Partner(_mix, s) is { } left)
            route.Items.Add(new ToggleParam($"{left.Id}.linked", $"Stereo pair with channel {left.Number} (changes go to both)", m => m.Find(left.Id)?.Linked ?? false, q));
        if (s.Kind is not (StripKind.Main or StripKind.Stream))
            route.Items.Add(new ToggleParam($"{id}.soloSafe", "Solo safe (stays soloed when solos are cleared)", m => S(m)?.SoloSafe ?? false, q));
        ChannelSections.Add(route);

        // Control groups this strip is in.
        if (s.Kind != StripKind.Stream && _mix.Controls.Count > 0)
        {
            var cgs = new ParamSection("Control groups", "Tap to put this strip in a group or take it out. A safe member stays in the group but ignores it for now.");
            foreach (var g in _mix.Controls)
            {
                var gid = g.Id;
                cgs.Items.Add(new ToggleParam($"{gid}.member.{id}", $"{ControlRules.Label(g)} ({ControlRules.ModeLabel(g.Mode).ToLowerInvariant()})", m => m.Control(gid)?.Members.Contains(id) ?? false, q));
            }
            ChannelSections.Add(cgs);
        }

        if (s.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group)
        {
            var st = new ParamSection("Stream mix", "Used when the stream has its own balance (Stream tab).");
            st.Items.Add(new ToggleParam($"{id}.streamOn", "In the stream", m => S(m)?.StreamOn ?? false, q));
            st.Items.Add(new SliderParam($"{id}.streamLevel", "Stream level", Db.Floor, 10, "dB", m => S(m)?.StreamLevel, q, step: 0.5));
            ChannelSections.Add(st);
        }

        foreach (var sec in ChannelSections)
        {
            sec.Switch?.Refresh(_mix);
            foreach (var p in sec.Items) p.Refresh(_mix);
        }

        for (var i = 0; i < s.Rack.Count; i++)
        {
            var rv = new RackItemView(this, id, s.Rack[i], i, s.Rack.Count);
            rv.Refresh(_mix);
            Rack.Add(rv);
        }
        UpdatePluginStatus();

        foreach (var other in _mix.AllStrips().Where(o => o.Id != id && o.Kind == s.Kind)) CopyTargets.Add(new Choice(other.Id, NameOf(other)));
        CopyTarget = CopyTargets.FirstOrDefault();
        SelectedName = s.Name;

        SelectBar.Clear();
        foreach (var v in ChannelStrips.Concat(BusStrips).Append(MainStrip!)) if (v is not null) SelectBar.Add(v);
        BuildWorkspace(s);
    }

    [ObservableProperty] private string _selectedName = "";

    [RelayCommand]
    private void Rename()
    {
        if (SelectedId is { } id) Queue($"{id}.name", SelectedName.Trim());
        RebuildAll();
    }

    private void RefreshChannel()
    {
        foreach (var sec in ChannelSections)
        {
            sec.Switch?.Refresh(_mix);
            foreach (var p in sec.Items) p.Refresh(_mix);
        }
        foreach (var r in Rack) r.Refresh(_mix);
        OnPropertyChanged(nameof(SelectedFilters));
        UpdateWorkspace();
    }

    [RelayCommand]
    private Task CopyProcessing()
    {
        if (SelectedId is not { } from || CopyTarget is not { } to) return Task.CompletedTask;
        return Command("copy", new() { ["from"] = from, ["to"] = to.Value });
    }

    // Rack

    public ObservableCollection<AddChoice> AddChoices { get; } = [];
    [ObservableProperty] private AddChoice? _addChoice;

    private void BuildAddChoices()
    {
        var keep = AddChoice?.Label;
        AddChoices.Clear();
        foreach (var e in Effects.All)
        {
            if (e.Type == "reverb")
            {
                foreach (var st in new[] { "room", "hall", "plate", "chamber", "church", "ambience" })
                    AddChoices.Add(new AddChoice("reverb", st, "Reverb: " + st, e.Group));
            }
            else AddChoices.Add(new AddChoice(e.Type, null, e.Group == "Effects rack" ? "Effects rack: " + e.Name : e.Name, e.Group));
        }
        foreach (var p in DeskStatus.Plugins.Where(p => !p.IsInstrument))
            AddChoices.Add(new AddChoice("plugin", null, $"Plugin: {p.Name} ({p.Vendor})", "Plugins", p));
        AddChoice = AddChoices.FirstOrDefault(a => a.Label == keep) ?? AddChoices.FirstOrDefault();
    }

    [RelayCommand]
    private Task AddEffect()
    {
        if (SelectedId is not { } id || AddChoice is not { } a) return Task.CompletedTask;
        var args = new Dictionary<string, string> { ["strip"] = id, ["type"] = a.Type };
        if (a.Style is { } st) args["style"] = st;
        object? data = a.Plugin is { } p ? new PluginRef(p.Path, p.ClassId, p.Name, p.Vendor) : null;
        return Command("rack.add", args, data);
    }

    internal void RackCommand(string cmd, string strip, string item, int move = 0)
    {
        var args = new Dictionary<string, string> { ["strip"] = strip, ["item"] = item };
        if (cmd == "rack.move")
        {
            var s = _mix.Find(strip);
            var i = s?.Rack.FindIndex(r => r.Id == item) ?? -1;
            args["index"] = (i + move).ToString(CultureInfo.InvariantCulture);
        }
        _ = Command(cmd, args);
    }

    private void UpdatePluginStatus()
    {
        foreach (var r in Rack.Where(r => r.IsPlugin))
        {
            var st = DeskStatus.PluginSlots.FirstOrDefault(p => p.RackId == r.Id);
            r.Status = st is null ? IsRunning ? "Starting…" : "Starts with the sound engine." : st.Status switch
            {
                "running" => $"Running · adds {st.LatencySamples * 1000.0 / Math.Max(1, Status.SampleRate):0.0} ms (lined up)" + (st.Misses > 0 ? $" · {st.Misses} late blocks" : ""),
                "failed" => st.Message ?? "Switched off after failing.",
                "restarting" => "Stopped; starting it again. The sound goes through without it meanwhile.",
                _ => "Starting…",
            };
        }
    }

    // ---- Outputs -------------------------------------------------------------------------------

    public ObservableCollection<OutputRow> OutputRows { get; } = [];
    private List<Choice> _sources = [];

    private void BuildOutputs()
    {
        var src = new List<Choice> { new("off", "Off (silent)"), new("tone", "Test signal (set it on the Routing tab)") };
        src.Add(new("main:l", "Main left"));
        src.Add(new("main:r", "Main right"));
        if (_mix.IsLcr) src.Add(new("centre", "Main centre"));
        src.Add(new("mono", "Mono / sub"));
        foreach (var kind in new[] { StripKind.Mix, StripKind.Matrix, StripKind.Group, StripKind.Fx })
            foreach (var b in _mix.BusesOf(kind))
            {
                if (b.Stereo)
                {
                    src.Add(new($"{b.Id}:l", $"{NameOf(b)} left"));
                    src.Add(new($"{b.Id}:r", $"{NameOf(b)} right"));
                }
                else src.Add(new(b.Id, NameOf(b)));
            }
        src.Add(new("stream:l", "Stream left"));
        src.Add(new("stream:r", "Stream right"));
        src.Add(new("cue:l", "Headphones left"));
        src.Add(new("cue:r", "Headphones right"));
        src.Add(new("mon:l", "Solo bus 1 left"));
        src.Add(new("mon:r", "Solo bus 1 right"));
        src.Add(new("mon2:l", "Solo bus 2 left"));
        src.Add(new("mon2:r", "Solo bus 2 right"));
        foreach (var c in _mix.Channels) src.Add(new($"ch:{c.Number}", $"Channel {c.Number} direct out · {NameOf(c)}"));
        for (var i = 1; i <= Math.Max(32, Status.Inputs); i++) src.Add(new($"in:{i}", $"Input {i} straight through"));
        var changed = !src.Select(x => x.Value + x.Label).SequenceEqual(_sources.Select(x => x.Value + x.Label));
        _sources = src;
        if (changed || OutputRows.Count != _mix.Outputs.Count)
        {
            OutputRows.Clear();
            for (var o = 1; o <= _mix.Outputs.Count; o++) OutputRows.Add(new OutputRow(this, o, _sources));
        }
        foreach (var r in OutputRows) r.Refresh(_mix);
    }

    [RelayCommand]
    private void OutputsStraight()
    {
        for (var o = 1; o <= OutputRows.Count; o++) Queue($"out.{o}.source", $"in:{o}");
        BuildOutputs();
    }

    [RelayCommand]
    private void OutputsSuggested()
    {
        // A sensible start: main on 1 and 2, mono/sub on 3, mixes from 5, the stream on 15 and 16.
        Queue("out.1.source", "main:l");
        Queue("out.2.source", "main:r");
        Queue("out.3.source", "mono");
        var o = 5;
        foreach (var b in _mix.BusesOf(StripKind.Mix))
        {
            if (o > 14) break;
            Queue($"out.{o}.source", b.Stereo ? $"{b.Id}:l" : b.Id);
            o++;
        }
        Queue("out.15.source", "stream:l");
        Queue("out.16.source", "stream:r");
        BuildOutputs();
    }

    // ---- Stream --------------------------------------------------------------------------------

    public ObservableCollection<MixParam> StreamParams { get; } = [];
    public ObservableCollection<MixParam> StreamChannelParams { get; } = [];
    public bool StreamOwnMix => _mix.StreamSettings.Source == StreamSource.Own;

    private void BuildStream()
    {
        StreamParams.Clear();
        Action<string, object> q = Queue;
        StreamParams.Add(new ChoiceParam("streamset.source", "The stream hears", [new("Main", "A copy of the main mix"), new("Own", "Its own balance")], m => m.StreamSettings.Source.ToString(), q));
        StreamParams.Add(new ToggleParam("streamset.sendToLiveOutput", "Send to the Live Output PC", m => m.StreamSettings.SendToLiveOutput, q));
        StreamParams.Add(new SliderParam("stream.fader", "Stream level", Db.Floor, 10, "dB", m => m.Stream.Fader, q, step: 0.5));
        StreamParams.Add(new SliderParam("streamset.targetLufs", "Loudness target", -30, -6, "LUFS", m => m.StreamSettings.TargetLufs, q, step: 0.5));
        StreamParams.Add(new SliderParam("streamset.ceiling", "Peak ceiling", -6, 0, "dB", m => m.StreamSettings.Ceiling, q, step: 0.1));
        StreamParams.Add(new SliderParam("streamset.delayMs", "Delay to match the video", 0, 1000, "ms", m => m.StreamSettings.DelayMs, q, step: 1));
        StreamParams.Add(new ToggleParam("stream.comp.on", "Stream compressor", m => m.Stream.Comp.On, q));
        StreamParams.Add(new SliderParam("stream.comp.threshold", "Compressor threshold", -40, 0, "dB", m => m.Stream.Comp.Threshold, q, step: 0.5));
        foreach (var p in StreamParams) p.Refresh(_mix);

        StreamChannelParams.Clear();
        foreach (var s in _mix.Channels.Concat(_mix.BusesOf(StripKind.Fx)).Concat(_mix.BusesOf(StripKind.Group)))
        {
            var id = s.Id;
            var p = new SliderParam($"{id}.streamLevel", NameOf(s), Db.Floor, 10, "dB", m => m.Find(id)?.StreamLevel, q, step: 0.5);
            p.Refresh(_mix);
            StreamChannelParams.Add(p);
        }
        OnPropertyChanged(nameof(StreamOwnMix));

        // The speech feed for NEXORA's Scripture listening.
        SpeechParams.Clear();
        var mics = new List<Choice> { new("", "Off") };
        mics.AddRange(_mix.Channels.Select(c => new Choice(c.Id, NameOf(c))));
        SpeechParams.Add(new ChoiceParam("speech.channel", "Pastor's mic", mics, m => m.Speech.Channel, q));
        SpeechParams.Add(new ToggleParam("speech.afterFader", "Only while the mic is up (after the fader)", m => m.Speech.AfterFader, q));
        foreach (var p in SpeechParams) p.Refresh(_mix);
        OnPropertyChanged(nameof(SpeechFeedText));
    }

    [ObservableProperty] private string _lufsMomentary = "—";
    [ObservableProperty] private string _lufsShort = "—";
    [ObservableProperty] private string _lufsIntegrated = "—";
    [ObservableProperty] private string _truePeak = "—";
    [ObservableProperty] private double _lufsShortValue = -60;
    [ObservableProperty] private string _loudnessAdvice = "";
    [ObservableProperty] private bool _peakWarning;

    private static string L(double v) => double.IsFinite(v) && v > -70 ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    private void ApplyLoudness(LoudnessReading l)
    {
        LufsMomentary = L(l.Momentary);
        LufsShort = L(l.ShortTerm);
        LufsIntegrated = L(l.Integrated);
        TruePeak = L(l.MaxTruePeak);
        LufsShortValue = double.IsFinite(l.ShortTerm) ? l.ShortTerm : -60;
        var target = _mix.StreamSettings.TargetLufs;
        PeakWarning = l.MaxTruePeak > _mix.StreamSettings.Ceiling + 0.2;
        LoudnessAdvice = !double.IsFinite(l.Integrated) || l.Integrated < -60 ? "Waiting for sound on the stream mix."
            : l.Integrated < target - 2 ? $"The stream is quieter than the {target:0} LUFS target. Raise the stream level a little."
            : l.Integrated > target + 2 ? $"The stream is louder than the {target:0} LUFS target. Lower the stream level a little."
            : "The stream is at the right loudness.";
    }

    public double LoudnessTarget => _mix.StreamSettings.TargetLufs;

    [RelayCommand]
    private Task ResetLoudness() => _engine.SendAsync(new DeskCommandMsg("loudness.reset"));

    // ---- Desk status: scenes, plugins, failsafe ---------------------------------------------------

    [ObservableProperty] private DeskStatus _deskStatus = DeskStatus.Empty;
    public ObservableCollection<SceneRow> SceneRows { get; } = [];
    public ObservableCollection<SceneRow> SnapshotRows { get; } = [];
    public ObservableCollection<PluginInfo> FoundPlugins { get; } = [];
    public ObservableCollection<Choice> X32SceneChoices { get; } = [];
    private string _scenesKey = "", _pluginsKey = "", _x32ScenesKey = "";
    private bool _applyingDesk;

    private void ApplyDesk(DeskStatus d)
    {
        _applyingDesk = true;
        DeskStatus = d;
        ApplyTools(d);
        ApplyWorkflowStatus();
        ApplyRoutings(d);
        ApplyShow(d);
        ApplySurfaces(d);
        ApplyTape(d.Tape);
        ApplyDrums(d);
        BuildDiagnostics();
        var key = string.Join("|", d.Scenes.Concat(d.Snapshots).Select(s => s.Id + s.Name + s.SavedUtc.Ticks)) + d.Settings.CurrentSceneId + _currentSnapshot;
        if (key != _scenesKey)
        {
            _scenesKey = key;
            SceneRows.Clear();
            foreach (var s in d.Scenes) SceneRows.Add(new SceneRow(s, false, s.Id == d.Settings.CurrentSceneId, this));
            SnapshotRows.Clear();
            foreach (var s in d.Snapshots) SnapshotRows.Add(new SceneRow(s, true, s.Id == _currentSnapshot, this));
        }
        var pk = string.Join("|", d.Plugins.Select(p => p.Path + p.ClassId));
        if (pk != _pluginsKey)
        {
            _pluginsKey = pk;
            FoundPlugins.Clear();
            foreach (var p in d.Plugins) FoundPlugins.Add(p);
            BuildAddChoices();
        }
        var xk = string.Join("|", d.X32Scenes.Select(s => s.Number + s.Name));
        if (xk != _x32ScenesKey || X32SceneChoices.Count == 0)
        {
            _x32ScenesKey = xk;
            X32SceneChoices.Clear();
            var names = d.X32Scenes.ToDictionary(s => s.Number, s => s.Name);
            for (var i = 0; i < 100; i++)
                X32SceneChoices.Add(new Choice(i.ToString(CultureInfo.InvariantCulture), names.TryGetValue(i, out var n) && n.Length > 0 ? $"Scene {i + 1}: {n}" : $"Scene {i + 1}"));
        }
        DeskScene = X32SceneChoices.ElementAtOrDefault(d.Settings.DeskScene);
        ConsoleScene = X32SceneChoices.ElementAtOrDefault(d.Settings.ConsoleScene);
        if (!_editingFailsafe)
        {
            Armed = d.Settings.Armed;
            ShadowOn = d.Settings.ShadowMix;
            FollowDesk = d.Settings.FollowDesk;
            LiveOutputHost = d.Settings.LiveOutputHost ?? "";
            TimeoutMs = d.Settings.TimeoutMs;
        }
        UpdatePluginStatus();
        _applyingDesk = false;
        foreach (var p in new[]
                 {
                     nameof(CurrentSceneText), nameof(FailsafeStateText), nameof(FailsafeDetail), nameof(HandedOver), nameof(NotHandedOver),
                     nameof(DeskX32Text), nameof(LatencyMixText), nameof(ScanText), nameof(PluginCountText), nameof(StreamLinkText), nameof(SpeechFeedText), nameof(FadeRunning),
                 })
            OnPropertyChanged(p);
    }

    // Scenes

    [ObservableProperty] private string _newSceneName = "";
    [ObservableProperty] private string _newSnapshotName = "";
    [ObservableProperty] private Choice? _sceneScope;
    [ObservableProperty] private double _snapshotFade;
    private string? _currentSnapshot;

    public IReadOnlyList<Choice> ScopeChoices { get; } =
    [
        new("All", "Everything"), new("Levels", "Levels only (faders, mutes, sends)"), new("Levels, Processing", "Levels and processing"),
        new("Levels, Effects", "Levels and effects"), new("Custom", "Choose the sections…"),
    ];

    /// <summary>The sections to tick when a snapshot's scope is "Choose the sections".</summary>
    public IReadOnlyList<ScopeToggle> ScopeSections { get; } = RecallScopes.Sections.Select(s => new ScopeToggle(s, RecallScopes.Name(s), s is RecallScope.Fader or RecallScope.Mute)).ToList();
    public bool IsCustomScope => SceneScope?.Value == "Custom";
    partial void OnSceneScopeChanged(Choice? value) => OnPropertyChanged(nameof(IsCustomScope));

    private string SnapshotScope()
    {
        if (!IsCustomScope) return (SceneScope ?? ScopeChoices[1]).Value;
        var scope = ScopeSections.Where(t => t.IsOn).Aggregate(RecallScope.None, (a, t) => a | t.Scope);
        return (scope == RecallScope.None ? RecallScope.Levels : scope).ToString();
    }

    public static string ScopeText(RecallScope s) => RecallScopes.Text(s);

    public string CurrentSceneText => DeskStatus.Scenes.FirstOrDefault(s => s.Id == DeskStatus.Settings.CurrentSceneId) is { } sc ? $"Current scene: {sc.Name}" : "No scene recalled yet";
    public bool FadeRunning => DeskStatus.SceneFade is > 0 and < 1;

    [RelayCommand]
    private Task SaveScene()
    {
        var name = NewSceneName.Trim();
        if (name.Length == 0) name = $"Service {DateTime.Now:ddd d MMM}";
        NewSceneName = "";
        return Command("scene.save", new() { ["name"] = name, ["by"] = Live.User?.Name ?? "", ["scope"] = "All" });
    }

    [RelayCommand]
    private Task SaveSnapshot()
    {
        var name = NewSnapshotName.Trim();
        if (name.Length == 0) name = $"Moment {SnapshotRows.Count + 1}";
        NewSnapshotName = "";
        return Command("scene.save", new()
        {
            ["name"] = name, ["by"] = Live.User?.Name ?? "", ["snapshot"] = "true", ["scope"] = SnapshotScope(),
            ["fade"] = SnapshotFade.ToString(CultureInfo.InvariantCulture),
        });
    }

    internal void RecallScene(string id, bool snapshot)
    {
        if (snapshot) _currentSnapshot = id;
        _ = Command("scene.recall", new() { ["id"] = id, ["snapshot"] = snapshot ? "true" : "false" });
    }

    internal void UpdateScene(SavedMixInfo info, bool snapshot) =>
        _ = Command("scene.save", new()
        {
            ["id"] = info.Id, ["name"] = info.Name, ["notes"] = info.Notes, ["by"] = Live.User?.Name ?? "", ["snapshot"] = snapshot ? "true" : "false",
            ["scope"] = info.Scope.ToString(), ["fade"] = info.FadeSeconds.ToString(CultureInfo.InvariantCulture),
        });

    internal void DeleteScene(string id, bool snapshot) => _ = Command("scene.delete", new() { ["id"] = id, ["snapshot"] = snapshot ? "true" : "false" });

    [RelayCommand]
    private void NextSnapshot() => StepSnapshot(1);

    [RelayCommand]
    private void PreviousSnapshot() => StepSnapshot(-1);

    private void StepSnapshot(int dir)
    {
        if (SnapshotRows.Count == 0) return;
        var i = SnapshotRows.ToList().FindIndex(r => r.Id == _currentSnapshot);
        var next = Math.Clamp(i + dir, 0, SnapshotRows.Count - 1);
        if (i < 0) next = dir > 0 ? 0 : SnapshotRows.Count - 1;
        RecallScene(SnapshotRows[next].Id, true);
    }

    // Plugins

    public string ScanText => DeskStatus.ScanNote ?? (DeskStatus.Plugins.Count == 0 ? "Press Look for plugins to find the VST3 plugins installed on this PC." : "");
    public string PluginCountText => DeskStatus.Plugins.Count == 0 ? "No plugins found yet" : $"{DeskStatus.Plugins.Count} plugins on this PC";

    [RelayCommand]
    private Task ScanPlugins() => Command("plugins.scan");

    // Failsafe and X32

    [ObservableProperty] private bool _armed;
    [ObservableProperty] private bool _shadowOn = true;
    [ObservableProperty] private bool _followDesk;
    [ObservableProperty] private string _liveOutputHost = "";
    [ObservableProperty] private double _timeoutMs = 500;
    [ObservableProperty] private Choice? _deskScene;
    [ObservableProperty] private Choice? _consoleScene;
    private bool _editingFailsafe;

    public bool HandedOver => DeskStatus.Settings.HandedOver;
    public bool NotHandedOver => !HandedOver;

    public string FailsafeStateText => HandedOver ? "The X32 is running the room" : DeskStatus.Settings.Armed ? "Failsafe armed" : "Failsafe off";

    public string FailsafeDetail => HandedOver
        ? DeskStatus.FailsafeNote ?? "The room is on the X32's own mix. Press Return to NEX-Audio at a quiet moment."
        : DeskStatus.Settings.Armed
            ? $"If NEX-Audio stops for {DeskStatus.Settings.TimeoutMs / 1000.0:0.0} s, the X32 recalls scene {DeskStatus.Settings.ConsoleScene + 1} and runs the room by itself."
            : "Arm the failsafe before each service, once both X32 scenes are saved.";

    public string DeskX32Text => DeskStatus.X32Connected ? $"The sound engine is linked to {DeskStatus.X32Name ?? "the X32"}" + (DeskStatus.ShadowPending > 0 ? $" · sending {DeskStatus.ShadowPending} changes" : " · desk in step") : "The sound engine is not linked to the X32 yet (X32 link tab).";

    public string StreamLinkText => DeskStatus.Settings.LiveOutputHost is { Length: > 0 } h
        ? DeskStatus.StreamPackets > 0 ? $"Sending to {h} · {DeskStatus.StreamPackets:N0} packets" : $"Will send to {h} once the sound is running"
        : "Type the Live Output PC's address on the Failsafe tab to send the stream there.";

    private void SaveFailsafe()
    {
        if (_applyingDesk || !CanMix) return;
        var s = DeskStatus.Settings with
        {
            Armed = Armed, ShadowMix = ShadowOn, FollowDesk = FollowDesk, LiveOutputHost = string.IsNullOrWhiteSpace(LiveOutputHost) ? null : LiveOutputHost.Trim(),
            TimeoutMs = (int)TimeoutMs, DeskScene = int.TryParse(DeskScene?.Value, out var d) ? d : 0, ConsoleScene = int.TryParse(ConsoleScene?.Value, out var c) ? c : 1,
        };
        _ = Command("desk.set", null, s);
    }

    partial void OnArmedChanged(bool value) => SaveFailsafe();
    partial void OnShadowOnChanged(bool value) => SaveFailsafe();
    partial void OnFollowDeskChanged(bool value) => SaveFailsafe();
    partial void OnTimeoutMsChanged(double value) => SaveFailsafe();
    partial void OnDeskSceneChanged(Choice? value) => SaveFailsafe();
    partial void OnConsoleSceneChanged(Choice? value) => SaveFailsafe();

    [RelayCommand]
    private void SaveLiveOutputHost()
    {
        _editingFailsafe = false;
        SaveFailsafe();
    }

    partial void OnLiveOutputHostChanged(string value)
    {
        if (!_applyingDesk) _editingFailsafe = true;
    }

    [RelayCommand]
    private Task HandOver() => Command("failsafe.handover");

    [RelayCommand]
    private Task ReturnToNexAudio() => Command("failsafe.return");

    [RelayCommand]
    private Task RefreshX32Scenes() => Command("x32.scenes");
}

/// <summary>One section of a recall, ticked or not.</summary>
public sealed partial class ScopeToggle(RecallScope scope, string label, bool on) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public RecallScope Scope { get; } = scope;
    public string Label { get; } = label;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isOn = on;
}
