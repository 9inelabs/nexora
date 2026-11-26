using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.Core.Bible;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;
using Nexora.Core.Security;

namespace Nexora.App.ViewModels;

/// <summary>Admin settings: church branding, this PC, screens, users and PINs, network.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _brandingSave;
    private bool _loading;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _brandingSave = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => SaveBranding());
        NavItems.Add(new NavItem("branding", "Church branding", "palette"));
        NavItems.Add(new NavItem("pc", "This PC", "pc"));
        NavItems.Add(new NavItem("screens", "Screens & outputs", "monitor"));
        NavItems.Add(new NavItem("users", "Users & PINs", "users"));
        NavItems.Add(new NavItem("bibles", "Bibles & Scripture", "scripture"));
        NavItems.Add(new NavItem("network", "Network", "network"));
        AddMorePages();
        Live.PropertyChanged += OnLive;
        S.Bibles.Changed += OnBiblesChanged;
        LoadBranding();
        LoadPc();
        LoadScreens();
        LoadUsers();
        LoadNetwork();
        Go(NavItems[0]);
    }

    public ObservableCollection<NavItem> NavItems { get; } = [];
    [ObservableProperty] private string _page = "branding";
    public bool IsBranding => Page == "branding";
    public bool IsPc => Page == "pc";
    public bool IsScreens => Page == "screens";
    public bool IsUsers => Page == "users";
    public bool IsNetwork => Page == "network";
    public bool IsBibles => Page == "bibles";

    partial void OnPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsBranding));
        OnPropertyChanged(nameof(IsPc));
        OnPropertyChanged(nameof(IsScreens));
        OnPropertyChanged(nameof(IsUsers));
        OnPropertyChanged(nameof(IsNetwork));
        OnPropertyChanged(nameof(IsBibles));
        OnMorePage(value);
    }

    [RelayCommand]
    private void Go(NavItem item)
    {
        foreach (var n in NavItems) n.IsSelected = n == item;
        Page = item.Key;
        if (item.Key == "network") LoadNetwork();
        if (item.Key == "bibles") _ = LoadBiblesAsync();
    }

    /// <summary>Set by the view: opens a file picker and returns the chosen image bytes.</summary>
    public Func<Task<byte[]?>>? PickImage { get; set; }

    /// <summary>Set by the view: the displays attached to this PC, in order.</summary>
    public Func<IReadOnlyList<string>>? ListDisplays { get; set; }

    [ObservableProperty] private string? _savedText = "Saved automatically";

    // ================= Church branding =================

    [ObservableProperty] private string _churchName = "";
    [ObservableProperty] private string _churchAddress = "";
    [ObservableProperty] private string _logoLine = "";
    [ObservableProperty] private bool _showOnSignIn;
    [ObservableProperty] private bool _logoWhenIdle;
    [ObservableProperty] private bool _cornerWatermark;
    [ObservableProperty] private string? _logoBase64;

    public Branding EditedBranding => new(ChurchName.Trim(), ChurchAddress.Trim(), LogoLine.Trim(), LogoBase64, ShowOnSignIn, LogoWhenIdle, CornerWatermark);
    public Branding PreviewBranding => EditedBranding with { LogoWhenIdle = true };
    public Bitmap? LogoBitmap => ScreenView.LogoBitmap(EditedBranding);
    public bool HasLogo => LogoBitmap is not null;
    public string ChurchInitials => EditedBranding.Initials;

    private void LoadBranding()
    {
        _loading = true;
        var b = Live.Branding;
        ChurchName = b.ChurchName;
        ChurchAddress = b.Address;
        LogoLine = b.LogoLine;
        ShowOnSignIn = b.ShowOnSignIn;
        LogoWhenIdle = b.LogoWhenIdle;
        CornerWatermark = b.CornerWatermark;
        LogoBase64 = b.LogoPngBase64;
        _loading = false;
        RaiseBranding();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        if (e.PropertyName is nameof(ChurchName) or nameof(ChurchAddress) or nameof(LogoLine) or nameof(ShowOnSignIn)
            or nameof(LogoWhenIdle) or nameof(CornerWatermark) or nameof(LogoBase64))
        {
            RaiseBranding();
            SavedText = "Saving…";
            _brandingSave.Stop();
            _brandingSave.Start();
        }
    }

    private void RaiseBranding()
    {
        OnPropertyChanged(nameof(EditedBranding));
        OnPropertyChanged(nameof(PreviewBranding));
        OnPropertyChanged(nameof(LogoBitmap));
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(ChurchInitials));
    }

    private async void SaveBranding()
    {
        _brandingSave.Stop();
        if (string.IsNullOrWhiteSpace(ChurchName)) { SavedText = "Church name cannot be empty"; return; }
        if (Live.Client is { } c && await c.SendAsync(new SaveBranding(EditedBranding))) SavedText = "Saved automatically";
        else SavedText = "Not saved · no connection";
    }

    [RelayCommand]
    private async Task UploadLogo()
    {
        if (PickImage is null) return;
        var bytes = await PickImage();
        if (bytes is null) return;
        if (bytes.Length > 2_000_000) { _main.ShowToast("That logo file is too large. Please use one under 2 MB.", true); return; }
        try
        {
            using var ms = new MemoryStream(bytes);
            _ = new Bitmap(ms);
        }
        catch (Exception)
        {
            _main.ShowToast("That file is not a picture NEXORA can read. Use a PNG or JPG.", true);
            return;
        }
        LogoBase64 = Convert.ToBase64String(bytes);
    }

    [RelayCommand]
    private void RemoveLogo() => LogoBase64 = null;

    // ================= This PC =================

    [ObservableProperty] private string _pcName = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _manualAddress = "";
    [ObservableProperty] private string? _pcMessage;
    [ObservableProperty] private bool _confirmReset;

    public bool IsLiveOutputPc => S.IsLiveOutputPc;
    public bool IsTerminalPc => !S.IsLiveOutputPc;
    public string PcRoleText => S.IsLiveOutputPc ? "Live Output (hosts the server, the database and the audience screens)" : "Terminal";
    public string LanAddresses => string.Join("   ", Firewall.LanAddresses().Select(a => $"{a}:{S.Config.Port}"));
    public string DataFolder => AppPaths.Root;
    public string VersionText => AppServices.Version;

    private void LoadPc()
    {
        PcName = S.Config.PcName;
        PairingCode = S.Config.PairingCode;
        ManualAddress = S.Config.ManualAddress ?? "";
    }

    [RelayCommand]
    private void SavePc()
    {
        PcMessage = null;
        if (PairingCode.Trim().Length < 4) { PcMessage = "The pairing code needs at least 4 characters."; return; }
        if (!string.IsNullOrWhiteSpace(ManualAddress) && ThisPcConfig.ParseAddress(ManualAddress, S.Config.Port) is null)
        {
            PcMessage = "That address does not look right. Example: 192.168.1.20";
            return;
        }
        var cfg = S.Config;
        var reconnect = cfg.PairingCode != PairingCode.Trim() || (cfg.ManualAddress ?? "") != ManualAddress.Trim() || cfg.PcName != PcName.Trim().ToUpperInvariant();
        cfg.PcName = string.IsNullOrWhiteSpace(PcName) ? Environment.MachineName.ToUpperInvariant() : PcName.Trim().ToUpperInvariant();
        cfg.PairingCode = PairingCode.Trim();
        cfg.ManualAddress = string.IsNullOrWhiteSpace(ManualAddress) ? null : ManualAddress.Trim();
        cfg.Save();
        PcName = cfg.PcName;
        PcMessage = S.IsLiveOutputPc
            ? "Saved. Terminals need the new pairing code the next time they connect."
            : "Saved.";
        if (reconnect && !S.IsLiveOutputPc) S.Client?.Reconnect();
    }

    [RelayCommand]
    private void NewPairingCode() => PairingCode = PinHasher.NewPairingCode();

    [RelayCommand]
    private void AllowFirewall()
    {
        var (_, msg) = Firewall.AllowNexora();
        PcMessage = msg;
    }

    [RelayCommand]
    private async Task ResetPc()
    {
        if (!ConfirmReset)
        {
            ConfirmReset = true;
            PcMessage = "Press again to set up this PC from the start. Users, songs and settings on the Live Output PC are kept.";
            return;
        }
        S.Config.Role = PcRole.NotSet;
        S.Config.Save();
        await _main.GoFirstRunAsync();
    }

    // ================= Screens & outputs =================

    public ObservableCollection<ScreenEdit> ScreenEdits { get; } = [];
    public ObservableCollection<string> DisplayOptions { get; } = [];
    public IReadOnlyList<string> KindOptions { get; } = ["Audience (full themes)", "Stage Monitor (plain, high contrast)"];
    [ObservableProperty] private string? _screensMessage;

    public void LoadDisplays()
    {
        DisplayOptions.Clear();
        DisplayOptions.Add("Window (for testing)");
        var list = ListDisplays?.Invoke() ?? [];
        for (var i = 0; i < list.Count; i++) DisplayOptions.Add($"Display {i + 1} · {list[i]}");
        for (var i = list.Count; i < 4; i++) DisplayOptions.Add($"Display {i + 1} · not connected");
    }

    private void LoadScreens()
    {
        ScreenEdits.Clear();
        foreach (var s in Live.Snapshot.Screens) ScreenEdits.Add(new ScreenEdit(s));
    }

    [RelayCommand]
    private void AddScreen()
    {
        if (ScreenEdits.Count >= 6) { ScreensMessage = "Up to 6 screens are supported."; return; }
        ScreenEdits.Add(new ScreenEdit(new ScreenInfo("s" + Guid.NewGuid().ToString("N")[..6], $"Screen {ScreenEdits.Count + 1}", ScreenKind.Audience, 0, true, ScreenEdits.Count)));
    }

    [RelayCommand]
    private void RemoveScreen(ScreenEdit s)
    {
        if (ScreenEdits.Count <= 1) { ScreensMessage = "Keep at least one screen."; return; }
        ScreenEdits.Remove(s);
    }

    [RelayCommand]
    private async Task SaveScreens()
    {
        ScreensMessage = null;
        if (ScreenEdits.Any(s => string.IsNullOrWhiteSpace(s.Name))) { ScreensMessage = "Every screen needs a name."; return; }
        var used = ScreenEdits.Where(s => s.DisplayIndex > 0).GroupBy(s => s.DisplayIndex).FirstOrDefault(g => g.Count() > 1);
        if (used is not null) { ScreensMessage = $"Two screens are set to display {used.Key}. Each display can show one screen."; return; }
        var list = ScreenEdits.Select((s, i) => s.ToInfo(i)).ToList();
        if (Live.Client is { } c && await c.SendAsync(new SaveScreens(list)))
            ScreensMessage = "Saved. The output windows move to their displays now.";
        else
            ScreensMessage = "Not saved · no connection to the Live Output PC.";
    }

    // ================= Users & PINs =================

    public ObservableCollection<UserEdit> UserEdits { get; } = [];
    [ObservableProperty] private UserEdit? _selectedUser;
    [ObservableProperty] private string? _usersMessage;
    public bool HasSelectedUser => SelectedUser is not null;
    partial void OnSelectedUserChanged(UserEdit? value)
    {
        OnPropertyChanged(nameof(HasSelectedUser));
        UsersMessage = null;
        foreach (var u in UserEdits) u.IsSelected = u == value;
    }

    private void LoadUsers()
    {
        var keep = SelectedUser?.Id;
        UserEdits.Clear();
        foreach (var u in Live.Info?.Users ?? []) UserEdits.Add(new UserEdit(u));
        SelectedUser = UserEdits.FirstOrDefault(u => u.Id == keep) ?? UserEdits.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectUser(UserEdit u) => SelectedUser = u;

    [RelayCommand]
    private void AddUser()
    {
        var u = new UserEdit(new UserInfo(Guid.NewGuid().ToString("N")[..10], "", "", Role.Scripture, "")) { IsNew = true };
        UserEdits.Add(u);
        SelectedUser = u;
    }

    [RelayCommand]
    private async Task SaveUser()
    {
        if (SelectedUser is not { } u) return;
        UsersMessage = null;
        if (string.IsNullOrWhiteSpace(u.Name)) { UsersMessage = "Please type a name."; return; }
        if (u.IsNew && !PinHasher.IsValidPin(u.Pin)) { UsersMessage = "Choose a 4-digit PIN for the new user."; return; }
        if (!u.IsNew && !string.IsNullOrEmpty(u.Pin) && !PinHasher.IsValidPin(u.Pin)) { UsersMessage = "A PIN is exactly 4 digits."; return; }
        if (u.ToInfo().Roles == Role.None) { UsersMessage = "Tick at least one role."; return; }
        var pin = string.IsNullOrEmpty(u.Pin) ? null : u.Pin;
        if (Live.Client is { } c && await c.SendAsync(new SaveUser(u.ToInfo(), pin)))
        {
            u.Pin = "";
            u.IsNew = false;
        }
        else UsersMessage = "Not saved · no connection.";
    }

    [RelayCommand]
    private async Task DeleteUser()
    {
        if (SelectedUser is not { } u) return;
        if (u.IsNew)
        {
            UserEdits.Remove(u);
            SelectedUser = UserEdits.FirstOrDefault();
            return;
        }
        if (!u.ConfirmDelete)
        {
            u.ConfirmDelete = true;
            UsersMessage = $"Press Remove again to remove {u.Name}.";
            return;
        }
        if (Live.Client is { } c) await c.SendAsync(new DeleteUser(u.Id));
    }

    // ================= Network =================

    public ObservableCollection<ClientRow> Clients { get; } = [];

    private void LoadNetwork()
    {
        Clients.Clear();
        foreach (var c in Live.Snapshot.Clients) Clients.Add(new ClientRow(c));
    }

    // ================= Bibles & Scripture =================

    public ObservableCollection<BibleRow> BibleRows { get; } = [];
    public ObservableCollection<string> TranslationOptions { get; } = [];
    public ObservableCollection<string> ThemeOptions { get; } = [];
    [ObservableProperty] private string? _defaultTranslation;
    [ObservableProperty] private string? _defaultTheme;
    [ObservableProperty] private string? _biblesMessage;
    [ObservableProperty] private bool _biblesBusy;

    // A file being imported, waiting for the admin to check the name and press Add.
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _pendingAbbrev = "";
    [ObservableProperty] private string _pendingName = "";
    [ObservableProperty] private string _pendingReport = "";
    [ObservableProperty] private string _pendingFile = "";
    [ObservableProperty] private bool _pendingComplete;
    private BibleImport.Result? _pending;

    /// <summary>Set by the view: opens a file picker and returns the chosen file's path.</summary>
    public Func<Task<string?>>? PickBibleFile { get; set; }

    private bool _loadingScripture;

    private void OnBiblesChanged() => Dispatcher.UIThread.Post(() => { if (Page == "bibles") RefreshBibles(); });

    private async Task LoadBiblesAsync()
    {
        await S.LoadBiblesAsync();
        Dispatcher.UIThread.Post(RefreshBibles);
    }

    private void RefreshBibles()
    {
        _loadingScripture = true;
        BibleRows.Clear();
        foreach (var t in S.Bibles.All)
        {
            var bundled = S.Bibles.IsBundled(t.Abbrev);
            BibleRows.Add(new BibleRow(t.Abbrev, t.Name, $"{t.Info.Verses:N0} verses · {(t.Info.Complete ? "complete" : "has gaps")}", bundled, t.Info.Complete));
        }
        TranslationOptions.Clear();
        foreach (var t in S.Bibles.All) TranslationOptions.Add(t.Abbrev);
        ThemeOptions.Clear();
        foreach (var th in Live.ScriptureThemes) ThemeOptions.Add(th.Name);
        var set = Live.ScriptureSettings;
        DefaultTranslation = TranslationOptions.FirstOrDefault(a => a.Equals(set.DefaultTranslation, StringComparison.OrdinalIgnoreCase)) ?? TranslationOptions.FirstOrDefault();
        DefaultTheme = Live.ScriptureThemes.FirstOrDefault(t => t.Id == set.DefaultThemeId)?.Name ?? ThemeOptions.FirstOrDefault();
        _loadingScripture = false;
    }

    partial void OnDefaultTranslationChanged(string? value) => SaveScripture();
    partial void OnDefaultThemeChanged(string? value) => SaveScripture();

    private void SaveScripture()
    {
        if (_loadingScripture || DefaultTranslation is null) return;
        var theme = Live.ScriptureThemes.FirstOrDefault(t => t.Name == DefaultTheme)?.Id ?? ScriptureSettings.Default.DefaultThemeId;
        var next = new ScriptureSettings(DefaultTranslation, theme);
        if (next == Live.ScriptureSettings) return;
        if (Live.Client is { } c) _ = c.SendAsync(new SaveScriptureSettings(next));
    }

    [RelayCommand]
    private async Task ChooseBibleFile()
    {
        if (PickBibleFile is null) return;
        var path = await PickBibleFile();
        if (path is null) return;
        BiblesBusy = true;
        BiblesMessage = "Reading the file and checking every verse…";
        try
        {
            var result = await Task.Run(() => BibleImport.Load(path));
            _pending = result;
            PendingFile = Path.GetFileName(path);
            PendingAbbrev = GuessAbbrev(result.Bible.Abbrev, path);
            PendingName = result.Bible.Name.Equals(result.Bible.Abbrev, StringComparison.OrdinalIgnoreCase) ? PendingAbbrev : result.Bible.Name;
            PendingComplete = result.Report.Complete;
            PendingReport = $"{result.Format}. {result.Report.Summary}";
            HasPending = true;
            BiblesMessage = null;
        }
        catch (Exception ex)
        {
            BiblesMessage = "That file could not be read: " + ex.Message;
        }
        finally { BiblesBusy = false; }
    }

    /// <summary>
    /// The file's own short name, unless the file name says better: "Bible_English_RSV.zip" gives RSV
    /// when the file inside only calls itself "BIBLEE".
    /// </summary>
    private static string GuessAbbrev(string fromFile, string path)
    {
        var own = BibleImport.CleanAbbrev(fromFile);
        var name = Path.GetFileNameWithoutExtension(path);
        if (own.Length > 0 && name.Contains(own, StringComparison.OrdinalIgnoreCase)) return own;
        var token = name.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).Reverse()
            .FirstOrDefault(t => t.Length is >= 2 and <= 8 && t.All(char.IsLetterOrDigit) && t.Any(char.IsLetter)
                && !t.Equals("bible", StringComparison.OrdinalIgnoreCase) && !t.Equals("english", StringComparison.OrdinalIgnoreCase));
        return token is null ? own : BibleImport.CleanAbbrev(token.ToUpperInvariant());
    }

    [RelayCommand]
    private void CancelBible()
    {
        _pending = null;
        HasPending = false;
    }

    [RelayCommand]
    private async Task AddBible()
    {
        if (_pending is not { } p || Live.Client is not { } c) return;
        var abbrev = BibleImport.CleanAbbrev(PendingAbbrev);
        if (abbrev.Length == 0) { BiblesMessage = "Give the translation a short name, like NIV."; return; }
        if (S.Bibles.IsBundled(abbrev)) { BiblesMessage = $"{abbrev} is already built in. Use another short name."; return; }
        BiblesBusy = true;
        BiblesMessage = $"Adding {abbrev}…";
        var tmp = Path.Combine(Path.GetTempPath(), $"nexora-{Guid.NewGuid():N}{BibleFile.Extension}");
        try
        {
            p.Bible.Abbrev = abbrev;
            p.Bible.Name = string.IsNullOrWhiteSpace(PendingName) ? abbrev : PendingName.Trim();
            await Task.Run(() => BibleFile.Write(p.Bible, tmp, PendingFile, p.Hash));
            var name = await c.UploadAsync("bibles", abbrev + BibleFile.Extension, tmp);
            // Use it on this PC at once; the other PCs copy it from the Live Output PC.
            await Task.Run(() => S.Bibles.AddFile(tmp));
            BiblesMessage = $"{abbrev} added. Every PC gets it in a moment.";
            _pending = null;
            HasPending = false;
            RefreshBibles();
        }
        catch (Exception ex)
        {
            BiblesMessage = $"{abbrev} could not be added: {ex.Message}";
        }
        finally
        {
            BiblesBusy = false;
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [RelayCommand]
    private async Task RemoveBible(BibleRow row)
    {
        if (row.Bundled) return;
        if (!row.ConfirmRemove)
        {
            foreach (var r in BibleRows) r.ConfirmRemove = false;
            row.ConfirmRemove = true;
            BiblesMessage = $"Press Remove again to remove {row.Abbrev} from every PC.";
            return;
        }
        if (Live.Client is { } c) await c.SendAsync(new RemoveBible(row.Abbrev));
        BiblesMessage = $"{row.Abbrev} removed.";
    }

    // ================= Live updates =================

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Info))
        {
            LoadUsers();
            if (!_brandingSave.IsEnabled && SavedText != "Saving…") LoadBranding();
        }
        if (e.PropertyName is nameof(Live.Library) && Page == "bibles") RefreshBibles();
        if (e.PropertyName == nameof(Live.Snapshot))
        {
            if (Page == "network") LoadNetwork();
            if (!Live.Snapshot.Screens.Select(s => s.Id).SequenceEqual(ScreenEdits.Where(s => !s.IsNewScreen).Select(s => s.Id))
                && ScreenEdits.All(s => !s.Dirty))
                LoadScreens();
        }
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnLive;
        S.Bibles.Changed -= OnBiblesChanged;
        if (_brandingSave.IsEnabled) SaveBranding();
    }
}

