using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>One of your own fader banks, as a button beside the usual banks.</summary>
public sealed partial class UserBankView(NexAudioViewModel owner, int index, string name, int count) : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string CountText { get; } = count == 1 ? "1 strip" : $"{count} strips";
    public string KeyText => Index < 9 ? $"Ctrl+{Index + 1}" : "";
    [ObservableProperty] private bool _isCurrent;
    [RelayCommand] private void Show() => owner.ShowUserBank(Index);
    [RelayCommand] private void Edit() => owner.EditUserBank(Index);
}

/// <summary>A strip in the bank editor: tick it to put it on the bank.</summary>
public sealed partial class BankMemberChoice(string id, string label, string kind, IBrush colour, bool on) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Kind { get; } = kind;
    public IBrush Colour { get; } = colour;
    [ObservableProperty] private bool _on = on;
}

/// <summary>One answer in quick select.</summary>
public sealed record QuickItem(string Id, string Number, string Name, string Kind, IBrush Colour);

/// <summary>A utility fader: any strip's fader, or its send to one bus, always to hand beside the main.</summary>
public sealed partial class UtilityView(NexAudioViewModel owner, int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Title => $"UTILITY {Index + 1}";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FaderText))] private double _fader = Db.Floor;
    [ObservableProperty] private bool _assigned;
    [ObservableProperty] private string _name = "Not set";
    [ObservableProperty] private string _what = "Press SET to choose";
    [ObservableProperty] private IBrush _colour = Brushes.Gray;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _level = -60;
    [ObservableProperty] private int _meterIndex = -1;
    [ObservableProperty] private int _meterIndexR = -1;
    internal bool Refreshing;
    public string FaderText => Fader <= Db.Floor ? "-∞" : $"{Fader:+0.0;-0.0;0.0}";

    partial void OnFaderChanged(double value)
    {
        if (!Refreshing) owner.MoveUtility(Index, value);
    }

    [RelayCommand] private void Assign() => owner.OpenUtilityPicker(Index);
    [RelayCommand] private void ResetFader() => Fader = 0;
}

/// <summary>
/// Phase 5: the meter bridge, the correlation meters, your own fader banks, quick select and keyboard shortcuts,
/// the utility faders, pages on a second screen, text size and contrast, and offline show preparation.
/// </summary>
public sealed partial class NexAudioViewModel
{
    private UserPrefs _prefs = new();
    private string UserKey => Live.User?.Id ?? "anyone";

    private void LoadPrefs()
    {
        _prefs = UserPrefs.Load(UserKey);
        _textScale = TextScales.FirstOrDefault(c => c.Value == _prefs.TextScale.ToString("0.##", CultureInfo.InvariantCulture)) ?? TextScales[0];
        _highContrast = _prefs.HighContrast;
        _peakHold = PeakHolds.FirstOrDefault(c => c.Value == _prefs.PeakHold) ?? PeakHolds[1];
        _meterFall = MeterFalls.FirstOrDefault(c => c.Value == _prefs.Fall) ?? MeterFalls[1];
        ApplyDisplay();
    }

    private void SavePrefs(UserPrefs p)
    {
        _prefs = p.Clean();
        _prefs.Save(UserKey);
    }

    private void BuildWorkflow()
    {
        BuildBridge();
        BuildUserBanks();
        BuildUtilities();
    }

    // ---- Meter bridge -------------------------------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<BridgeEntry> _bridgeEntries = [];
    public bool OnBridge => Tab == NexAudioTab.Bridge;

    private void BuildBridge()
    {
        var all = _mix.AllStrips().ToList();
        var n = all.Count;
        var list = new List<BridgeEntry>();
        void Add(string section, Strip s, string number)
        {
            var i = all.IndexOf(s);
            if (i < 0) return;
            list.Add(new BridgeEntry(section, number, NameOf(s), new SolidColorBrush(StripColour(s)), i, s.Stereo ? i + n : -1));
        }
        foreach (var c in _mix.Channels) Add("Channels", c, c.Number.ToString(CultureInfo.InvariantCulture));
        foreach (var (kind, title, tag) in new[] { (StripKind.Group, "Groups", "G"), (StripKind.Fx, "Effects", "FX"), (StripKind.Mix, "Mixes", "M"), (StripKind.Matrix, "Matrix", "MX") })
            foreach (var b in _mix.BusesOf(kind)) Add(title, b, $"{tag}{b.Number}");
        Add("Outputs", _mix.Main, "L R");
        if (_mix.IsLcr) Add("Outputs", _mix.Centre, "C");
        Add("Outputs", _mix.Mono, "MONO");
        Add("Outputs", _mix.Stream, "STR");
        BridgeEntries = list;
    }

