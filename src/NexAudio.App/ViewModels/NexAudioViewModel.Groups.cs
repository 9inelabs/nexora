using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>Control groups (VCA masters, linked faders, mute groups), stereo pairs and the solo settings.</summary>
public sealed partial class NexAudioViewModel
{
    /// <summary>Colours for control groups, so each group's chip on a fader is easy to tell apart.</summary>
    public static readonly IReadOnlyList<string> GroupColours =
        ["#F59E5B", "#6CB4F5", "#9BD66B", "#F472B6", "#A78BFA", "#4FD1C5", "#F2D06B", "#FF8A7A"];

    public static Color ColourOf(ControlGroup g)
    {
        if (g.Colour is { Length: > 0 } c && Color.TryParse(c, out var own)) return own;
        var n = int.TryParse(g.Id.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i - 1 : 0;
        return Color.Parse(GroupColours[n % GroupColours.Count]);
    }

    /// <summary>Every control group, for the Groups tab and the Groups bank of faders.</summary>
    public ObservableCollection<ControlGroupView> ControlGroups { get; } = [];
    /// <summary>The mute groups, as big buttons above the faders.</summary>
    public ObservableCollection<ControlGroupView> MuteGroups { get; } = [];
    public bool HasControlGroups => ControlGroups.Count > 0;
    public bool HasMuteGroups => MuteGroups.Count > 0;
    public bool CanAddControl => ControlGroups.Count < MixState.MaxControls;
    public string ControlCountText => $"{ControlGroups.Count} of {MixState.MaxControls} in use";

    public ObservableCollection<PairRow> PairRows { get; } = [];
    public ObservableCollection<MixParam> SoloParams { get; } = [];

    public static IReadOnlyList<Choice> ControlModes { get; } =
    [
        new("Vca", "Control group (VCA)"), new("Linked", "Linked faders"), new("Mute", "Mute group"),
    ];

    private void BuildGroups()
    {
        ControlGroups.Clear();
        MuteGroups.Clear();
        foreach (var g in _mix.Controls)
        {
            var v = new ControlGroupView(this, g);
            ControlGroups.Add(v);
            if (g.Mode == ControlMode.Mute) MuteGroups.Add(v);
        }
        if (SpillId is { } sid && _mix.Control(sid) is null) SpillId = null;
        foreach (var v in ControlGroups) v.IsSpilled = v.Id == SpillId;

        PairRows.Clear();
        for (var i = 0; i + 1 < _mix.Channels.Count; i += 2) PairRows.Add(new PairRow(this, _mix.Channels[i], _mix.Channels[i + 1]));

        SoloParams.Clear();
        var q = (Action<string, object>)Queue;
        var modes = new List<Choice> { new("Pfl", "Before the fader (PFL)"), new("Afl", "After the fader (AFL)") };
        SoloParams.Add(new ChoiceParam("solo.route", "Solo buttons go to", [new("Bus1", "Solo bus 1"), new("Bus2", "Solo bus 2"), new("Both", "Both solo buses")], m => m.Solo.Route.ToString(), q));
        SoloParams.Add(new ChoiceParam("solo.mode", "Solo bus 1 listens", modes, m => m.Solo.Mode.ToString(), q));
        SoloParams.Add(new SliderParam("solo.level", "Solo bus 1 level", -40, 10, "dB", m => m.Solo.Level, q, step: 0.5, def: 0));
        SoloParams.Add(new ChoiceParam("solo.mode2", "Solo bus 2 listens", modes, m => m.Solo.Mode2.ToString(), q));
        SoloParams.Add(new SliderParam("solo.level2", "Solo bus 2 level", -40, 10, "dB", m => m.Solo.Level2, q, step: 0.5, def: 0));
        SoloParams.Add(new ToggleParam("solo.exclusive", "One solo at a time (a new solo clears the others)", m => m.Solo.Exclusive, q));
        SoloParams.Add(new ToggleParam("solo.autoSolo", "Selecting a strip solos it", m => m.Solo.AutoSolo, q));
        foreach (var p in SoloParams) p.Refresh(_mix);

        OnPropertyChanged(nameof(HasControlGroups));
        OnPropertyChanged(nameof(HasMuteGroups));
        OnPropertyChanged(nameof(CanAddControl));
        OnPropertyChanged(nameof(ControlCountText));
        OnPropertyChanged(nameof(SpillTitle));
        OnPropertyChanged(nameof(IsSpilled));
    }

    private void RefreshGroups()
    {
        foreach (var v in ControlGroups) if (_mix.Control(v.Id) is { } g) v.Update(g);
        foreach (var r in PairRows) r.Refresh(_mix);
        foreach (var p in SoloParams) p.Refresh(_mix);
    }

    /// <summary>Strips that can join a control group (everything but the stream), in desk order.</summary>
    internal IEnumerable<Strip> GroupableStrips() => _mix.AllStrips().Where(s => s.Kind != StripKind.Stream);

    [RelayCommand]
    private Task AddControl(string mode)
    {
        var members = SelectedId is { } id && _mix.Find(id) is { Kind: not StripKind.Stream } ? id : "";
        return Command("control.add", new() { ["mode"] = mode, ["members"] = AddWithSelection ? members : "" });
    }

    /// <summary>New groups start with the selected strip in them.</summary>
    [ObservableProperty] private bool _addWithSelection = true;

    internal Task RemoveControl(string id) => Command("control.remove", new() { ["id"] = id });

    // ---- Spill: the faders show only one group's members ----------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpillTitle), nameof(IsSpilled))]
    private string? _spillId;

    public bool IsSpilled => SpillId is not null;
    public string SpillTitle => SpillId is { } id && _mix.Control(id) is { } g ? $"Showing the members of {ControlRules.Label(g)}" : "";

    internal void Spill(string id)
    {
        SpillId = SpillId == id ? null : id;
        foreach (var v in ControlGroups) v.IsSpilled = v.Id == SpillId;
        if (SpillId is not null && Bank == 3) Bank = 0;
        FillVisible();
    }

    [RelayCommand]
    private void ClearSpill()
    {
        SpillId = null;
        foreach (var v in ControlGroups) v.IsSpilled = false;
        FillVisible();
    }

    public bool BankGroups => Bank == 3;

    /// <summary>The control groups a strip is in, as small coloured chips on its fader.</summary>
    internal IReadOnlyList<GroupChip> ChipsFor(Strip s) =>
        _mix.Controls.Where(g => g.Members.Contains(s.Id)).Select(g => new GroupChip(Short(g), new SolidColorBrush(ColourOf(g)), g.Safe.Contains(s.Id), ControlRules.Label(g))).ToList();

    /// <summary>A few letters for a group's chip: "DRUMS", "BAND", "BM" for "Band mics", "C1" for an unnamed control group.</summary>
    private static string Short(ControlGroup g)
    {
        var words = g.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1) return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
        if (words.Length == 1) return (words[0].Length <= 5 ? words[0] : words[0][..3]).ToUpperInvariant();
        return g.Mode switch
        {
            ControlMode.Vca => $"C{g.Id[2..]}",
            ControlMode.Linked => $"L{g.Id[2..]}",
            _ => $"M{g.Id[2..]}",
        };
    }