public sealed partial class NavItem(string key, string title, string icon) : ObservableObject
{
    public string Key => key;
    public string Title => title;
    public string Icon => icon;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ScreenEdit : ObservableObject
{
    public ScreenEdit(ScreenInfo s)
    {
        Id = s.Id;
        _name = s.Name;
        _kindIndex = s.Kind == ScreenKind.Stage ? 1 : 0;
        _displayIndex = s.Display;
        _oneTextSource = s.OneTextSource;
        _pixelWidth = s.PixelWidth > 0 ? s.PixelWidth : 1920;
        _pixelHeight = s.PixelHeight > 0 ? s.PixelHeight : 1080;
        _shapeIndex = Array.FindIndex(Presets, p => p.W * _pixelHeight == p.H * _pixelWidth && p.W > 0);
        if (_shapeIndex < 0) _shapeIndex = Presets.Length - 1;
        _useArea = s.HasArea;
        _areaX = s.AreaX;
        _areaY = s.AreaY;
        _areaWidth = s.HasArea ? s.AreaWidth : 1920;
        _areaHeight = s.HasArea ? s.AreaHeight : 1080;
        _stretchToFill = s.StretchToFill;
        _testPattern = s.TestPattern;
    }

    private static readonly (string Name, int W, int H)[] Presets =
    [
        ("Wide 16:9 (1920 × 1080)", 1920, 1080),
        ("4:3 (1440 × 1080)", 1440, 1080),
        ("Square 1:1", 1080, 1080),
        ("Tall 9:16 (portrait)", 1080, 1920),
        ("Custom pixel size", 0, 0),
    ];

    public static IReadOnlyList<string> ShapeOptions { get; } = Presets.Select(p => p.Name).ToList();

    [ObservableProperty] private int _shapeIndex;
    [ObservableProperty] private decimal _pixelWidth;
    [ObservableProperty] private decimal _pixelHeight;
    [ObservableProperty] private bool _useArea;
    [ObservableProperty] private decimal _areaX;
    [ObservableProperty] private decimal _areaY;
    [ObservableProperty] private decimal _areaWidth;
    [ObservableProperty] private decimal _areaHeight;
    [ObservableProperty] private bool _stretchToFill;
    [ObservableProperty] private bool _testPattern;

    public bool IsCustomShape => ShapeIndex == Presets.Length - 1;

    partial void OnShapeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomShape));
        if (value >= 0 && value < Presets.Length && Presets[value].W > 0)
        {
            PixelWidth = Presets[value].W;
            PixelHeight = Presets[value].H;
        }
    }

    /// <summary>What the picture looks like in words, under the shape choice.</summary>
    public string ShapeNote
    {
        get
        {
            var (w, h) = ScreenInfo.CanvasFor((int)PixelWidth, (int)PixelHeight);
            var shape = Math.Abs(w - h) < 1 ? "square" : w > h ? $"{w / h:0.##} times as wide as tall" : $"{h / w:0.##} times as tall as wide";
            return $"Words, pictures and videos are laid out for a {shape} picture.";
        }
    }

    partial void OnPixelWidthChanged(decimal value) => OnPropertyChanged(nameof(ShapeNote));
    partial void OnPixelHeightChanged(decimal value) => OnPropertyChanged(nameof(ShapeNote));

    public string Id { get; }
    public bool IsNewScreen { get; init; }
    public bool Dirty { get; private set; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _displayIndex;
    [ObservableProperty] private bool _oneTextSource;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Dirty) or nameof(ShapeNote) or nameof(IsCustomShape))) Dirty = true;
    }

    public ScreenInfo ToInfo(int order)
    {
        int pw = (int)Math.Clamp(PixelWidth, 16, 16384), ph = (int)Math.Clamp(PixelHeight, 16, 16384);
        var wide = pw * 1080 == ph * 1920;
        return new(Id, Name.Trim(), KindIndex == 1 ? ScreenKind.Stage : ScreenKind.Audience, Math.Max(0, DisplayIndex), OneTextSource, order,
            PixelWidth: wide ? 0 : pw, PixelHeight: wide ? 0 : ph,
            AreaX: UseArea ? (int)AreaX : 0, AreaY: UseArea ? (int)AreaY : 0,
            AreaWidth: UseArea ? (int)Math.Max(16, AreaWidth) : 0, AreaHeight: UseArea ? (int)Math.Max(16, AreaHeight) : 0,
            TestPattern: TestPattern, StretchToFill: StretchToFill);
    }
}