    [RelayCommand] private void ClearPeaks() => MeterSet.ClearPeaks();

    // ---- Correlation ----------------------------------------------------------------------------------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MainPhaseText), nameof(MainPhaseBrush))] private double _mainPhase = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StreamPhaseText), nameof(StreamPhaseBrush))] private double _streamPhase = double.NaN;
    public string MainPhaseText => PhaseWords(MainPhase);
    public string StreamPhaseText => PhaseWords(StreamPhase);
    public IBrush MainPhaseBrush => PhaseMeter.BrushFor(MainPhase);
    public IBrush StreamPhaseBrush => PhaseMeter.BrushFor(StreamPhase);

    /// <summary>What the correlation means, in plain words.</summary>
    public static string PhaseWords(double v) => double.IsNaN(v) ? "Too quiet to tell"
        : v >= 0.3 ? $"{v:+0.00;-0.00} · safe in mono"
        : v >= -0.1 ? $"{v:+0.00;-0.00} · wide (check in mono)"
        : $"{v:+0.00;-0.00} · out of phase: thins out in mono";

    // ---- Your own fader banks -------------------------------------------------------------------------

    public ObservableCollection<UserBankView> UserBanks { get; } = [];
    public bool CanAddBank => _prefs.Banks.Count < UserPrefs.MaxBanks;
    public bool OnUserBank => Bank >= 10;

    [ObservableProperty] private bool _bankEditing;
    [ObservableProperty] private string _bankName = "";
    [ObservableProperty] private string _bankFilter = "";
    private int _bankEditIndex = -1;
    public ObservableCollection<BankMemberChoice> BankMembers { get; } = [];
    public bool BankEditIsNew => _bankEditIndex < 0;
    public string BankEditTitle => _bankEditIndex < 0 ? "New fader bank" : "Edit fader bank";
    public string BankCountText => $"{BankMembers.Count(m => m.On)} of {UserPrefs.MaxBankStrips} strips";

    private void BuildUserBanks()
    {
        UserBanks.Clear();
        for (var i = 0; i < _prefs.Banks.Count; i++)
        {
            var b = _prefs.Banks[i];
            UserBanks.Add(new UserBankView(this, i, b.Name, b.Strips.Count(id => _mix.Find(id) is not null)) { IsCurrent = Bank == 10 + i });
        }
        if (Bank >= 10 && Bank - 10 >= _prefs.Banks.Count) Bank = 0;
        OnPropertyChanged(nameof(CanAddBank));
    }

    partial void OnBankChanged(int value)
    {
        foreach (var b in UserBanks) b.IsCurrent = value == 10 + b.Index;
        OnPropertyChanged(nameof(OnUserBank));
    }

    private IEnumerable<StripView> UserBankStrips(int k)
    {
        if (k < 0 || k >= _prefs.Banks.Count) return [];
        var views = ChannelStrips.Concat(BusStrips).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        return _prefs.Banks[k].Strips.Select(id => views.GetValueOrDefault(id)).OfType<StripView>().ToList();
    }

