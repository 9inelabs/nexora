using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.App.ViewModels;

/// <summary>The parts of the channel workspace, in the order of the section rail.</summary>
public enum ChannelSection
{
    Home,
    Input,
    Gate,
    Eq,
    Dynamics,
    Geq,
    Effects,
    Sends,
}

/// <summary>One entry in the channel workspace's section rail, with a short live summary under its name.</summary>
public sealed partial class ChannelRailItem(ChannelSection section, string title, NexAudioViewModel owner) : ObservableObject
{
    public ChannelSection Section { get; } = section;
    public string Title { get; } = title;

    /// <summary>The section's own colour (input blue, gate teal, EQ violet, compressor orange, effects pink, sends gold).</summary>
    public Avalonia.Media.IBrush Tone { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(section switch
    {
        ChannelSection.Input => "#6CB4F5",
        ChannelSection.Gate => "#4FD1C5",
        ChannelSection.Eq or ChannelSection.Geq => "#A78BFA",
        ChannelSection.Dynamics => "#F59E5B",
        ChannelSection.Effects => "#F472B6",
        ChannelSection.Sends => "#E2BE76",
        _ => "#F2F2F0",
    }));

    public Avalonia.Media.IBrush TitleBrush => IsCurrent ? Tone : Avalonia.Media.Brushes.WhiteSmoke;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(TitleBrush));
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isCurrent;

    [RelayCommand] private void Open() => owner.ChannelPage = Section;
}

/// <summary>One parametric EQ band, shown as a column of knobs.</summary>
public sealed partial class EqBandView : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public EqBandView(NexAudioViewModel owner, string id, int band, Action<string, object> q)
    {
        _owner = owner;
        Band = band;
        EqBand? B(MixState m) => m.Find(id) is { } st && band < st.Eq.Bands.Count ? st.Eq.Bands[band] : null;
        On = new ToggleParam($"{id}.eq.{band}.on", "On", m => B(m)?.On ?? false, q);
        Shape = new ChoiceParam($"{id}.eq.{band}.shape", "Shape", Shapes, m => B(m)?.Shape.ToString(), q);
        Freq = new SliderParam($"{id}.eq.{band}.freq", "Freq", 20, 20000, "Hz", m => B(m)?.Freq, q, log: true);
        Gain = new SliderParam($"{id}.eq.{band}.gain", "Gain", -18, 18, "dB", m => B(m)?.Gain, q, step: 0.5, def: 0);
        Q = new SliderParam($"{id}.eq.{band}.q", "Width (Q)", 0.3, 10, "", m => B(m)?.Q, q, log: true, def: 1);
    }

    public static readonly IReadOnlyList<Choice> Shapes =
    [
        new("Peak", "Bell"), new("LowShelf", "Low shelf"), new("HighShelf", "High shelf"), new("Notch", "Notch"),
        new("HighPass", "Low cut"), new("LowPass", "High cut"),
    ];

    public int Band { get; }
    public string Number => (Band + 1).ToString(CultureInfo.InvariantCulture);
    public ToggleParam On { get; }
    public ChoiceParam Shape { get; }
    public SliderParam Freq { get; }
    public SliderParam Gain { get; }
    public SliderParam Q { get; }
    [ObservableProperty] private bool _isSelected;

    public bool HasGain => Shape.Selected?.Value is "Peak" or "LowShelf" or "HighShelf";
    public string ShapeText => Shape.Selected?.Label.ToUpperInvariant() ?? "";

    public void Refresh(MixState m)
    {
        On.Refresh(m);
        Shape.Refresh(m);
        Freq.Refresh(m);
        Gain.Refresh(m);
        Q.Refresh(m);
        OnPropertyChanged(nameof(HasGain));
        OnPropertyChanged(nameof(ShapeText));
    }

    [RelayCommand] private void Select() => _owner.SelectBand(Band);
}

/// <summary>One send from the open strip to a bus.</summary>
public sealed partial class SendView : ObservableObject
{
    private readonly string _path;
    private readonly Action<string, object> _q;
    private readonly Func<MixState, Send?> _get;
    private readonly SendTap _default;
    private bool _refreshing;