    /// <summary>Auto-solo: selecting a strip solos it on its own.</summary>
    private void AutoSolo(string id)
    {
        if (!_mix.Solo.AutoSolo || _mix.Find(id) is not { Kind: not (StripKind.Main or StripKind.Stream) }) return;
        _ = Command("solo.only", new() { ["strip"] = id });
    }
}

/// <summary>A control group's chip on a member's fader.</summary>
public sealed record GroupChip(string Text, IBrush Colour, bool Safe, string Tip)
{
    public double Opacity => Safe ? 0.4 : 1;
}

/// <summary>One control group: its master fader and mute, and who is in it.</summary>
public sealed partial class ControlGroupView : ObservableObject
{
    private readonly NexAudioViewModel _owner;
    private bool _refreshing;

    public ControlGroupView(NexAudioViewModel owner, ControlGroup g)
    {
        _owner = owner;
        Id = g.Id;
        Number = g.Id[2..];
        ModeChoice = new ChoiceParam($"{g.Id}.mode", "What it does", NexAudioViewModel.ControlModes, m => m.Control(Id)?.Mode.ToString(), owner.Queue);
        foreach (var s in owner.GroupableStrips()) Members.Add(new MemberChip(this, s.Id, owner.NameOf(s), new SolidColorBrush(owner.StripColour(s)), s.Kind == StripKind.Channel ? $"{s.Number}" : MixState.KindLabel(s.Kind)));
        foreach (var c in NexAudioViewModel.GroupColours) Swatches.Add(new Swatch(c, this));
        Update(g);
    }

