using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.App.ViewModels;

// ---- Settings shown as sliders, switches and lists ------------------------------------------

/// <summary>Something on a NEX-Audio page that edits one mix setting by its path.</summary>
public abstract partial class MixParam : ObservableObject
{
    protected MixParam(string path, string label, Action<string, object> send)
    {
        Path = path;
        Label = label;
        Send = send;
    }

    public string Path { get; protected set; }
    public string Label { get; }
    protected Action<string, object> Send { get; }
    protected bool Refreshing;

    /// <summary>Reads the value from the mix again (after a change from elsewhere).</summary>
    public abstract void Refresh(MixState mix);
}

/// <summary>A number with a slider (straight or logarithmic, like frequencies).</summary>
public sealed partial class SliderParam : MixParam
{
    private readonly Func<MixState, double?> _get;

    public SliderParam(string path, string label, double min, double max, string unit, Func<MixState, double?> get, Action<string, object> send, bool log = false, double step = 0, double? def = null)
        : base(path, label, send)
    {
        _get = get;
        _default = def;
        Min = min;
        Max = max;
        Unit = unit;
        IsLog = log && min > 0;
        Step = step;
    }

    public SliderParam(ParamInfo p, string path, Func<MixState, double?> get, Action<string, object> send)
        : this(path, p.Label, p.Min, p.Max, p.Unit, get, send, p.Log, p.Step)
    {
        _info = p;
    }

    private readonly ParamInfo? _info;
    private readonly double? _default;
    public double Min { get; }
    public double Max { get; }
    public string Unit { get; }
    public bool IsLog { get; }
    public double Step { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(Position))]
    private double _value;

    /// <summary>Where the slider sits, 0 to 1000.</summary>
    public double Position
    {
        get => IsLog ? 1000 * Math.Log(Math.Clamp(Value, Min, Max) / Min) / Math.Log(Max / Min) : 1000 * (Math.Clamp(Value, Min, Max) - Min) / (Max - Min);
        set
        {
            var t = Math.Clamp(value / 1000, 0, 1);
            var v = IsLog ? Min * Math.Pow(Max / Min, t) : Min + t * (Max - Min);
            v = Step > 0 ? Math.Round(v / Step) * Step : Unit is "Hz" ? Math.Round(v, v < 100 ? 1 : 0) : Math.Round(v, 2);
            Value = Math.Clamp(v, Min, Max);
        }
    }

    public string Text => _info?.Format(Value) ?? Unit switch
    {
        "dB" => Value <= Db.Floor ? "-∞" : $"{Value:+0.0;-0.0;0.0} dB",
        "Hz" => Value >= 1000 ? $"{Value / 1000:0.##} kHz" : $"{Value:0} Hz",
        "ms" => $"{Value:0.#} ms",
        "%" => $"{Value:0}%",
        ":1" => $"{Value:0.#}:1",
        "" => $"{Value:0.##}",
        _ => $"{Value:0.##} {Unit}",
    };

    partial void OnValueChanged(double value)
    {
        if (!Refreshing) Send(Path, value);
    }

    public override void Refresh(MixState mix)
    {
        Refreshing = true;
        Value = _get(mix) ?? Min;
        Refreshing = false;
    }

    [RelayCommand]
    private void Reset()
    {
        if (_info is not null) Value = _info.Default;
        else if (_default is { } d) Value = d;
        else if (Unit == "dB" && Min < 0 && Max > 0) Value = 0;
    }
}

public sealed partial class ToggleParam : MixParam
{
    private readonly Func<MixState, bool> _get;

    public ToggleParam(string path, string label, Func<MixState, bool> get, Action<string, object> send) : base(path, label, send) => _get = get;

    [ObservableProperty] private bool _value;

    partial void OnValueChanged(bool value)
    {
        if (!Refreshing) Send(Path, value);
    }