public sealed partial class UserEdit : ObservableObject
{
    public UserEdit(UserInfo u)
    {
        Id = u.Id;
        _name = u.Name;
        _title = u.Title;
        _admin = u.Roles.HasFlag(Role.Admin);
        _liveOutput = u.Roles.HasFlag(Role.LiveOutput);
        _scripture = u.Roles.HasFlag(Role.Scripture);
        _lyrics = u.Roles.HasFlag(Role.Lyrics);
        _camera = u.Roles.HasFlag(Role.Camera);
        _announcements = u.Roles.HasFlag(Role.Announcements);
        _lowerThirds = u.Roles.HasFlag(Role.LowerThirds);
        _countdown = u.Roles.HasFlag(Role.Countdown);
        _audio = u.Roles.HasFlag(Role.Audio);
        _trainee = u.Roles.HasFlag(Role.Trainee);
    }

    public string Id { get; }
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _confirmDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials), nameof(DisplayName))]
    private string _name;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _admin;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _liveOutput;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _scripture;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _lyrics;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _camera;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _announcements;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _lowerThirds;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _countdown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _audio;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RolesText))] private bool _trainee;

    public string Initials => UserInfo.MakeInitials(string.IsNullOrWhiteSpace(Name) ? "?" : Name);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "New user" : Name;
    public string PinHint => IsNew ? "4 digits" : "Leave empty to keep the current PIN";

    public string RolesText
    {
        get
        {
            if (Admin) return "Admin";
            var parts = new List<string>();
            if (LiveOutput) parts.Add("Live Output");
            if (Scripture) parts.Add("Scripture");
            if (Lyrics) parts.Add("Lyrics");
            if (Camera) parts.Add("Camera");
            if (Announcements) parts.Add("Announcements");
            if (LowerThirds) parts.Add("Lower Thirds");
            if (Countdown) parts.Add("Countdown");
            if (Audio) parts.Add("NEX-Audio");
            var text = parts.Count == 0 ? "No roles" : string.Join(", ", parts);
            return Trainee ? "Trainee · " + text : text;
        }
    }

    partial void OnIsNewChanged(bool value) => OnPropertyChanged(nameof(PinHint));

    public UserInfo ToInfo()
    {
        var r = Role.None;
        if (Admin) r |= Role.Admin;
        if (LiveOutput) r |= Role.LiveOutput;
        if (Scripture) r |= Role.Scripture;
        if (Lyrics) r |= Role.Lyrics;
        if (Camera) r |= Role.Camera;
        if (Announcements) r |= Role.Announcements;
        if (LowerThirds) r |= Role.LowerThirds;
        if (Countdown) r |= Role.Countdown;
        if (Audio) r |= Role.Audio;
        if (Trainee) r |= Role.Trainee;
        return new UserInfo(Id, Name.Trim(), Initials, r, Title.Trim());
    }
}

public sealed class ClientRow(ClientInfo c)
{
    public string PcName => c.PcName;
    public string Kind => c.Kind == ClientKind.OutputEngine ? "Output engine" : "Terminal";
    public string User => c.UserName ?? "Not signed in";
    public string Address => c.Address.Replace("::ffff:", "");
    public string Sections => c.Sections.Count == 0 ? "—" : string.Join(", ", c.Sections.Select(s => Nexora.Core.Model.Sections.Get(s).Title));
    public string Version => c.AppVersion;
}

public sealed partial class BibleRow(string abbrev, string name, string detail, bool bundled, bool complete) : ObservableObject
{
    public string Abbrev => abbrev;
    public string Name => name;
    public string Detail => detail;
    public bool Bundled => bundled;
    public bool Imported => !bundled;
    public bool Complete => complete;
    public string Kind => bundled ? "Built in" : "Added";
    [ObservableProperty] private bool _confirmRemove;
}
