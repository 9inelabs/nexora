using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.App.ViewModels;

/// <summary>
/// The bus tags above the mixer: Main, every mix, effect, group and matrix, and the stream, each a small card.
/// One tap shows that bus on the faders (sends on faders for mixes and effects); the pencil opens it to edit
/// its EQ, compressor, graphic EQ and effects.
/// </summary>
public sealed partial class NexAudioViewModel
{
    /// <summary>Colours for strips and buses that have none of their own, chosen to sit together on the dark desk.</summary>
    private static readonly string[] MixPalette = ["#6CB4F5", "#4FD1C5", "#A78BFA", "#F59E5B", "#F472B6", "#9BD66B", "#F2D06B", "#FF8A7A"];
    private static readonly string[] FxPalette = ["#F472B6", "#E879F9", "#C084FC", "#FB7185"];
    private static readonly string[] GroupPalette = ["#5AA9F0", "#38BDF8", "#818CF8", "#60A5FA", "#22D3EE", "#93C5FD", "#7DD3FC", "#A5B4FC"];

    /// <summary>The colour a strip shows: its own (from the X32 or set here), or one for its kind.</summary>
    public static Color ColourOf(Strip s)
    {
        if (s.Colour is { Length: > 0 } c && Color.TryParse(c, out var own)) return own;
        var n = Math.Max(1, s.Number) - 1;
        return Color.Parse(s.Kind switch
        {
            StripKind.Mix => MixPalette[n % MixPalette.Length],
            StripKind.Fx => FxPalette[n % FxPalette.Length],
            StripKind.Group => GroupPalette[n % GroupPalette.Length],
            StripKind.Matrix => "#94A3B8",
            StripKind.Main => "#E2BE76",
            StripKind.Mono => "#A4A7AE",
            StripKind.Stream => "#FF6B6B",
            _ => "#3A3D44",
        });
    }

    /// <summary>A strip's colour, taking a channel's X32 scribble colour when it has none of its own.</summary>
    public Color StripColour(Strip s)
    {
        if (s.Colour is not { Length: > 0 } && s.Kind == StripKind.Channel && _x32.Info is not null)
        {
            var ch = _x32.Channels;
            if (s.Number >= 1 && s.Number <= ch.Count && ch[s.Number - 1].Colour != 0) return Color.Parse(ch[s.Number - 1].ColourHex);
        }
        return ColourOf(s);
    }

    public ObservableCollection<BusTagView> BusTags { get; } = [];

    private void BuildBusTags()
    {
        BusTags.Clear();
        var all = _mix.AllStrips().ToList();
        var n = all.Count;
        void Add(Strip s) => BusTags.Add(new BusTagView(this, s, all.IndexOf(s), s.Stereo ? all.IndexOf(s) + n : -1));
        Add(_mix.Main);
        foreach (var kind in new[] { StripKind.Mix, StripKind.Fx, StripKind.Group, StripKind.Matrix })
            foreach (var b in _mix.BusesOf(kind)) Add(b);
        Add(_mix.Stream);
        UpdateBusTags();
    }

    private void UpdateBusTags()
    {
        var current = LayerBus?.Id ?? "main";
        foreach (var t in BusTags)
            if (_mix.Find(t.Id) is { } s) t.Update(s, t.Id == current, NameOf(s));
    }

    /// <summary>Shows a bus on the faders: the main mix, or every strip's send to a mix or effect.</summary>
    internal void ShowBus(string id)
    {
        var target = Layers.FirstOrDefault(l => l.BusId == id) ?? (id == "main" ? Layers.FirstOrDefault() : null);
        if (target is not null)
        {
            Layer = target;
            if (Bank == 2) ShowBank("0");
        }
        else
        {
            // Groups, matrices and the stream: open their strip on the bus bank.
            Layer = Layers.FirstOrDefault();
            if (Bank != 2) ShowBank("2");
        }
        SelectStrip(id, false);
        UpdateBusTags();
    }

    internal void EditBus(string id) => OpenStripSection(id, nameof(ChannelSection.Home));

    /// <summary>The new-bus card: what kind, its name, mono or stereo, and where its sends are taken from.</summary>
    public NewBusForm NewBus => _newBus ??= new NewBusForm(this);
    private NewBusForm? _newBus;

    [RelayCommand]
    private void OpenNewBus()
    {
        NewBus.Reset();
        NewBus.IsOpen = true;
    }