    public override void Refresh(MixState mix)
    {
        Refreshing = true;
        Value = _get(mix);
        Refreshing = false;
    }
}

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ChoiceParam : MixParam
{
    private readonly Func<MixState, string?> _get;

    public ChoiceParam(string path, string label, IReadOnlyList<Choice> options, Func<MixState, string?> get, Action<string, object> send, bool asNumber = false)
        : base(path, label, send)
    {
        _get = get;
        Options = options;
        _asNumber = asNumber;
    }

    private readonly bool _asNumber;
    public IReadOnlyList<Choice> Options { get; }
    [ObservableProperty] private Choice? _selected;

    partial void OnSelectedChanged(Choice? value)
    {
        if (Refreshing || value is null) return;
        if (_asNumber && double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) Send(Path, n);
        else Send(Path, value.Value);
    }

    public override void Refresh(MixState mix)
    {
        Refreshing = true;
        var v = _get(mix);
        Selected = Options.FirstOrDefault(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)) ?? Options.FirstOrDefault();
        Refreshing = false;
    }
}

/// <summary>A titled group of settings on the channel page.</summary>
public sealed partial class ParamSection(string title, string? note = null) : ObservableObject
{
    public string Title { get; } = title;
    public string? Note { get; } = note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public ObservableCollection<MixParam> Items { get; } = [];
    /// <summary>The on/off switch shown in the section's header (EQ on, gate on…), if any.</summary>
    public ToggleParam? Switch { get; init; }
    public bool HasSwitch => Switch is not null;
}

// ---- Mixer strips ----------------------------------------------------------------------------

public sealed partial class StripView : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public StripView(NexAudioViewModel owner, Strip s, int meterIndex, int meterIndexR)
    {
        _owner = owner;
        Id = s.Id;
        Kind = s.Kind;
        MeterIndex = meterIndex;
        MeterIndexR = meterIndexR;
        Update(s);
    }

    public string Id { get; }
    public StripKind Kind { get; }
    public int MeterIndex { get; }
    public int MeterIndexR { get; }
    public bool IsChannel => Kind == StripKind.Channel;
    public bool CanSolo => Kind is not (StripKind.Main or StripKind.Stream);

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private IBrush? _colour;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private bool _solo;
    [ObservableProperty] private double _pan;
    [ObservableProperty] private bool _stereo;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _eqOn;
    [ObservableProperty] private bool _dynOn;
    [ObservableProperty] private bool _rackOn;
    [ObservableProperty] private bool _faderEnabled = true;
    [ObservableProperty] private string _layerNote = "";
    /// <summary>The strip's filters and EQ, for the small curve at the top of the strip.</summary>
    [ObservableProperty] private FilterSettings? _filters;
    [ObservableProperty] private double _gateGr;
    [ObservableProperty] private double _compGr;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FaderText))] private double _fader;
    private bool _refreshing;

    public string FaderText => Fader <= Db.Floor ? "-∞" : $"{Fader:+0.0;-0.0;0.0}";
    /// <summary>The pan as a knob position, 0 (left) to 1000 (right).</summary>
    public double PanPosition
    {
        get => (Pan + 1) * 500;
        set => Pan = Math.Round(Math.Clamp(value / 500 - 1, -1, 1), 2);
    }

    public string PanText => Math.Abs(Pan) < 0.01 ? "C" : Pan < 0 ? $"L{-Pan * 100:0}" : $"R{Pan * 100:0}";

    public void Update(Strip s)
    {
        _refreshing = true;
        Name = _owner.NameOf(s);
        Number = s.Kind switch
        {
            StripKind.Channel => s.Number.ToString(CultureInfo.InvariantCulture),
            StripKind.Main => "L R",
            StripKind.Mono => "M",
            StripKind.Stream => "STREAM",
            _ => $"{MixState.KindLabel(s.Kind).ToUpperInvariant()} {s.Number}",
        };
        Colour = s.Colour is { Length: > 0 } c && Color.TryParse(c, out var col) ? new SolidColorBrush(col) : null;
        Mute = s.Mute;
        Solo = s.Solo;
        Pan = s.Pan;
        Stereo = s.Stereo;
        EqOn = !s.Eq.IsFlat || s.Hpf.On;
        DynOn = s.Gate.On || s.Comp.On;
        RackOn = s.Rack.Any(r => !r.Bypass);
        var f = FilterSettings.From(s);
        if (Filters is null || !SameFilters(Filters, f)) Filters = f;
        var layer = _owner.LayerBus;
        if (layer is null || s.Id == layer.Id)
        {
            Fader = s.Fader;
            FaderEnabled = true;
            LayerNote = "";
        }
        else
        {
            var can = Routing.CanSend(s, layer);
            var send = s.SendTo(layer.Id);
            Fader = can && send is { On: true } ? send.Level : Db.Floor;
            FaderEnabled = can;
            LayerNote = can ? send?.Tap == SendTap.Pre ? "send · pre" : "send · post" : "";
        }
        OnPropertyChanged(nameof(PanText));
        OnPropertyChanged(nameof(PanPosition));
        _refreshing = false;
    }

    partial void OnFaderChanged(double value)
    {
        if (_refreshing) return;
        var layer = _owner.LayerBus;
        if (layer is null || layer.Id == Id) _owner.Queue($"{Id}.fader", value);
        else
        {
            _owner.Queue($"{Id}.send.{layer.Id}.level", value);
            _owner.Queue($"{Id}.send.{layer.Id}.on", true);
        }
    }

    partial void OnPanChanged(double value)
    {
        OnPropertyChanged(nameof(PanText));
        OnPropertyChanged(nameof(PanPosition));
        if (!_refreshing) _owner.Queue($"{Id}.pan", Math.Round(value, 2));
    }

    [RelayCommand] private void ToggleMute() => _owner.Queue($"{Id}.mute", !Mute);
    [RelayCommand] private void ToggleSolo() => _owner.Queue($"{Id}.solo", !Solo);
    [RelayCommand] private void Select() => _owner.SelectStrip(Id, true);
    [RelayCommand] private void CentrePan() => Pan = 0;
    [RelayCommand] private void OpenSection(string section) => _owner.OpenStripSection(Id, section);

    private static bool SameFilters(FilterSettings a, FilterSettings b) =>
        System.Text.Json.JsonSerializer.Serialize(a, MixState.Json) == System.Text.Json.JsonSerializer.Serialize(b, MixState.Json);
}

