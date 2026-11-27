using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexAudio.App.Controls;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

public enum RoutingSection
{
    Grid,
    Inputs,
    Layouts,
    Signal,
}

/// <summary>
/// The Routing tab: the routing grid (every channel and bus against every bus it can feed, with loop protection and signal trace),
/// the input patch with alternate inputs, delays and direct outs, saved routing layouts, and the test signal.
/// </summary>
public sealed partial class NexAudioViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnRtGrid), nameof(OnRtInputs), nameof(OnRtLayouts), nameof(OnRtSignal))]
    private RoutingSection _routingPage = RoutingSection.Grid;

    public bool OnRouting => Tab == NexAudioTab.Routing;
    public bool OnRtGrid => RoutingPage == RoutingSection.Grid;
    public bool OnRtInputs => RoutingPage == RoutingSection.Inputs;
    public bool OnRtLayouts => RoutingPage == RoutingSection.Layouts;
    public bool OnRtSignal => RoutingPage == RoutingSection.Signal;

    [RelayCommand]
    private void ShowRouting(string page) => RoutingPage = Enum.Parse<RoutingSection>(page);

    // ---- The routing grid -------------------------------------------------------------------------------

    [ObservableProperty] private RouteMap? _routeMap;
    [ObservableProperty] private string _routeHint = "";
    [ObservableProperty] private string? _tracedId;
    [ObservableProperty] private string _traceTitle = "";
    [ObservableProperty] private string _traceFrom = "";
    [ObservableProperty] private string _traceTo = "";
    [ObservableProperty] private string _traceOutputs = "";
    [ObservableProperty] private string _traceDelay = "";
    public bool HasTrace => TracedId is not null;
    partial void OnTracedIdChanged(string? value) => OnPropertyChanged(nameof(HasTrace));

    /// <summary>The level a new send starts at when it is made on the grid (quiet enough to bring up by ear).</summary>
    public const double NewSendLevel = -10;

    private List<Strip> _gridRows = [], _gridCols = [];

    private static string GroupOf(StripKind k) => k switch
    {
        StripKind.Channel => "Channels",
        StripKind.Fx => "Effects",
        StripKind.Group => "Groups",
        StripKind.Mix => "Mixes",
        StripKind.Main or StripKind.Mono or StripKind.Centre => "Main",
        StripKind.Matrix => "Matrices",
        _ => "Other",
    };

    private string RowLabel(Strip s) => s.Kind == StripKind.Channel ? $"{s.Number}  {NameOf(s)}" : NameOf(s);

    private void BuildRouteMap()
    {
        var m = _mix;
        List<Strip> centre = m.IsLcr ? [m.Centre] : [];
        _gridRows = [.. m.Channels, .. m.BusesOf(StripKind.Fx), .. m.BusesOf(StripKind.Group), .. m.BusesOf(StripKind.Mix), m.Main, .. centre, m.Mono, .. m.BusesOf(StripKind.Matrix)];
        _gridCols = [m.Main, .. centre, .. m.BusesOf(StripKind.Group), .. m.BusesOf(StripKind.Fx), .. m.BusesOf(StripKind.Mix), .. m.BusesOf(StripKind.Matrix)];
        var map = new RouteMap(
            _gridRows.Select(s => new RouteEnd(s.Id, RowLabel(s), GroupOf(s.Kind), StripColour(s))).ToList(),
            _gridCols.Select(s => new RouteEnd(s.Id, NameOf(s), GroupOf(s.Kind), StripColour(s))).ToList());
        for (var r = 0; r < _gridRows.Count; r++)
            for (var c = 0; c < _gridCols.Count; c++)
            {
                var from = _gridRows[r];
                var to = _gridCols[c];
                var (state, label, tip) = Crossing(m, from, to);
                map.States[r, c] = state;
                map.Labels[r, c] = label;
                map.Tips[r, c] = tip;
            }
        if (TracedId is { } t && m.Find(t) is not null)
        {
            map.Traced = t;
            map.TraceDown = Routing.Downstream(m, t);
            map.TraceUp = Routing.Upstream(m, t);
        }
        else TracedId = null;
        RouteMap = map;
        UpdateTrace();
    }

    private (RouteState, string?, string) Crossing(MixState m, Strip from, Strip to)
    {
        var name = $"{NameOf(from)} → {NameOf(to)}";
        if (to.Kind == StripKind.Main)
        {
            if (from.Kind is not (StripKind.Channel or StripKind.Fx or StripKind.Group))
                return (RouteState.Never, null, $"{name}: only channels, effect returns and groups go to the main mix this way.");
            return from.ToMain
                ? (RouteState.On, null, $"{name}: on. Tap to take it off the main mix.")
                : (RouteState.Free, null, $"{name}: off. Tap to send it to the main mix.");
        }
        if (to.Kind == StripKind.Centre)
        {
            if (from.Kind is not (StripKind.Channel or StripKind.Fx or StripKind.Group))
                return (RouteState.Never, null, $"{name}: only channels, effect returns and groups go to the centre.");
            if (from.Stereo)
                return (RouteState.Never, null, $"{name}: a stereo strip goes to left and right only. Make it mono to pan it across the centre.");
            return Routing.FeedsCentre(m, from)
                ? (RouteState.On, null, $"{name}: on, panned across left, centre and right. Tap to keep it in left and right only.")
                : (RouteState.Free, null, $"{name}: off{(from.ToMain ? " (left and right only)" : "")}. Tap to pan it across left, centre and right.");
        }
        if (to.Kind == StripKind.Group)
        {
            if (from.Groups.Contains(to.Id)) return (RouteState.On, null, $"{name}: through this group. Tap to take it out.");
            if (Routing.WhyNot(m, from, to, group: true) is { } why)
                return (Routing.CanGroup(from, to) ? RouteState.Loop : RouteState.Never, null, $"{name}: {why}");
            return (RouteState.Free, null, $"{name}: tap to put it through this group{(from.Kind == StripKind.Group ? " (it becomes a sub-group and leaves the main mix)" : "")}.");
        }
        var send = from.SendTo(to.Id);
        if (send is not null)
        {
            var level = send.Level <= Db.Floor + 0.5 ? "-∞" : send.Level.ToString("+0;-0;0", CultureInfo.InvariantCulture);
            var tap = MixState.TapShort(send.Tap);
            if (send.On && send.Level > Db.Floor)
                return (RouteState.On, level, $"{name}: {level} dB from {tap}. Tap to switch it off (the level is kept); set levels on the Channel page or with the bus tag.");
            return (RouteState.Off, null, $"{name}: a send at {level} dB from {tap}, switched off. Tap to switch it on.");
        }
        if (Routing.WhyNot(m, from, to, group: false) is { } no)
            return (Routing.CanSend(from, to) ? RouteState.Loop : RouteState.Never, null, $"{name}: {no}");
        return (RouteState.Free, null, $"{name}: tap to make a send at {NewSendLevel:0} dB from {MixState.TapShort(MixState.DefaultTap(to))}.");
    }

    /// <summary>A crossing on the grid was tapped.</summary>
    public void RouteClicked(int row, int col)
    {
        if (row < 0 || row >= _gridRows.Count || col < 0 || col >= _gridCols.Count) return;
        var from = _mix.Find(_gridRows[row].Id);
        var to = _mix.Find(_gridCols[col].Id);
        if (from is null || to is null) return;
        var (state, _, tip) = Crossing(_mix, from, to);
        switch (state)
        {
            case RouteState.Never:
            case RouteState.Loop:
                Notice = tip;
                return;
        }
        if (to.Kind == StripKind.Main) Queue($"{from.Id}.toMain", !from.ToMain);
        else if (to.Kind == StripKind.Centre)
        {
            var on = !Routing.FeedsCentre(_mix, from);
            if (on && !from.ToMain) Queue($"{from.Id}.toMain", true);
            Queue($"{from.Id}.toCentre", on);
        }
        else if (to.Kind == StripKind.Group) Queue($"{from.Id}.group.{to.Id}", !from.Groups.Contains(to.Id));
        else if (from.SendTo(to.Id) is { } send)
        {
            if (send.On && send.Level > Db.Floor) Queue($"{from.Id}.send.{to.Id}.on", false);
            else
            {
                Queue($"{from.Id}.send.{to.Id}.on", true);
                if (send.Level <= Db.Floor) Queue($"{from.Id}.send.{to.Id}.level", NewSendLevel);
            }
        }
        else Queue($"{from.Id}.send.{to.Id}.level", NewSendLevel);
        BuildRouteMap();
        RouteHint = Crossing(_mix, from, to).Item3;
    }

    /// <summary>A name on the grid was tapped: light up its whole path (tap again to stop).</summary>
    public void TraceStrip(string id)
    {
        TracedId = TracedId == id ? null : id;
        BuildRouteMap();
    }

    [RelayCommand] private void ClearTrace() => TraceStrip(TracedId ?? "");

    private void UpdateTrace()
    {
        if (TracedId is not { } id || _mix.Find(id) is not { } s)
        {
            TraceTitle = TraceFrom = TraceTo = TraceOutputs = TraceDelay = "";
            return;
        }
        string Names(IEnumerable<string> ids) =>
            string.Join(", ", ids.Select(x => _mix.Find(x)).Where(x => x is not null).OrderBy(x => Routing.Stage(x!.Kind)).ThenBy(x => x!.Number).Select(x => NameOf(x!)));
        var up = Routing.Upstream(_mix, id);
        var down = Routing.Downstream(_mix, id);
        TraceTitle = $"Tracing {NameOf(s)}";
        var inputs = s.Kind == StripKind.Channel ? InputText(s) : "";
        TraceFrom = up.Count > 0 ? Names(up) : s.Kind == StripKind.Channel ? inputs : "Nothing feeds it yet.";
        if (s.Kind == StripKind.Channel && up.Count > 0) TraceFrom = inputs + " · " + TraceFrom;
        TraceTo = down.Count > 0 ? Names(down) : "It does not go anywhere yet.";
        var path = new HashSet<string>(down) { id };
        var outs = _mix.Outputs.Where(o => OutputCarries(o.Source, path, s)).Select(o => o.Name.Length > 0 ? $"Out {o.Number} ({o.Name})" : $"Out {o.Number}").ToList();
        TraceOutputs = outs.Count > 0 ? string.Join(", ", outs) : "No output plays it.";
        var lat = DeskStatus.Latency.FirstOrDefault(l => l.Id == id);
        TraceDelay = lat is null ? "No added delay." : $"Its own effects add {lat.OwnMs:0.0} ms; it is lined up {lat.ArrivesMs:0.0} ms after the inputs.";
    }

    private static bool OutputCarries(string source, HashSet<string> path, Strip s)
    {
        var src = source.ToLowerInvariant();
        if (s.Kind == StripKind.Channel && src == $"ch:{s.Number}") return true;
        var id = src.Split(':')[0];
        return path.Contains(id);
    }

    private static string InputText(Strip c)
    {
        var main = c.Input > 0 ? $"input {c.Input}" : "no input";
        if (c.AltInput <= 0) return "Hears " + main;
        return c.UseAlt ? $"Hears its alternate, input {c.AltInput} (main is {main})" : $"Hears {main} (alternate: input {c.AltInput})";
    }

    public string LatencyText =>
        DeskStatus.Latency.Count == 0
            ? "Nothing is adding delay: every path arrives together."
            : string.Join("\n", DeskStatus.Latency.Where(l => l.OwnMs > 0.01).Select(l => $"{(_mix.Find(l.Id) is { } st ? NameOf(st) : l.Id)} adds {l.OwnMs:0.0} ms"))
              + (DeskStatus.MixLatencyMs > 0.05 ? $"\nThe main mix is {DeskStatus.MixLatencyMs:0.0} ms late; every faster path into each bus waits to match." : "");

    // ---- Inputs -----------------------------------------------------------------------------------------

    public ObservableCollection<InputRow> InputRows { get; } = [];

    private void BuildInputs()
    {
        var inputs = new List<Choice> { new("0", "None") };
        for (var i = 1; i <= Math.Max(32, Status.Inputs); i++) inputs.Add(new(i.ToString(CultureInfo.InvariantCulture), $"In {i}"));
        var taps = MixState.Taps.Select(t => new Choice(t.Tap.ToString(), t.Short)).ToList();
        var outs = new List<Choice> { new("0", "Not on an output") };
        for (var o = 1; o <= _mix.Outputs.Count; o++)
            outs.Add(new(o.ToString(CultureInfo.InvariantCulture), _mix.Outputs[o - 1].Name is { Length: > 0 } n ? $"Out {o} · {n}" : $"Out {o}"));
        if (InputRows.Count != _mix.Channels.Count || _inputOuts.Count != outs.Count)
        {
            InputRows.Clear();
            foreach (var c in _mix.Channels) InputRows.Add(new InputRow(this, c.Number, inputs, taps, outs));
        }
        _inputOuts = outs;
        foreach (var r in InputRows) r.Refresh(_mix, this);
    }

    private List<Choice> _inputOuts = [];

    /// <summary>Puts a channel's direct out on one output (the output it was on before goes silent).</summary>
    internal void PatchDirectOut(int channel, int output)
    {
        var src = $"ch:{channel}";
        foreach (var o in _mix.Outputs.Where(o => o.Source.Equals(src, StringComparison.OrdinalIgnoreCase) && o.Number != output))
            Queue($"out.{o.Number}.source", "off");
        if (output >= 1 && output <= _mix.Outputs.Count) Queue($"out.{output}.source", src);
        BuildOutputs();
    }

    [RelayCommand] private Task AllAlternate() => Command("inputs.alt", new() { ["on"] = "true" });
    [RelayCommand] private Task AllMain() => Command("inputs.alt", new() { ["on"] = "false" });

    public string AltSummary
    {
        get
        {
            var withAlt = _mix.Channels.Count(c => c.AltInput > 0);
            var onAlt = _mix.Channels.Count(c => c.UseAlt);
            return withAlt == 0 ? "No channel has an alternate input yet." : $"{withAlt} channels have an alternate input; {onAlt} are listening to it now.";
        }
    }

    // ---- Routing layouts ------------------------------------------------------------------------------------

    public ObservableCollection<RoutingRow> RoutingRows { get; } = [];
    [ObservableProperty] private string _newRoutingName = "";
    private string _routingKey = "";

    private void ApplyRoutings(Nexora.Audio.Engine.DeskStatus d)
    {
        var key = string.Join("|", d.Routings.Select(r => r.Id + r.Name + r.SavedUtc.Ticks));
        if (key != _routingKey)
        {
            _routingKey = key;
            RoutingRows.Clear();
            foreach (var r in d.Routings) RoutingRows.Add(new RoutingRow(r, this));
        }
        OnPropertyChanged(nameof(LatencyText));
        if (TracedId is not null) UpdateTrace();
    }

    [RelayCommand]
    private Task SaveRouting()
    {
        var name = NewRoutingName.Trim();
        if (name.Length == 0) name = $"Routing {DateTime.Now:ddd d MMM}";
        NewRoutingName = "";
        return Command("routing.save", new() { ["name"] = name, ["by"] = Live.User?.Name ?? "" });
    }

    internal void PreviewRouting(string id)
    {
        _afterPreview = () => RecallRouting(id);
        _ = Command("routing.preview", new() { ["id"] = id });
    }

    internal void RecallRouting(string id) => _ = Command("routing.recall", new() { ["id"] = id });
    internal void SaveRoutingOver(RoutingInfo r) => _ = Command("routing.save", new() { ["id"] = r.Id, ["name"] = r.Name, ["notes"] = r.Notes, ["by"] = Live.User?.Name ?? "" });
    internal void DeleteRouting(string id) => _ = Command("routing.delete", new() { ["id"] = id });

    // ---- Test signal ------------------------------------------------------------------------------------------

    public ChoiceParam? ToneKind { get; private set; }
    public SliderParam? ToneFreq { get; private set; }
    public SliderParam? ToneLevel { get; private set; }
    public ChoiceParam? ToneBus { get; private set; }
    [ObservableProperty] private bool _toneConfirming;

    public bool ToneOn => _mix.Tone.On;
    public bool ToneIsSine => _mix.Tone.Kind == Nexora.Audio.Mix.ToneKind.Sine;
    public string ToneOutputsText
    {
        get
        {
            var outs = _mix.Outputs.Where(o => o.Source == "tone").Select(o => $"Out {o.Number}").ToList();
            return outs.Count == 0 ? "No output is set to the test signal (Outputs tab)."
                : (outs.Count == 1 ? outs[0] + " is" : string.Join(", ", outs) + " are") + " set to the test signal on the Outputs tab, so " + (outs.Count == 1 ? "it plays" : "they play") + " it all the time.";
        }
    }
    public string ToneStateText => _mix.Tone.On && _mix.Find(_mix.Tone.Bus) is { } b
        ? $"Playing into {NameOf(b)} now."
        : "Not playing into any bus.";

    private void BuildTone()
    {
        var kinds = new List<Choice> { new("Sine", "Sine tone"), new("Pink", "Pink noise"), new("White", "White noise") };
        ToneKind = new ChoiceParam("tone.kind", "Signal", kinds, m => m.Tone.Kind.ToString(), Queue);
        ToneFreq = new SliderParam("tone.freq", "Frequency", 20, 20000, "Hz", m => m.Tone.Freq, Queue, log: true, def: 1000);
        ToneLevel = new SliderParam("tone.level", "Level", -60, ToneSettings.MaxLevel, "dB", m => m.Tone.Level, Queue, step: 0.5, def: -18);
        var buses = new List<Choice> { new("", "No bus (outputs only)") };
        buses.Add(new(_mix.Main.Id, NameOf(_mix.Main)));
        foreach (var kind in new[] { StripKind.Mix, StripKind.Matrix, StripKind.Group, StripKind.Fx })
            foreach (var b in _mix.BusesOf(kind)) buses.Add(new(b.Id, NameOf(b)));
        ToneBus = new ChoiceParam("tone.bus", "Play into", buses, m => m.Tone.Bus, Queue);
        OnPropertyChanged(nameof(ToneKind));
        OnPropertyChanged(nameof(ToneFreq));
        OnPropertyChanged(nameof(ToneLevel));
        OnPropertyChanged(nameof(ToneBus));
        RefreshTone();
    }

    private void RefreshTone()
    {
        ToneKind?.Refresh(_mix);
        ToneFreq?.Refresh(_mix);
        ToneLevel?.Refresh(_mix);
        ToneBus?.Refresh(_mix);
        OnPropertyChanged(nameof(ToneOn));
        OnPropertyChanged(nameof(ToneIsSine));
        OnPropertyChanged(nameof(ToneOutputsText));
        OnPropertyChanged(nameof(ToneStateText));
    }

    /// <summary>Playing a test signal into a live bus is loud: ask first.</summary>
    [RelayCommand]
    private void AskTone()
    {
        if (_mix.Tone.Bus.Length == 0)
        {
            Notice = "Pick the bus to play the test signal into first.";
            return;
        }
        ToneConfirming = true;
    }

    [RelayCommand]
    private void StartTone()
    {
        ToneConfirming = false;
        Queue("tone.on", true);
        RefreshTone();
    }

    [RelayCommand]
    private void StopTone()
    {
        ToneConfirming = false;
        Queue("tone.on", false);
        RefreshTone();
    }

    public string ToneConfirmText => _mix.Find(_mix.Tone.Bus) is { } b
        ? $"This plays {(_mix.Tone.Kind == Nexora.Audio.Mix.ToneKind.Sine ? $"a {_mix.Tone.Freq:0} Hz tone" : $"{_mix.Tone.Kind.ToString().ToLowerInvariant()} noise")} at {_mix.Tone.Level:0} dB into {NameOf(b)} and every speaker it feeds. Turn the speakers down first if people are in the room."
        : "";

    partial void OnToneConfirmingChanged(bool value) => OnPropertyChanged(nameof(ToneConfirmText));

    // ---- Keeping it all up to date ------------------------------------------------------------------------

    private void BuildRouting()
    {
        BuildRouteMap();
        BuildInputs();
        BuildTone();
        OnPropertyChanged(nameof(AltSummary));
    }

    private void RefreshRouting()
    {
        BuildRouteMap();
        foreach (var r in InputRows) r.Refresh(_mix, this);
        RefreshTone();
        OnPropertyChanged(nameof(AltSummary));
    }
}