    /// <summary>How many more buses of a kind there is room for.</summary>
    internal int RoomFor(StripKind kind) => kind switch
    {
        StripKind.Mix => MixState.MaxMix,
        StripKind.Group => MixState.MaxGroups,
        StripKind.Matrix => MixState.MaxMatrix,
        StripKind.Fx => MixState.MaxFx,
        _ => 0,
    } - _mix.BusesOf(kind).Count();

    internal async Task CreateBus(NewBusForm f)
    {
        var args = new Dictionary<string, string>
        {
            ["kind"] = f.Kind.Kind.ToString(),
            ["stereo"] = f.Stereo ? "true" : "false",
        };
        if (f.Name.Trim() is { Length: > 0 } name) args["name"] = name;
        if (f.ShowTap && f.Tap is { } tap) args["tap"] = tap.Tap.ToString();
        if (f.ShowToMain) args["toMain"] = f.ToMain ? "true" : "false";
        if (f.ShowEffect && f.Effect is { } fx) args["effect"] = fx.Value;
        await Command("bus.add", args);
        f.IsOpen = false;
    }

    /// <summary>Called with the meters 30 times a second.</summary>
    private void UpdateBusMeters(float[] l, float[] r)
    {
        foreach (var t in BusTags)
        {
            var v = t.MeterIndex >= 0 && t.MeterIndex < l.Length ? l[t.MeterIndex] : 0;
            if (t.MeterIndexR >= 0 && t.MeterIndexR - l.Length >= 0 && t.MeterIndexR - l.Length < r.Length) v = Math.Max(v, r[t.MeterIndexR - l.Length]);
            t.Level = v <= 1e-5 ? -60 : Math.Max(-60, 20 * Math.Log10(v));
        }
    }
}

/// <summary>One bus as a small card above the mixer.</summary>
public sealed partial class BusTagView(NexAudioViewModel owner, Strip s, int meterIndex, int meterIndexR) : ObservableObject
{
    public string Id { get; } = s.Id;
    public StripKind Kind { get; } = s.Kind;
    public int MeterIndex { get; } = meterIndex;
    public int MeterIndexR { get; } = meterIndexR;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private IBrush _tone = Brushes.Gray;
    [ObservableProperty] private IBrush _toneTint = Brushes.Transparent;
    [ObservableProperty] private IBrush _edge = Brushes.Transparent;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private double _level = -60;
    [ObservableProperty] private bool _hasEq;
    [ObservableProperty] private bool _hasComp;
    [ObservableProperty] private bool _hasFx;

    /// <summary>Tapping shows sends on faders (mixes and effects) or the main mix.</summary>
    public bool ShowsSends => Kind is StripKind.Mix or StripKind.Fx;

    public void Update(Strip st, bool current, string name)
    {
        Name = name;
        KindText = st.Kind switch
        {
            StripKind.Main => "MAIN  L R",
            StripKind.Stream => "STREAM",
            _ => $"{MixState.KindLabel(st.Kind).ToUpperInvariant()} {st.Number}",
        };
        var c = NexAudioViewModel.ColourOf(st);
        Tone = new SolidColorBrush(c);
        ToneTint = new SolidColorBrush(Color.FromArgb(current ? (byte)0x30 : (byte)0x12, c.R, c.G, c.B));
        IsCurrent = current;
        Edge = current ? Tone : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        Mute = st.Mute;
        LevelText = st.Fader <= Db.Floor ? "-∞" : $"{st.Fader:+0.0;-0.0;0.0}";
        HasEq = st.Eq.On && !st.Eq.IsFlat || st.Hpf.On;
        HasComp = st.Comp.On;
        HasFx = st.Rack.Any(r => !r.Bypass);
        Detail = st.Kind switch
        {
            StripKind.Fx => st.Rack.FirstOrDefault() is { } fx ? Effects.NameOf(fx) : "No effect",
            StripKind.Mix => st.Stereo ? "Stereo mix" : "Mono mix",
            StripKind.Group => st.ToMain ? "To main" : "Not to main",
            StripKind.Matrix => "From main",
            StripKind.Stream => "Live Output",
            _ => "PA",
        };
    }

    [RelayCommand] private void Show() => owner.ShowBus(Id);
    [RelayCommand] private void Edit() => owner.EditBus(Id);
}