    public void ShowUserBank(int k)
    {
        if (k < 0 || k >= _prefs.Banks.Count) return;
        ShowBank((10 + k).ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void NewUserBank()
    {
        if (!CanAddBank)
        {
            Notice = $"You have all {UserPrefs.MaxBanks} banks. Edit or delete one first.";
            return;
        }
        OpenBankEditor(-1, $"My bank {_prefs.Banks.Count + 1}", []);
    }

    public void EditUserBank(int k)
    {
        if (k < 0 || k >= _prefs.Banks.Count) return;
        OpenBankEditor(k, _prefs.Banks[k].Name, _prefs.Banks[k].Strips);
    }

    private void OpenBankEditor(int index, string name, IReadOnlyList<string> on)
    {
        _bankEditIndex = index;
        BankName = name;
        BankFilter = "";
        BankMembers.Clear();
        // Ticked strips first, in the bank's order, then the rest in mix order.
        var strips = _mix.AllStrips().Where(s => s.Kind is not StripKind.Main and not StripKind.Centre).ToList();
        foreach (var id in on)
            if (strips.FirstOrDefault(s => s.Id == id) is { } s) BankMembers.Add(Member(s, true));
        foreach (var s in strips.Where(s => !on.Contains(s.Id))) BankMembers.Add(Member(s, false));
        foreach (var m in BankMembers) m.PropertyChanged += (_, _) => OnPropertyChanged(nameof(BankCountText));
        BankEditing = true;
        OnPropertyChanged(nameof(BankEditIsNew));
        OnPropertyChanged(nameof(BankEditTitle));
        OnPropertyChanged(nameof(BankCountText));
    }

    private BankMemberChoice Member(Strip s, bool on) =>
        new(s.Id, s.Kind == StripKind.Channel ? $"{s.Number} · {NameOf(s)}" : NameOf(s), MixState.KindLabel(s.Kind), new SolidColorBrush(StripColour(s)), on);

    [RelayCommand]
    private void SaveUserBank()
    {
        var picked = BankMembers.Where(m => m.On).Select(m => m.Id).ToList();
        if (picked.Count > UserPrefs.MaxBankStrips)
        {
            Notice = $"A bank holds up to {UserPrefs.MaxBankStrips} strips. Untick {picked.Count - UserPrefs.MaxBankStrips}.";
            return;
        }
        var bank = new UserBank(string.IsNullOrWhiteSpace(BankName) ? "My bank" : BankName.Trim(), picked);
        var banks = _prefs.Banks.ToList();
        int index;
        if (_bankEditIndex >= 0 && _bankEditIndex < banks.Count)
        {
            banks[_bankEditIndex] = bank;
            index = _bankEditIndex;
        }
        else
        {
            banks.Add(bank);
            index = banks.Count - 1;
        }
        SavePrefs(_prefs with { Banks = banks });
        BankEditing = false;
        BuildUserBanks();
        ShowUserBank(index);
    }

    [RelayCommand]
    private void DeleteUserBank()
    {
        if (_bankEditIndex < 0 || _bankEditIndex >= _prefs.Banks.Count) return;
        var banks = _prefs.Banks.ToList();
        banks.RemoveAt(_bankEditIndex);
        SavePrefs(_prefs with { Banks = banks });
        BankEditing = false;
        if (Bank >= 10) ShowBank("0");
        BuildUserBanks();
    }

    [RelayCommand] private void CancelUserBank() => BankEditing = false;

    /// <summary>Moves a ticked strip one place earlier or later on the bank.</summary>
    [RelayCommand]
    private void MoveBankMember(BankMemberChoice m)
    {
        var i = BankMembers.IndexOf(m);
        if (i > 0) BankMembers.Move(i, i - 1);
    }

    // ---- Quick select ---------------------------------------------------------------------------------

    [ObservableProperty] private bool _quickOpen;
    [ObservableProperty] private string _quickText = "";
    public ObservableCollection<QuickItem> QuickResults { get; } = [];

    [RelayCommand]
    private void OpenQuick()
    {
        QuickText = "";
        FillQuick();
        QuickOpen = true;
    }

    [RelayCommand] private void CloseQuick() => QuickOpen = false;

    partial void OnQuickTextChanged(string value) => FillQuick();

    private void FillQuick()
    {
        QuickResults.Clear();
        foreach (var s in MixSearch.Find(_mix, QuickText, NameOf).Take(14))
            QuickResults.Add(new QuickItem(s.Id, Short(s), NameOf(s), MixState.KindLabel(s.Kind), new SolidColorBrush(StripColour(s))));
    }

    private static string Short(Strip s) => s.Kind switch
    {
        StripKind.Channel => s.Number.ToString(CultureInfo.InvariantCulture),
        StripKind.Main => "L R",
        StripKind.Centre => "C",
        StripKind.Mono => "M",
        StripKind.Stream => "STR",
        _ => $"{MixState.KindLabel(s.Kind)[..1]}{s.Number}",
    };

    [RelayCommand]
    private void QuickPick(string? id)
    {
        id ??= QuickResults.FirstOrDefault()?.Id;
        if (id is null || _mix.Find(id) is not { } s) return;
        QuickOpen = false;
        RevealOnFaders(s);
        SelectStrip(id, false);
    }

    /// <summary>Puts a strip's bank on the faders (unless it is on the bank showing already).</summary>
    private void RevealOnFaders(Strip s)
    {
        if (VisibleStrips.Any(v => v.Id == s.Id) || s.Kind is StripKind.Main or StripKind.Centre) return;
        var bank = s.Kind == StripKind.Channel ? (_mix.Channels.IndexOf(s) < 16 ? 0 : 1) : 2;
        ShowBank(bank.ToString(CultureInfo.InvariantCulture));
    }

    // ---- Keyboard shortcuts -------------------------------------------------------------------------

    public static IReadOnlyList<(string Keys, string What)> Shortcuts { get; } =
    [
        ("Ctrl+K or /", "Quick select: type a channel number, a bus or part of a name"),
        ("← →", "The previous or next strip"),
        ("M  ·  S", "Mute  ·  solo the selected strip"),
        ("Page Up  ·  Page Down", "The previous or next fader bank"),
        ("Ctrl+1 to Ctrl+8", "Your own fader banks"),
        ("Ctrl+Z  ·  Ctrl+Y", "Undo  ·  redo"),
        ("Ctrl+M", "The Mixer"),
        ("Ctrl+B", "The meter bridge"),
        ("Ctrl+E", "The selected strip's Channel page"),
        ("Esc", "Close quick select or the bank editor"),
        ("F1 to F12", "The macros given those keys (Show tab)"),
    ];

    public IReadOnlyList<ShortcutRow> ShortcutRows { get; } = Shortcuts.Select(s => new ShortcutRow(s.Keys, s.What)).ToList();

    /// <summary>A key on the desk (not while typing). Returns true when it did something.</summary>
    public bool HandleKey(Key key, KeyModifiers mods)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        if (key == Key.Escape)
        {
            if (QuickOpen) { QuickOpen = false; return true; }
            if (BankEditing) { BankEditing = false; return true; }
            if (UtilityPicking) { UtilityPicking = false; return true; }
            return false;
        }
        if (QuickOpen) return false;
        if (ctrl && key == Key.K || !ctrl && key is Key.Divide or Key.OemQuestion && !shift)
        {
            OpenQuick();
            return true;
        }
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when !shift: _ = Undo(); return true;
                case Key.Y:
                case Key.Z when shift: _ = Redo(); return true;
                case Key.M: Tab = NexAudioTab.Mixer; return true;
                case Key.B: Tab = NexAudioTab.Bridge; return true;
                case Key.E when SelectedId is not null: Tab = NexAudioTab.Channel; return true;
                case >= Key.D1 and <= Key.D9 when key - Key.D1 < _prefs.Banks.Count:
                    Tab = NexAudioTab.Mixer;
                    ShowUserBank(key - Key.D1);
                    return true;
            }
            return false;
        }
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.Left: StepSelection("-1"); return true;
            case Key.Right: StepSelection("1"); return true;
            case Key.PageUp: StepBank(-1); return true;
            case Key.PageDown: StepBank(1); return true;
            case Key.M when SelectedId is { } m && _mix.Find(m) is { } ms: Queue($"{m}.mute", !ms.Mute); return true;
            case Key.S when SelectedId is { } so && _mix.Find(so) is { } ss: Queue($"{so}.solo", !ss.Solo); return true;
        }
        return false;
    }

    private void StepBank(int dir)
    {
        var order = new List<int> { 0, 1, 2, 3 };
        order.AddRange(Enumerable.Range(0, _prefs.Banks.Count).Select(i => 10 + i));
        var i = order.IndexOf(Bank);
        var next = order[((i < 0 ? 0 : i) + dir + order.Count) % order.Count];
        Tab = NexAudioTab.Mixer;
        ShowBank(next.ToString(CultureInfo.InvariantCulture));
    }

    // ---- Utility faders -------------------------------------------------------------------------------

    public ObservableCollection<UtilityView> Utilities { get; } = [];
    [ObservableProperty] private bool _utilityPicking;
    [ObservableProperty] private int _utilityIndex;
    public ObservableCollection<Choice> UtilityStrips { get; } = [];
    public ObservableCollection<Choice> UtilitySends { get; } = [];
    [ObservableProperty] private Choice? _utilityStrip;
    [ObservableProperty] private Choice? _utilitySend;
    public string UtilityPickTitle => $"Utility fader {UtilityIndex + 1}";

    private void BuildUtilities()
    {
        if (Utilities.Count == 0)
            for (var i = 0; i < 2; i++) Utilities.Add(new UtilityView(this, i));
        var all = _mix.AllStrips().ToList();
        var n = all.Count;
        for (var i = 0; i < 2; i++)
        {
            var u = Utilities[i];
            var t = _prefs.Utility.ElementAtOrDefault(i);
            var s = t is null ? null : _mix.Find(t.StripId);
            var bus = t?.BusId is { } b ? _mix.Find(b) : null;
            u.MeterIndex = s is null ? -1 : all.IndexOf(s);
            u.MeterIndexR = s is { Stereo: true } ? all.IndexOf(s) + n : -1;
            RefreshUtility(u, s, bus, t?.BusId is not null);
        }
    }

    private void RefreshUtilities(string id)
    {
        foreach (var u in Utilities)
        {
            var t = _prefs.Utility.ElementAtOrDefault(u.Index);
            if (t is null || t.StripId != id && t.BusId != id) continue;
            RefreshUtility(u, _mix.Find(t.StripId), t.BusId is { } b ? _mix.Find(b) : null, t.BusId is not null);
        }
    }

    private void RefreshUtility(UtilityView u, Strip? s, Strip? bus, bool wantsBus)
    {
        u.Refreshing = true;
        if (s is null || wantsBus && bus is null)
        {
            u.Assigned = false;
            u.Enabled = false;
            u.Name = "Not set";
            u.What = "Press SET to choose a fader or a send";
            u.Colour = Brushes.Gray;
            u.Fader = Db.Floor;
        }
        else
        {
            u.Assigned = true;
            u.Name = NameOf(s);
            u.Colour = new SolidColorBrush(StripColour(bus ?? s));
            if (bus is null)
            {
                u.What = "Its fader";
                u.Fader = s.Fader;
                u.Enabled = true;
            }
            else
            {
                var can = Routing.CanSend(s, bus);
                var send = s.SendTo(bus.Id);
                u.What = $"Send to {NameOf(bus)}";
                u.Fader = can && send is { On: true } ? send.Level : Db.Floor;
                u.Enabled = can;
            }
        }
        u.Refreshing = false;
    }

    private void UpdateUtilityMeters(float[] l, float[] r)
    {
        foreach (var u in Utilities)
        {
            var v = u.MeterIndex >= 0 && u.MeterIndex < l.Length ? l[u.MeterIndex] : 0;
            if (u.MeterIndexR >= l.Length && u.MeterIndexR - l.Length < r.Length) v = Math.Max(v, r[u.MeterIndexR - l.Length]);
            u.Level = v <= 1e-5 ? -60 : Math.Max(-60, 20 * Math.Log10(v));
        }
    }

    public void MoveUtility(int index, double value)
    {
        var t = _prefs.Utility.ElementAtOrDefault(index);
        if (t is null) return;
        if (t.BusId is null) Queue($"{t.StripId}.fader", value);
        else
        {
            Queue($"{t.StripId}.send.{t.BusId}.level", value);
            Queue($"{t.StripId}.send.{t.BusId}.on", true);
        }
    }

    public void OpenUtilityPicker(int index)
    {
        UtilityIndex = index;
        UtilityStrips.Clear();
        foreach (var s in _mix.AllStrips()) UtilityStrips.Add(new Choice(s.Id, s.Kind == StripKind.Channel ? $"{s.Number} · {NameOf(s)}" : NameOf(s)));
        var t = _prefs.Utility.ElementAtOrDefault(index);
        UtilityStrip = UtilityStrips.FirstOrDefault(c => c.Value == t?.StripId) ?? UtilityStrips.FirstOrDefault();
        UtilitySend = UtilitySends.FirstOrDefault(c => c.Value == (t?.BusId ?? "")) ?? UtilitySends.FirstOrDefault();
        OnPropertyChanged(nameof(UtilityPickTitle));
        UtilityPicking = true;
    }

    partial void OnUtilityStripChanged(Choice? value)
    {
        var keep = UtilitySend?.Value;
        UtilitySends.Clear();
        UtilitySends.Add(new Choice("", "Its own fader"));
        if (value is not null && _mix.Find(value.Value) is { } s)
            foreach (var b in _mix.AllStrips().Where(b => b.Kind is StripKind.Mix or StripKind.Fx or StripKind.Matrix && Routing.CanSend(s, b)))
                UtilitySends.Add(new Choice(b.Id, $"Send to {NameOf(b)}"));
        UtilitySend = UtilitySends.FirstOrDefault(c => c.Value == keep) ?? UtilitySends[0];
    }

    [RelayCommand]
    private void SaveUtility()
    {
        if (UtilityStrip is not { } s) return;
        var u = _prefs.Utility.ToArray();
        u[UtilityIndex] = new UtilityTarget(s.Value, UtilitySend is { Value.Length: > 0 } b ? b.Value : null);
        SavePrefs(_prefs with { Utility = u });
        UtilityPicking = false;
        BuildUtilities();
    }

    [RelayCommand]
    private void ClearUtility()
    {
        var u = _prefs.Utility.ToArray();
        u[UtilityIndex] = null;
        SavePrefs(_prefs with { Utility = u });
        UtilityPicking = false;
        BuildUtilities();
    }

    [RelayCommand] private void CancelUtility() => UtilityPicking = false;

    // ---- Text size, contrast and meters ---------------------------------------------------------------

    public static IReadOnlyList<Choice> TextScales { get; } = [new("1", "Normal"), new("1.15", "Large"), new("1.3", "Extra large")];
    public static IReadOnlyList<Choice> PeakHolds { get; } = [new("off", "Off"), new("1", "1 second"), new("3", "3 seconds"), new("hold", "Until cleared")];
    public static IReadOnlyList<Choice> MeterFalls { get; } = [new("fast", "Fast"), new("medium", "Medium"), new("slow", "Slow")];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UiScale))] private Choice _textScale = TextScales[0];
    [ObservableProperty] private bool _highContrast;
    [ObservableProperty] private Choice _peakHold = PeakHolds[1];
    [ObservableProperty] private Choice _meterFall = MeterFalls[1];

    public double UiScale => double.Parse(TextScale.Value, CultureInfo.InvariantCulture);

    partial void OnTextScaleChanged(Choice value) => SaveDisplay();
    partial void OnHighContrastChanged(bool value) => SaveDisplay();
    partial void OnPeakHoldChanged(Choice value) => SaveDisplay();
    partial void OnMeterFallChanged(Choice value) => SaveDisplay();

    private void SaveDisplay()
    {
        SavePrefs(_prefs with { TextScale = UiScale, HighContrast = HighContrast, PeakHold = PeakHold.Value, Fall = MeterFall.Value });
        ApplyDisplay();
    }

    private void ApplyDisplay()
    {
        MeterSet.Configure(MeterFall.Value, PeakHold.Value);
        Contrast.Apply(HighContrast);
        MeterBridge.HighContrast = HighContrast;
    }

    // ---- Pages on a second screen -------------------------------------------------------------------

    /// <summary>Asks the window to open a page in a window of its own ("bridge", "routing" or "channel").</summary>
    public event Action<string>? PopOutRequested;

    [RelayCommand] private void PopOut(string what) => PopOutRequested?.Invoke(what);

    // ---- Offline: preparing a show without the X32 ---------------------------------------------------

    public bool IsOffline => DeskStatus.Settings.Offline;
    public string OfflineBanner => "Offline: preparing a show. Nothing reaches the X32, the failsafe and the stream are paused. Save the show (Show tab) to take it to the sound PC.";

    [RelayCommand]
    private async Task GoOffline()
    {
        await Command("desk.offline", new() { ["on"] = "true" });
        _x32.Disconnect();
        Notice = "Offline. Change anything you like; the room is not affected. If the sound is stopped, start the test device (Setup > Driver) to see meters.";
    }

    [RelayCommand]
    private async Task GoOnline()
    {
        await Command("desk.offline", new() { ["on"] = "false" });
        if (X32Address.Length > 0) _ = ConnectConsole();
        Notice = "Back online. The X32 links again and its mix is brought into step with NEX-Audio.";
    }

    private void ApplyWorkflowStatus()
    {
        OnPropertyChanged(nameof(IsOffline));
    }
}

public sealed record ShortcutRow(string Keys, string What);