public sealed record LayerChoice(string? BusId, string Label)
{
    public override string ToString() => Label;
}

// ---- Racks ---------------------------------------------------------------------------------------

public sealed partial class RackItemView : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public RackItemView(NexAudioViewModel owner, string stripId, RackItem item, int index, int count)
    {
        _owner = owner;
        StripId = stripId;
        Id = item.Id;
        Type = item.Type;
        Name = Effects.NameOf(item);
        IsPlugin = item.Type == "plugin";
        Vendor = item.Plugin?.Vendor ?? "";
        Description = Effects.Get(item.Type)?.Description ?? (IsPlugin ? "A VST3 plugin, running in its own safe space." : "");
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        Bypass = new ToggleParam($"{stripId}.rack.{item.Id}.bypass", "Bypass", m => Find(m)?.Bypass ?? false, owner.Queue);
        if (Effects.Get(item.Type) is { } info)
        {
            foreach (var c in info.Choices)
                Params.Add(new ChoiceParam($"{stripId}.rack.{item.Id}.t.{c.Key}", c.Label, c.Options.Select(o => new Choice(o, Cap(o))).ToList(),
                    m => Find(m)?.T(c.Key, c.Default), owner.Queue));
            foreach (var p in info.Params)
                Params.Add(new SliderParam(p, $"{stripId}.rack.{item.Id}.p.{p.Key}", m => Find(m)?.P(p.Key, p.Default), owner.Queue));
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private RackItem? Find(MixState m) => m.Find(StripId)?.Rack.FirstOrDefault(r => r.Id == Id);

    public string StripId { get; }
    public string Id { get; }
    public string Type { get; }
    public string Name { get; }
    public string Vendor { get; }
    public string Description { get; }
    public bool IsPlugin { get; }
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }
    public ToggleParam Bypass { get; }
    public ObservableCollection<MixParam> Params { get; } = [];
    public IEnumerable<SliderParam> Knobs => Type == "eq31" ? [] : Params.OfType<SliderParam>();
    public IEnumerable<ChoiceParam> Choices => Params.OfType<ChoiceParam>();
    public bool IsGraphicEq => Type == "eq31";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _expanded = true;

    public void Refresh(MixState m)
    {
        Bypass.Refresh(m);
        foreach (var p in Params) p.Refresh(m);
    }

    [RelayCommand] private void Remove() => _owner.RackCommand("rack.remove", StripId, Id);
    [RelayCommand] private void MoveUp() => _owner.RackCommand("rack.move", StripId, Id, -1);
    [RelayCommand] private void MoveDown() => _owner.RackCommand("rack.move", StripId, Id, +1);
    [RelayCommand] private void OpenEditor() => _owner.RackCommand("plugin.editor", StripId, Id);
    [RelayCommand] private void Retry() => _owner.RackCommand("plugin.retry", StripId, Id);
}