/// <summary>A kind of bus on the new-bus card.</summary>
public sealed record BusKindChoice(StripKind Kind, string Label, string Hint)
{
    public static IReadOnlyList<BusKindChoice> All { get; } =
    [
        new(StripKind.Mix, "Monitor mix", "A wedge, in-ear or side fill. Each channel gets its own send level."),
        new(StripKind.Group, "Subgroup", "Drums, vocals, choir or band: channels go through it to the main mix, after their faders, so one fader rides the lot."),
        new(StripKind.Fx, "Effect", "A reverb or delay that channels send to; its return goes back into the mix."),
        new(StripKind.Matrix, "Matrix", "Lobby, overflow room or delay speakers, built from the main mix, subgroups and mixes."),
    ];
}

/// <summary>The new-bus card on the mixer.</summary>
public sealed partial class NewBusForm : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public NewBusForm(NexAudioViewModel owner)
    {
        _owner = owner;
        _kind = BusKindChoice.All[0];
        Reset();
    }

    public IReadOnlyList<BusKindChoice> Kinds => BusKindChoice.All;
    public IReadOnlyList<TapChoice> Taps => TapChoice.All;
    public IReadOnlyList<Choice> Effects { get; } =
    [
        new("hall", "Hall reverb"),
        new("room", "Room reverb"),
        new("plate", "Plate reverb"),
        new("delay", "Delay (echo)"),
    ];

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTap), nameof(ShowToMain), nameof(ShowEffect), nameof(Hint), nameof(RoomText), nameof(CanCreate), nameof(TapNote), nameof(NamePlaceholder))]
    private BusKindChoice _kind;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StereoText))] private bool _stereo;
    public string StereoText => Stereo ? "Stereo (L R)" : "Mono";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TapNote))] private TapChoice? _tap;
    [ObservableProperty] private bool _toMain = true;
    [ObservableProperty] private Choice? _effect;

    /// <summary>Subgroups always take their channels after the fader, so they have no tap point to choose.</summary>
    public bool ShowTap => Kind.Kind is StripKind.Mix or StripKind.Fx or StripKind.Matrix;
    public bool ShowToMain => Kind.Kind is StripKind.Group or StripKind.Fx;
    public bool ShowEffect => Kind.Kind == StripKind.Fx;
    public string Hint => Kind.Hint;
    public bool CanCreate => _owner.RoomFor(Kind.Kind) > 0;
    public string NamePlaceholder => Kind.Kind switch
    {
        StripKind.Mix => "For example: Drummer's in-ear",
        StripKind.Group => "For example: Choir",
        StripKind.Fx => "For example: Vocal reverb",
        _ => "For example: Overflow room",
    };

    public string RoomText => _owner.RoomFor(Kind.Kind) switch
    {
        <= 0 => "There is no room for another of these.",
        1 => "Room for 1 more.",
        var n => $"Room for {n} more.",
    };

    public string TapNote => Tap?.Tap switch
    {
        SendTap.Input => "Straight from the input: no EQ, gate or compressor, and the fader does not change it.",
        SendTap.PreEq => "After the low cut and drive, before the EQ and dynamics. The fader does not change it.",
        SendTap.PostEq => "With the channel's EQ, before the gate and compressor. The fader does not change it.",
        SendTap.Pre => "With all the channel's processing, but the fader does not change it. Usual for wedges and in-ears.",
        SendTap.Post => "Follows the channel's fader and mute. Usual for reverbs, delays and the lobby.",
        _ => "",
    };

    partial void OnKindChanged(BusKindChoice value) => Defaults();

    /// <summary>Back to the usual settings for the chosen kind.</summary>
    public void Reset()
    {
        Name = "";
        if (Kind.Kind is var k && _owner.RoomFor(k) <= 0)
            Kind = BusKindChoice.All.FirstOrDefault(x => _owner.RoomFor(x.Kind) > 0) ?? Kind;
        Defaults();
        OnPropertyChanged(nameof(RoomText));
        OnPropertyChanged(nameof(CanCreate));
    }

    private void Defaults()
    {
        Stereo = Kind.Kind is StripKind.Fx or StripKind.Group;
        var tap = Kind.Kind == StripKind.Mix ? SendTap.Pre : SendTap.Post;
        Tap = TapChoice.All.First(t => t.Tap == tap);
        ToMain = true;
        Effect = Effects[0];
    }

    [RelayCommand] private void PickKind(BusKindChoice k) => Kind = k;
    [RelayCommand] private void PickTap(TapChoice t) => Tap = t;
    [RelayCommand] private Task Create() => _owner.CreateBus(this);
    [RelayCommand] private void Cancel() => IsOpen = false;
}
