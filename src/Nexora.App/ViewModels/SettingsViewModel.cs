using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Services;
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
        NavItems.Add(new NavItem("network", "Network", "network"));
        Live.PropertyChanged += OnLive;
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

    partial void OnPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsBranding));
        OnPropertyChanged(nameof(IsPc));
        OnPropertyChanged(nameof(IsScreens));
        OnPropertyChanged(nameof(IsUsers));
        OnPropertyChanged(nameof(IsNetwork));
    }

    [RelayCommand]
    private void Go(NavItem item)
    {
        foreach (var n in NavItems) n.IsSelected = n == item;
        Page = item.Key;
        if (item.Key == "network") LoadNetwork();
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

    // ================= Live updates =================

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Info))
        {
            LoadUsers();
            if (!_brandingSave.IsEnabled && SavedText != "Saving…") LoadBranding();
        }
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
    }

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
        if (e.PropertyName != nameof(Dirty)) Dirty = true;
    }

    public ScreenInfo ToInfo(int order) => new(Id, Name.Trim(), KindIndex == 1 ? ScreenKind.Stage : ScreenKind.Audience, Math.Max(0, DisplayIndex), OneTextSource, order);
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