    public SendView(string id, Strip bus, string name, Action<string, object> q)
    {
        var bid = bus.Id;
        _path = $"{id}.send.{bid}";
        _q = q;
        _get = m => m.Find(id)?.SendTo(bid);
        _default = bus.Kind == StripKind.Mix ? SendTap.Pre : SendTap.Post;
        Name = name;
        Kind = MixState.KindLabel(bus.Kind).ToUpperInvariant() + (bus.Number > 0 ? $" {bus.Number}" : "");
        Tone = new Avalonia.Media.SolidColorBrush(NexAudioViewModel.ColourOf(bus));
        Level = new SliderParam($"{_path}.level", name, Db.Floor, 10, "dB", m => _get(m) is { } s ? s.Level : Db.Floor, q, step: 0.5, def: 0);
        On = new ToggleParam($"{_path}.on", "On", m => _get(m)?.On ?? false, q);
    }

    public string Name { get; }
    public string Kind { get; }
    public Avalonia.Media.IBrush Tone { get; }
    public SliderParam Level { get; }
    public ToggleParam On { get; }
    [ObservableProperty] private bool _pre;

    partial void OnPreChanged(bool value)
    {
        if (!_refreshing) _q($"{_path}.tap", value ? "Pre" : "Post");
    }

    public void Refresh(MixState m)
    {
        Level.Refresh(m);
        On.Refresh(m);
        _refreshing = true;
        Pre = (_get(m)?.Tap ?? _default) == SendTap.Pre;
        _refreshing = false;
    }
}

/// <summary>One of the eight rack places on the Effects page.</summary>
public sealed partial class RackSlotView(int index, NexAudioViewModel owner) : ObservableObject
{
    public int Index { get; } = index;
    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _name = "Empty";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsActive))] private bool _isBypassed;
    [ObservableProperty] private bool _isSelected;
    public string? ItemId { get; set; }
    public bool IsActive => !IsEmpty && !IsBypassed;

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(IsActive));

    [RelayCommand] private void Select() => owner.SelectRackSlot(Index);

    /// <summary>From the home page: opens the Effects page on this place.</summary>
    [RelayCommand]
    private void Open()
    {
        owner.SelectRackSlot(Index);
        owner.ChannelPage = ChannelSection.Effects;
    }
}

