using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>
/// The channel workspace: a home page with every setting of the open strip at a glance, a page for each part
/// (input, gate, EQ, compressor, graphic EQ, effects, sends), live graphs, and the strip's own fader docked beside it.
/// </summary>
public sealed partial class NexAudioViewModel : IEqEditor, IGraphicEqEditor
{
    [ObservableProperty] private ChannelControls? _ch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnChHome), nameof(OnChInput), nameof(OnChGate), nameof(OnChEq), nameof(OnChDynamics),
        nameof(OnChGeq), nameof(OnChEffects), nameof(OnChSends), nameof(OnChTrigger))]
    private ChannelSection _channelPage = ChannelSection.Home;

    public bool OnChHome => ChannelPage == ChannelSection.Home;
    public bool OnChInput => ChannelPage == ChannelSection.Input;
    public bool OnChTrigger => ChannelPage == ChannelSection.Trigger;
    public bool OnChGate => ChannelPage == ChannelSection.Gate;
    public bool OnChEq => ChannelPage == ChannelSection.Eq;
    public bool OnChDynamics => ChannelPage == ChannelSection.Dynamics;
    public bool OnChGeq => ChannelPage == ChannelSection.Geq;
    public bool OnChEffects => ChannelPage == ChannelSection.Effects;
    public bool OnChSends => ChannelPage == ChannelSection.Sends;

    public ObservableCollection<ChannelRailItem> Rail { get; } = [];

    partial void OnChannelPageChanged(ChannelSection value)
    {
        foreach (var r in Rail) r.IsCurrent = r.Section == value;
        if (value == ChannelSection.Gate && !(Ch?.HasGate ?? false)) ChannelPage = ChannelSection.Home;
        if (value == ChannelSection.Trigger && !(Ch?.IsChannel ?? false)) ChannelPage = ChannelSection.Home;
    }

    internal void OpenStripSection(string id, string section)
    {
        SelectStrip(id, true);
        ChannelPage = Enum.Parse<ChannelSection>(section);
    }

    [RelayCommand]
    private void OpenSection(string section)
    {
        ChannelPage = Enum.Parse<ChannelSection>(section);
        Tab = NexAudioTab.Channel;
    }

    /// <summary>The open strip's own fader, pan, mute and solo, docked beside the workspace.</summary>
    [ObservableProperty] private StripView? _selectedStripView;

    // ---- Live graphs ------------------------------------------------------------------------------

    public LevelHistory GateHistory { get; } = new();
    public LevelHistory CompHistory { get; } = new();
    [ObservableProperty] private double _compMakeupNow;

    /// <summary>Called 30 times a second with the open strip's input level and gain reduction.</summary>
    private void PushHistory(double inDb, double gate, double comp)
    {
        GateHistory.Push(inDb, gate);
        CompHistory.Push(inDb, comp);
    }

    // ---- Building and refreshing ------------------------------------------------------------------

    private void BuildWorkspace(Strip s)
    {
        Ch = new ChannelControls(this, _mix, s);
        GateHistory.Clear();
        CompHistory.Clear();
        SelectedStripView = AllStripViews().FirstOrDefault(v => v.Id == s.Id);

        Rail.Clear();
        Rail.Add(new ChannelRailItem(ChannelSection.Home, "Home", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Input, "Input", this));
        if (Ch.IsChannel) Rail.Add(new ChannelRailItem(ChannelSection.Trigger, "Trigger", this));
        if (Ch.HasGate) Rail.Add(new ChannelRailItem(ChannelSection.Gate, Ch.IsChannel ? "Gate" : "Gate / ducker", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Eq, "EQ", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Dynamics, "Compressor", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Geq, "Graphic EQ", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Effects, "Effects", this));
        Rail.Add(new ChannelRailItem(ChannelSection.Sends, "Sends", this));
        if (!Ch.HasGate && ChannelPage == ChannelSection.Gate) ChannelPage = ChannelSection.Home;
        if (!Ch.IsChannel && ChannelPage == ChannelSection.Trigger) ChannelPage = ChannelSection.Home;
        BuildTrigger(s);
        foreach (var r in Rail) r.IsCurrent = r.Section == ChannelPage;

        if (SelectedBand >= s.Eq.Bands.Count) SelectedBand = -1;
        BuildSlots(s);
        UpdateWorkspace();
    }

    private void UpdateWorkspace()
    {
        if (SelectedStrip is not { } s || Ch is null) return;
        Ch.Refresh(_mix);
        foreach (var b in Ch.Bands) b.IsSelected = b.Band == SelectedBand;
        CompMakeupNow = Ch.EffectiveMakeup(_mix);

        foreach (var r in Rail)
        {
            (r.Status, r.IsOn) = r.Section switch
            {
                ChannelSection.Home => ("Everything at a glance", false),
                ChannelSection.Input => (InputSummary(s), s.Hpf.On || s.Phantom || s.Invert),
                ChannelSection.Trigger => TriggerSummary(s),
                ChannelSection.Gate => !s.Gate.On ? ("Off", false)
                    : s.Gate.Mode == GateMode.Ducker ? ($"Ducks under {(_mix.Find(s.Gate.Key) is { } k ? NameOf(k) : "itself")}", true)
                    : ($"Opens at {s.Gate.Threshold:0} dB", true),
                ChannelSection.Eq => !s.Eq.On ? ("Off", false)
                    : ((s.Eq.IsFlat ? "Flat" : $"{s.Eq.Bands.Count(b => b.On && Math.Abs(b.Gain) >= 0.01)} bands shaping") + (s.DynEq.Active ? " · dynamic" : ""), !s.Eq.IsFlat || s.DynEq.Active),
                ChannelSection.Dynamics => s.Comp.On ? ($"{s.Comp.Ratio:0.#}:1 from {s.Comp.Threshold:0} dB", true) : ("Off", false),
                ChannelSection.Geq => GeqItem(s) is { } g ? (g.Bypass ? "Bypassed" : "In use", !g.Bypass) : ("Not added", false),
                ChannelSection.Effects => s.Rack.Count == 0 ? ("Empty rack", false) : ($"{s.Rack.Count} of 8 in use", s.Rack.Any(x => !x.Bypass)),
                ChannelSection.Sends => Ch.Sends.Count == 0 ? ("None", false) : ($"{s.Sends.Count(x => x.On && x.Level > Db.Floor)} of {Ch.Sends.Count} sending", s.Sends.Any(x => x.On && x.Level > Db.Floor)),
                _ => ("", false),
            };
        }

        UpdateGeq(s);
        UpdateSlots(s);
        UpdateTrigger(s);
        OnPropertyChanged(nameof(HomeInputText));
        OnPropertyChanged(nameof(HomeRoutingText));
    }

    private static string InputSummary(Strip s)
    {
        var parts = new List<string>();
        if (s.Preamp is { } p) parts.Add($"{p:+0;-0;0} dB");
        else if (Math.Abs(s.Trim) > 0.01) parts.Add($"trim {s.Trim:+0.#;-0.#}");
        if (s.Phantom) parts.Add("48 V");
        if (s.Hpf.On) parts.Add($"cut {GraphKitHz(s.Hpf.Freq)}");
        if (s.Drive.On) parts.Add("drive");
        return parts.Count == 0 ? "As it arrives" : string.Join(" · ", parts);
    }

    private static string GraphKitHz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";

    public string HomeInputText => SelectedStrip is { } s
        ? $"{(s.Preamp is { } p ? $"Gain {p:+0.0;-0.0;0.0} dB" : "Gain on the desk")}   ·   Trim {s.Trim:+0.0;-0.0;0.0} dB   ·   Delay {s.DelayMs:0.#} ms"
        : "";

    public string HomeRoutingText
    {
        get
        {
            if (SelectedStrip is not { } s) return "";
            switch (s.Kind)
            {
                case StripKind.Main: return _mix.IsLcr ? "The main mix: left and right speakers (the centre has its own strip)" : "The main mix: left and right speakers";
                case StripKind.Centre: return "The centre cluster of the main mix";
                case StripKind.Mono: return "The mono / sub feed";
                case StripKind.Stream: return "The stream mix";
            }
            var to = new List<string>();
            if (s.ToMain && s.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group)
                to.Add(_mix.IsLcr && Routing.FeedsCentre(_mix, s) ? "Main (left, centre, right)" : "Main");
            foreach (var g in s.Groups) if (_mix.Find(g) is { } gs) to.Add(NameOf(gs));
            return to.Count == 0 ? "Not routed to the main mix" : "To " + string.Join(", ", to);
        }
    }

    // ---- EQ editing from the graph ------------------------------------------------------------------

    [ObservableProperty] private int _selectedBand = -1;

    partial void OnSelectedBandChanged(int value)
    {
        if (Ch is null) return;
        foreach (var b in Ch.Bands) b.IsSelected = b.Band == value;
    }

    internal void SelectBand(int band) => SelectedBand = band;

    void IEqEditor.SelectBand(int band) => SelectedBand = band;

    void IEqEditor.MoveBand(int band, double freq, double gain)
    {
        if (SelectedId is not { } id) return;
        SelectedBand = band;
        Queue($"{id}.eq.{band}.freq", Math.Clamp(freq, 20, 20000));
        Queue($"{id}.eq.{band}.gain", Math.Clamp(gain, -18, 18));
    }

    void IEqEditor.ScaleQ(int band, double factor)
    {
        if (SelectedStrip is not { } s || band >= s.Eq.Bands.Count) return;
        SelectedBand = band;
        Queue($"{s.Id}.eq.{band}.q", Math.Round(Math.Clamp(s.Eq.Bands[band].Q * factor, 0.3, 10), 2));
    }

    [RelayCommand]
    private void FlattenEq()
    {
        if (SelectedStrip is not { } s) return;
        for (var i = 0; i < s.Eq.Bands.Count; i++) Queue($"{s.Id}.eq.{i}.gain", 0.0);
    }

    // ---- Graphic EQ -------------------------------------------------------------------------------------

    private static RackItem? GeqItem(Strip s) => s.Rack.FirstOrDefault(r => r.Type == "eq31");

    [ObservableProperty] private bool _hasGeq;
    [ObservableProperty] private IReadOnlyList<double>? _geqValues;
    [ObservableProperty] private ToggleParam? _geqBypass;
    [ObservableProperty] private bool _geqOn;
    private string? _geqId;

    private void UpdateGeq(Strip s)
    {
        var g = GeqItem(s);
        HasGeq = g is not null;
        if (g is null)
        {
            _geqId = null;
            GeqBypass = null;
            GeqValues = null;
            GeqOn = false;
            return;
        }
        if (_geqId != g.Id || GeqBypass is null)
        {
            _geqId = g.Id;
            var sid = s.Id;
            var gid = g.Id;
            GeqBypass = new ToggleParam($"{sid}.rack.{gid}.bypass", "Bypass", m => m.Find(sid)?.Rack.FirstOrDefault(r => r.Id == gid)?.Bypass ?? false, Queue);
        }
        GeqBypass.Refresh(_mix);
        GeqOn = !g.Bypass;
        var vals = Enumerable.Range(0, 31).Select(i => g.P(GraphicEq.ParamName(i), 0)).ToList();
        if (GeqValues is null || !vals.SequenceEqual(GeqValues)) GeqValues = vals;
    }

    void IGraphicEqEditor.SetBand(int band, double gainDb)
    {
        if (SelectedId is not { } id || _geqId is not { } gid) return;
        Queue($"{id}.rack.{gid}.p.{GraphicEq.ParamName(band)}", Math.Round(gainDb * 2) / 2);
    }

    [RelayCommand]
    private Task AddGeq()
    {
        if (SelectedId is not { } id) return Task.CompletedTask;
        return Command("rack.add", new() { ["strip"] = id, ["type"] = "eq31", ["index"] = "0" });
    }

    [RelayCommand]
    private void FlattenGeq()
    {
        if (SelectedId is not { } id || _geqId is not { } gid) return;
        for (var i = 0; i < 31; i++) Queue($"{id}.rack.{gid}.p.{GraphicEq.ParamName(i)}", 0.0);
    }

    [RelayCommand]
    private void RemoveGeq()
    {
        if (SelectedId is { } id && _geqId is { } gid) RackCommand("rack.remove", id, gid);
    }

    // ---- Effects rack as eight places -------------------------------------------------------------------

    public ObservableCollection<RackSlotView> Slots { get; } = [];
    [ObservableProperty] private RackItemView? _selectedRackView;
    [ObservableProperty] private RackItem? _selectedEffect;
    [ObservableProperty] private int _selectedSlot;
    public bool SelectedSlotEmpty => SelectedRackView is null;

    private void BuildSlots(Strip s)
    {
        Slots.Clear();
        for (var i = 0; i < 8; i++) Slots.Add(new RackSlotView(i, this));
        if (SelectedSlot >= 8) SelectedSlot = 0;
    }

    private void UpdateSlots(Strip s)
    {
        for (var i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            var item = i < s.Rack.Count ? s.Rack[i] : null;
            slot.ItemId = item?.Id;
            slot.IsEmpty = item is null;
            slot.Name = item is null ? "Empty" : Effects.NameOf(item);
            slot.Detail = item is null ? "" : item.Type == "plugin" ? item.Plugin?.Vendor ?? "Plugin" : item.Type == "reverb" ? Cap(item.T("style", "hall")) : Effects.Get(item.Type)?.Group ?? "";
            slot.IsBypassed = item?.Bypass ?? false;
            slot.IsSelected = i == SelectedSlot;
        }
        var chosen = SelectedSlot < s.Rack.Count ? s.Rack[SelectedSlot] : null;
        SelectedRackView = chosen is null ? null : Rack.FirstOrDefault(r => r.Id == chosen.Id);
        SelectedEffect = chosen?.Clone();
        OnPropertyChanged(nameof(SelectedSlotEmpty));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal void SelectRackSlot(int index)
    {
        SelectedSlot = index;
        if (SelectedStrip is { } s) UpdateSlots(s);
    }

    [RelayCommand]
    private Task AddEffectHere()
    {
        if (SelectedId is not { } id || AddChoice is not { } a) return Task.CompletedTask;
        var args = new Dictionary<string, string> { ["strip"] = id, ["type"] = a.Type };
        if (a.Style is { } st) args["style"] = st;
        if (SelectedStrip is { } s) SelectedSlot = Math.Min(s.Rack.Count, 7);
        object? data = a.Plugin is { } p ? new PluginRef(p.Path, p.ClassId, p.Name, p.Vendor) : null;
        return Command("rack.add", args, data);
    }
}