/// <summary>One channel on the input patch: its input, alternate, delay and direct out.</summary>
public sealed partial class InputRow : ObservableObject
{
    public InputRow(NexAudioViewModel owner, int number, IReadOnlyList<Choice> inputs, IReadOnlyList<Choice> taps, IReadOnlyList<Choice> outs)
    {
        _owner = owner;
        Number = number;
        var id = $"ch{number}";
        Strip? S(MixState m) => m.Find(id);
        Main = new ChoiceParam($"{id}.input", "Input", inputs, m => S(m)?.Input.ToString(CultureInfo.InvariantCulture), owner.Queue, asInt: true);
        Alt = new ChoiceParam($"{id}.altInput", "Alternate", inputs, m => S(m)?.AltInput.ToString(CultureInfo.InvariantCulture), owner.Queue, asInt: true);
        UseAlt = new ToggleParam($"{id}.useAlt", "Use alternate", m => S(m)?.UseAlt ?? false, owner.Queue);
        Delay = new SliderParam($"{id}.delayMs", "Delay", 0, MixParams.MaxDelayMs, "ms", m => S(m)?.DelayMs, owner.Queue, step: 0.1, def: 0);
        DirectTap = new ChoiceParam($"{id}.directTap", "Direct out from", taps, m => S(m)?.DirectTap.ToString(), owner.Queue);
        Outs = outs;
    }