    public string Id { get; }
    public string Number { get; }
    public ChoiceParam ModeChoice { get; }
    public ObservableCollection<MemberChip> Members { get; } = [];
    public ObservableCollection<Swatch> Swatches { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private ControlMode _mode;
    [ObservableProperty] private IBrush _colour = Brushes.Gray;
    [ObservableProperty] private IBrush _card = Brushes.Transparent;
    [ObservableProperty] private IBrush _scribbleText = Brushes.White;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FaderText))] private double _fader;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private string _memberText = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSpilled;

    public string ModeText => Mode switch
    {
        ControlMode.Vca => "CONTROL",
        ControlMode.Linked => "LINKED",
        _ => "MUTE GROUP",
    };

    public string ModeHint => Mode switch
    {
        ControlMode.Vca => "The master turns every member up or down together and keeps their balance. Their sound is not mixed together.",
        ControlMode.Linked => "Move any member's fader and the others follow by the same amount. The master moves them all.",
        _ => "One button mutes every member, for example all the band mics during the sermon.",
    };

    public bool HasFader => Mode != ControlMode.Mute;
    public bool HasMute => Mode != ControlMode.Linked;
    public string FaderText => Fader <= Db.Floor ? "-∞" : $"{Fader:+0.0;-0.0;0.0}";
    public string MuteText => Mode == ControlMode.Mute ? Mute ? "MUTED" : "MUTE" : "MUTE";

    public void Update(ControlGroup g)
    {
        _refreshing = true;
        Name = g.Name;
        Title = ControlRules.Label(g);
        Mode = g.Mode;
        var c = NexAudioViewModel.ColourOf(g);
        Colour = new SolidColorBrush(c);
        Card = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        ScribbleText = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140 ? new SolidColorBrush(Color.Parse("#121316")) : Brushes.White;
        Fader = g.Fader;
        Mute = g.Mute;
        Count = g.Members.Count;
        var names = g.Members.Select(id => Members.FirstOrDefault(m => m.Id == id)?.Name ?? id).ToList();
        MemberText = names.Count == 0 ? "No members yet. Tap strips below to add them." :
            $"{names.Count} {(names.Count == 1 ? "member" : "members")}: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? $" and {names.Count - 6} more" : "")}" +
            (g.Safe.Count > 0 ? $". {g.Safe.Count} safe." : "");
        foreach (var m in Members)
        {
            m.IsMember = g.Members.Contains(m.Id);
            m.IsSafe = g.Safe.Contains(m.Id);
        }
        foreach (var s in Swatches) s.IsCurrent = s.Value.Equals(g.Colour, StringComparison.OrdinalIgnoreCase);
        ModeChoice.Refresh(_owner.Mix);
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(HasFader));
        OnPropertyChanged(nameof(HasMute));
        OnPropertyChanged(nameof(MuteText));
        _refreshing = false;
    }

    partial void OnFaderChanged(double value)
    {
        if (!_refreshing) _owner.Queue($"{Id}.fader", Math.Round(value, 1));
    }

    partial void OnNameChanged(string value)
    {
        if (!_refreshing) _owner.Queue($"{Id}.name", value);
    }

    [RelayCommand] private void ToggleMute() => _owner.Queue($"{Id}.mute", !Mute);
    [RelayCommand] private void Spill() => _owner.Spill(Id);
    [RelayCommand] private Task Remove() => _owner.RemoveControl(Id);
    [RelayCommand] private void ResetFader() => Fader = 0;

    internal void ToggleMember(string id, bool on) => _owner.Queue($"{Id}.member.{id}", on);
    internal void ToggleSafe(string id, bool on) => _owner.Queue($"{Id}.safe.{id}", on);
    internal void SetColour(string c) => _owner.Queue($"{Id}.colour", c);
}

/// <summary>One strip on a control group's member list: tap to add or take out, the lock to make it safe.</summary>
public sealed partial class MemberChip(ControlGroupView group, string id, string name, IBrush colour, string number) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Number { get; } = number;
    public IBrush Colour { get; } = colour;
    [ObservableProperty] private bool _isMember;
    [ObservableProperty] private bool _isSafe;

    [RelayCommand] private void Toggle() => group.ToggleMember(Id, !IsMember);
    [RelayCommand] private void ToggleSafe() => group.ToggleSafe(Id, !IsSafe);
}

public sealed partial class Swatch(string value, ControlGroupView group) : ObservableObject
{
    public string Value { get; } = value;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(value));
    [ObservableProperty] private bool _isCurrent;
    [RelayCommand] private void Pick() => group.SetColour(Value);
}

/// <summary>Two neighbouring channels that can be linked as a stereo pair.</summary>
public sealed partial class PairRow : ObservableObject
{
    public PairRow(NexAudioViewModel owner, Strip left, Strip right)
    {
        Title = $"Ch {left.Number} + {right.Number}";
        Names = $"{owner.NameOf(left)} / {owner.NameOf(right)}";
        var id = left.Id;
        Linked = new ToggleParam($"{id}.linked", "Stereo pair", m => m.Find(id)?.Linked ?? false, owner.Queue);
        Linked.Refresh(owner.Mix);
    }

    public string Title { get; }
    public string Names { get; }
    public ToggleParam Linked { get; }

    public void Refresh(MixState m) => Linked.Refresh(m);
}