public sealed record AddChoice(string Type, string? Style, string Label, string Group, Nexora.Audio.Plugins.PluginInfo? Plugin = null)
{
    public override string ToString() => Label;
}

// ---- Outputs ---------------------------------------------------------------------------------------

public sealed partial class OutputRow : ObservableObject
{
    public OutputRow(NexAudioViewModel owner, int number, IReadOnlyList<Choice> sources)
    {
        Number = number;
        var p = $"out.{number}";
        OutputPatch? O(MixState m) => number - 1 < m.Outputs.Count ? m.Outputs[number - 1] : null;
        Source = new ChoiceParam($"{p}.source", "Plays", sources, m => O(m)?.Source, owner.Queue);
        Level = new SliderParam($"{p}.level", "Level", -40, 10, "dB", m => O(m)?.Level, owner.Queue);
        Delay = new SliderParam($"{p}.delayMs", "Delay", 0, 200, "ms", m => O(m)?.DelayMs, owner.Queue, step: 0.1);
        Mute = new ToggleParam($"{p}.mute", "Mute", m => O(m)?.Mute ?? false, owner.Queue);
        Limit = new ToggleParam($"{p}.limit", "Limiter", m => O(m)?.Limit ?? false, owner.Queue);
        HpfOn = new ToggleParam($"{p}.hpf.on", "Low cut", m => O(m)?.Hpf.On ?? false, owner.Queue);
        HpfFreq = new SliderParam($"{p}.hpf.freq", "Low cut", 20, 300, "Hz", m => O(m)?.Hpf.Freq, owner.Queue, log: true);
        _owner = owner;
    }

    private readonly NexAudioViewModel _owner;
    public int Number { get; }
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);
    public ChoiceParam Source { get; }
    public SliderParam Level { get; }
    public SliderParam Delay { get; }
    public ToggleParam Mute { get; }
    public ToggleParam Limit { get; }
    public ToggleParam HpfOn { get; }
    public SliderParam HpfFreq { get; }
    [ObservableProperty] private string _name = "";
    private bool _refreshing;

    partial void OnNameChanged(string value)
    {
        if (!_refreshing) _owner.Queue($"out.{Number}.name", value.Trim());
    }

    public void Refresh(MixState m)
    {
        foreach (var p in new MixParam[] { Source, Level, Delay, Mute, Limit, HpfOn, HpfFreq }) p.Refresh(m);
        _refreshing = true;
        Name = Number - 1 < m.Outputs.Count ? m.Outputs[Number - 1].Name : "";
        _refreshing = false;
    }
}

// ---- Scenes ---------------------------------------------------------------------------------------

public sealed class SceneRow(SavedMixInfo info, bool snapshot, bool current, NexAudioViewModel owner)
{
    public string Id => info.Id;
    public string Name => info.Name;
    public bool IsSnapshot => snapshot;
    public bool IsCurrent => current;
    public string Detail
    {
        get
        {
            var when = info.SavedUtc.ToLocalTime().ToString("ddd d MMM, h:mm tt", CultureInfo.CurrentCulture);
            var by = string.IsNullOrEmpty(info.SavedBy) ? "" : $" by {info.SavedBy}";
            var scope = info.Scope == RecallScope.All ? "" : " · " + NexAudioViewModel.ScopeText(info.Scope);
            var fade = info.FadeSeconds > 0 ? $" · {info.FadeSeconds:0.#} s fade" : "";
            return $"Saved {when}{by}{scope}{fade}";
        }
    }
    public IRelayCommand RecallCommand { get; } = new RelayCommand(() => owner.RecallScene(info.Id, snapshot));
    public IRelayCommand UpdateCommand { get; } = new RelayCommand(() => owner.UpdateScene(info, snapshot));
    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => owner.DeleteScene(info.Id, snapshot));
}