/// <summary>Every setting of the open strip, as knobs and switches for the channel workspace.</summary>
public sealed class ChannelControls
{
    public ChannelControls(NexAudioViewModel owner, MixState mix, Strip s)
    {
        var id = s.Id;
        Strip? S(MixState m) => m.Find(id);
        Action<string, object> q = owner.Queue;
        IsChannel = s.Kind == StripKind.Channel;

        Preamp = new SliderParam($"{id}.preamp", "X32 gain", -12, 60, "dB", m => S(m)?.Preamp ?? 0, q, step: 0.5, def: 0);
        Phantom = new ToggleParam($"{id}.phantom", "+48 V", m => S(m)?.Phantom ?? false, q);
        Trim = new SliderParam($"{id}.trim", "Trim", -24, 24, "dB", m => S(m)?.Trim, q, step: 0.5, def: 0);
        Invert = new ToggleParam($"{id}.invert", "Polarity flip", m => S(m)?.Invert ?? false, q);
        Delay = new SliderParam($"{id}.delayMs", "Delay", 0, 100, "ms", m => S(m)?.DelayMs, q, step: 0.1, def: 0);
        HpfOn = new ToggleParam($"{id}.hpf.on", "Low cut", m => S(m)?.Hpf.On ?? false, q);
        HpfFreq = new SliderParam($"{id}.hpf.freq", "Low cut", 20, 600, "Hz", m => S(m)?.Hpf.Freq, q, log: true, def: 80);
        HpfSlope = new ChoiceParam($"{id}.hpf.slope", "Slope", [new("12", "12 dB"), new("24", "24 dB")], m => S(m)?.Hpf.Slope.ToString(CultureInfo.InvariantCulture), q, asNumber: true);
        LpfOn = new ToggleParam($"{id}.lpf.on", "High cut", m => S(m)?.Lpf.On ?? false, q);
        LpfFreq = new SliderParam($"{id}.lpf.freq", "High cut", 2000, 20000, "Hz", m => S(m)?.Lpf.Freq, q, log: true, def: 18000);

        GateOn = new ToggleParam($"{id}.gate.on", "On", m => S(m)?.Gate.On ?? false, q);
        GateThreshold = new SliderParam($"{id}.gate.threshold", "Threshold", -80, 0, "dB", m => S(m)?.Gate.Threshold, q, step: 0.5, def: -50);
        GateRange = new SliderParam($"{id}.gate.range", "Range", 0, 80, "dB down", m => S(m)?.Gate.Range, q, step: 1, def: 40);
        GateAttack = new SliderParam($"{id}.gate.attack", "Attack", 0.05, 50, "ms", m => S(m)?.Gate.Attack, q, log: true, def: 0.5);
        GateHold = new SliderParam($"{id}.gate.hold", "Hold", 0, 500, "ms", m => S(m)?.Gate.Hold, q, step: 1, def: 40);
        GateRelease = new SliderParam($"{id}.gate.release", "Release", 5, 2000, "ms", m => S(m)?.Gate.Release, q, log: true, def: 150);
        GateKey = new SliderParam($"{id}.gate.keyFilter", "Key filter", 0, 400, "Hz", m => S(m)?.Gate.KeyFilter, q, step: 5, def: 0);

        CompOn = new ToggleParam($"{id}.comp.on", "On", m => S(m)?.Comp.On ?? false, q);
        CompStyle = new ChoiceParam($"{id}.comp.style", "Style", [new("Clean", "Clean"), new("Opto", "Opto"), new("Fet", "FET"), new("Glue", "Glue")], m => S(m)?.Comp.Style.ToString(), q);
        CompThreshold = new SliderParam($"{id}.comp.threshold", "Threshold", -60, 0, "dB", m => S(m)?.Comp.Threshold, q, step: 0.5, def: -18);
        CompRatio = new SliderParam($"{id}.comp.ratio", "Ratio", 1, 20, ":1", m => S(m)?.Comp.Ratio, q, log: true, def: 3);
        CompAttack = new SliderParam($"{id}.comp.attack", "Attack", 0.05, 200, "ms", m => S(m)?.Comp.Attack, q, log: true, def: 10);
        CompRelease = new SliderParam($"{id}.comp.release", "Release", 10, 2000, "ms", m => S(m)?.Comp.Release, q, log: true, def: 120);
        CompKnee = new SliderParam($"{id}.comp.knee", "Knee", 0, 24, "dB", m => S(m)?.Comp.Knee, q, step: 0.5, def: 6);
        CompMakeup = new SliderParam($"{id}.comp.makeup", "Make-up", -12, 24, "dB", m => S(m)?.Comp.Makeup, q, step: 0.5, def: 0);
        CompAuto = new ToggleParam($"{id}.comp.autoMakeup", "Auto make-up", m => S(m)?.Comp.AutoMakeup ?? false, q);
        CompMix = new SliderParam($"{id}.comp.mix", "Mix", 0, 100, "%", m => S(m)?.Comp.Mix, q, step: 1, def: 100);
        CompKey = new SliderParam($"{id}.comp.keyFilter", "Key filter", 0, 400, "Hz", m => S(m)?.Comp.KeyFilter, q, step: 5, def: 0);

        EqOn = new ToggleParam($"{id}.eq.on", "On", m => S(m)?.Eq.On ?? false, q);

        DriveOn = new ToggleParam($"{id}.drive.on", "Drive", m => S(m)?.Drive.On ?? false, q);
        DriveStyle = new ChoiceParam($"{id}.drive.style", "Colour", [new("Warm", "Warm"), new("Edge", "Edge")], m => S(m)?.Drive.Style.ToString(), q);
        DriveAmount = new SliderParam($"{id}.drive.drive", "Drive", 0, 24, "dB", m => S(m)?.Drive.Drive, q, step: 0.5, def: 6);
        DriveMix = new SliderParam($"{id}.drive.mix", "Mix", 0, 100, "%", m => S(m)?.Drive.Mix, q, step: 1, def: 100);
        DriveOutput = new SliderParam($"{id}.drive.output", "Output", -12, 12, "dB", m => S(m)?.Drive.Output, q, step: 0.5, def: 0);

        DynEqOn = new ToggleParam($"{id}.dynEq.on", "On", m => S(m)?.DynEq.On ?? false, q);
        for (var b = 0; b < s.DynEq.Bands.Count; b++) DynBands.Add(new DynEqBandView(id, b, q));

        GateMode = new ChoiceParam($"{id}.gate.mode", "Mode", [new("Gate", "Gate"), new("Ducker", "Ducker")], m => S(m)?.Gate.Mode.ToString(), q);
        var keys = new List<Choice> { new("", "Itself") };
        keys.AddRange(mix.Channels.Where(c => c.Id != id).Select(c => new Choice(c.Id, $"{c.Number} · {owner.NameOf(c)}")));
        GateKeySource = new ChoiceParam($"{id}.gate.key", "Listens to", keys, m => S(m)?.Gate.Key ?? "", q);
        CompKeySource = new ChoiceParam($"{id}.comp.key", "Listens to", keys, m => S(m)?.Comp.Key ?? "", q);
        Order = new ChoiceParam($"{id}.order", "Order", [new("EqFirst", "EQ → Dynamics"), new("DynamicsFirst", "Dynamics → EQ")], m => S(m)?.Order.ToString(), q);
        for (var b = 0; b < s.Eq.Bands.Count; b++) Bands.Add(new EqBandView(owner, id, b, q));

        var buses = mix.Buses.Where(b => Routing.CanSend(s, b)).OrderBy(b => Routing.Stage(b.Kind)).ThenBy(b => b.Number).ToList();
        foreach (var b in buses) Sends.Add(new SendView(id, b, owner.NameOf(b), q));

        Refresh(mix);
    }