    private readonly NexAudioViewModel _owner;
    public int Number { get; }
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IBrush _colour = Brushes.Gray;
    [ObservableProperty] private bool _hasAlt;
    public ChoiceParam Main { get; }
    public ChoiceParam Alt { get; }
    public ToggleParam UseAlt { get; }
    public SliderParam Delay { get; }
    public ChoiceParam DirectTap { get; }
    public IReadOnlyList<Choice> Outs { get; }
    [ObservableProperty] private Choice? _directOut;
    private bool _refreshing;

    partial void OnDirectOutChanged(Choice? value)
    {
        if (_refreshing || value is null) return;
        _owner.PatchDirectOut(Number, int.Parse(value.Value, CultureInfo.InvariantCulture));
    }

    public void Refresh(MixState m, NexAudioViewModel owner)
    {
        foreach (var p in new MixParam[] { Main, Alt, UseAlt, Delay, DirectTap }) p.Refresh(m);
        if (m.Find($"ch{Number}") is not { } s) return;
        _refreshing = true;
        Name = owner.NameOf(s);
        var c = owner.StripColour(s);
        Colour = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        HasAlt = s.AltInput > 0;
        var on = m.Outputs.FirstOrDefault(o => o.Source.Equals($"ch:{Number}", StringComparison.OrdinalIgnoreCase));
        DirectOut = Outs.FirstOrDefault(o => o.Value == (on?.Number ?? 0).ToString(CultureInfo.InvariantCulture)) ?? Outs[0];
        _refreshing = false;
    }
}

public sealed class RoutingRow(RoutingInfo info, NexAudioViewModel owner)
{
    public string Id => info.Id;
    public string Name => info.Name;
    public bool BuiltIn => info.BuiltIn;
    public bool CanChange => !info.BuiltIn;
    public string Detail => info.BuiltIn
        ? info.Notes
        : $"Saved {info.SavedUtc.ToLocalTime().ToString("ddd d MMM, h:mm tt", CultureInfo.CurrentCulture)}{(info.SavedBy.Length > 0 ? " by " + info.SavedBy : "")}";
    public IRelayCommand PreviewCommand { get; } = new RelayCommand(() => owner.PreviewRouting(info.Id));
    public IRelayCommand RecallCommand { get; } = new RelayCommand(() => owner.RecallRouting(info.Id));
    public IRelayCommand UpdateCommand { get; } = new RelayCommand(() => owner.SaveRoutingOver(info));
    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => owner.DeleteRouting(info.Id));
}
