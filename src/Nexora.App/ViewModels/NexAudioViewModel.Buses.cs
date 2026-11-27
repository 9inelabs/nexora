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

    [RelayCommand]
    private Task AddMixBus() => Command("bus.add", new() { ["kind"] = nameof(StripKind.Mix) });

    [RelayCommand]
    private Task AddFxBus() => Command("bus.add", new() { ["kind"] = nameof(StripKind.Fx) });

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