    public bool IsChannel { get; }

    public SliderParam Preamp { get; }
    public ToggleParam Phantom { get; }
    public SliderParam Trim { get; }
    public ToggleParam Invert { get; }
    public SliderParam Delay { get; }
    public ToggleParam HpfOn { get; }
    public SliderParam HpfFreq { get; }
    public ChoiceParam HpfSlope { get; }
    public ToggleParam LpfOn { get; }
    public SliderParam LpfFreq { get; }

    public ToggleParam GateOn { get; }
    public SliderParam GateThreshold { get; }
    public SliderParam GateRange { get; }
    public SliderParam GateAttack { get; }
    public SliderParam GateHold { get; }
    public SliderParam GateRelease { get; }
    public SliderParam GateKey { get; }

    public ToggleParam CompOn { get; }
    public ChoiceParam CompStyle { get; }
    public SliderParam CompThreshold { get; }
    public SliderParam CompRatio { get; }
    public SliderParam CompAttack { get; }
    public SliderParam CompRelease { get; }
    public SliderParam CompKnee { get; }
    public SliderParam CompMakeup { get; }
    public ToggleParam CompAuto { get; }
    public SliderParam CompMix { get; }
    public SliderParam CompKey { get; }

    public ToggleParam EqOn { get; }

    public ToggleParam DriveOn { get; }
    public ChoiceParam DriveStyle { get; }
    public SliderParam DriveAmount { get; }
    public SliderParam DriveMix { get; }
    public SliderParam DriveOutput { get; }

    public ToggleParam DynEqOn { get; }
    public ObservableCollection<DynEqBandView> DynBands { get; } = [];

    public ChoiceParam GateMode { get; }
    public ChoiceParam GateKeySource { get; }
    public ChoiceParam CompKeySource { get; }
    public ChoiceParam Order { get; }
    public bool IsDucker => GateMode.Selected?.Value == "Ducker";
    public ObservableCollection<EqBandView> Bands { get; } = [];
    public ObservableCollection<SendView> Sends { get; } = [];
    public bool HasSends => Sends.Count > 0;

    /// <summary>The make-up gain the compressor really uses (its own figure when automatic make-up is on).</summary>
    public double EffectiveMakeup(MixState m) => m.Find(Preamp.Path.Split('.')[0])?.Comp is { } c
        ? c.AutoMakeup ? Compressor.AutoMakeup(c) : c.Makeup
        : 0;

    private IEnumerable<MixParam> All()
    {
        yield return Preamp; yield return Phantom; yield return Trim; yield return Invert; yield return Delay;
        yield return HpfOn; yield return HpfFreq; yield return HpfSlope; yield return LpfOn; yield return LpfFreq;
        yield return GateOn; yield return GateThreshold; yield return GateRange; yield return GateAttack; yield return GateHold; yield return GateRelease; yield return GateKey;
        yield return CompOn; yield return CompStyle; yield return CompThreshold; yield return CompRatio; yield return CompAttack; yield return CompRelease;
        yield return CompKnee; yield return CompMakeup; yield return CompAuto; yield return CompMix; yield return CompKey;
        yield return EqOn;
        yield return DriveOn; yield return DriveStyle; yield return DriveAmount; yield return DriveMix; yield return DriveOutput;
        yield return DynEqOn; yield return GateMode; yield return GateKeySource; yield return CompKeySource; yield return Order;
    }

    public void Refresh(MixState m)
    {
        foreach (var p in All()) p.Refresh(m);
        foreach (var b in Bands) b.Refresh(m);
        foreach (var b in DynBands) b.Refresh(m);
        foreach (var s in Sends) s.Refresh(m);
    }
}

/// <summary>One dynamic EQ band: a bell that only moves when its range gets loud.</summary>
public sealed partial class DynEqBandView : ObservableObject
{
    public DynEqBandView(string id, int band, Action<string, object> q)
    {
        Band = band;
        DynEqBand? B(MixState m) => m.Find(id) is { } st && band < st.DynEq.Bands.Count ? st.DynEq.Bands[band] : null;
        On = new ToggleParam($"{id}.dynEq.{band}.on", "On", m => B(m)?.On ?? false, q);
        Freq = new SliderParam($"{id}.dynEq.{band}.freq", "Freq", 20, 20000, "Hz", m => B(m)?.Freq, q, log: true);
        Q = new SliderParam($"{id}.dynEq.{band}.q", "Width (Q)", 0.3, 10, "", m => B(m)?.Q, q, log: true, def: 2);
        Threshold = new SliderParam($"{id}.dynEq.{band}.threshold", "Threshold", -60, 0, "dB", m => B(m)?.Threshold, q, step: 0.5, def: -30);
        Range = new SliderParam($"{id}.dynEq.{band}.range", "Range", -18, 12, "dB", m => B(m)?.Range, q, step: 0.5, def: -6);
        Ratio = new SliderParam($"{id}.dynEq.{band}.ratio", "Ratio", 1, 20, ":1", m => B(m)?.Ratio, q, log: true, def: 3);
        Attack = new SliderParam($"{id}.dynEq.{band}.attack", "Attack", 0.1, 200, "ms", m => B(m)?.Attack, q, log: true, def: 5);
        Release = new SliderParam($"{id}.dynEq.{band}.release", "Release", 10, 2000, "ms", m => B(m)?.Release, q, log: true, def: 120);
    }

    public int Band { get; }
    public string Title => $"DYNAMIC BAND {Band + 1}";
    public ToggleParam On { get; }
    public SliderParam Freq { get; }
    public SliderParam Q { get; }
    public SliderParam Threshold { get; }
    public SliderParam Range { get; }
    public SliderParam Ratio { get; }
    public SliderParam Attack { get; }
    public SliderParam Release { get; }

    public void Refresh(MixState m)
    {
        On.Refresh(m); Freq.Refresh(m); Q.Refresh(m); Threshold.Refresh(m); Range.Refresh(m);
        Ratio.Refresh(m); Attack.Refresh(m); Release.Refresh(m);
    }
}
