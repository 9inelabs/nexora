using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Security;
using Nexora.Server;

namespace Nexora.App.ViewModels;

/// <summary>"This PC is the Live Output" or "This PC is a Terminal", then the few details each needs.</summary>
public sealed partial class FirstRunViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _scan;

    public FirstRunViewModel(MainViewModel main, bool terminalStep = false)
    {
        _main = main;
        var cfg = S.Config;
        _pcName = cfg.PcName;
        _pairingCode = cfg.PairingCode;
        _manualAddress = cfg.ManualAddress ?? "";
        if (terminalStep) Step = 2;
        try
        {
            if (File.Exists(AppPaths.LibraryFile))
            {
                using var store = new LibraryStore(AppPaths.LibraryFile);
                HasExistingAdmin = store.HasAdmin();
                if (store.Branding.ChurchName != Branding.Default.ChurchName) _churchName = store.Branding.ChurchName;
                _address = store.Branding.Address;
            }
        }
        catch (Exception) { }
        _scan = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshServers());
        _scan.Start();
        RefreshServers();
    }

    /// <summary>0 = choose role, 1 = Live Output details, 2 = Terminal details.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsLiveOutput), nameof(IsTerminal))]
    private int _step;

    public bool IsChoose => Step == 0;
    public bool IsLiveOutput => Step == 1;
    public bool IsTerminal => Step == 2;

    [ObservableProperty] private string _pcName;
    [ObservableProperty] private string _pairingCode;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;

    // Live Output
    [ObservableProperty] private string _churchName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _adminName = "";
    [ObservableProperty] private string _adminPin = "";
    [ObservableProperty] private string _adminPin2 = "";

    // Terminal
    [ObservableProperty] private string _manualAddress;
    [ObservableProperty] private ServerChoice? _selectedServer;
    public ObservableCollection<ServerChoice> Servers { get; } = [];

    [ObservableProperty] private bool _noServersFound = true;

    public string Version => AppServices.Version;

    /// <summary>This PC was a Live Output before: keep its users, no new admin needed.</summary>
    public bool HasExistingAdmin { get; }
    public bool NeedsAdmin => !HasExistingAdmin;

    [RelayCommand]
    private void ChooseLiveOutput()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(PairingCode)) PairingCode = PinHasher.NewPairingCode();
        Step = 1;
    }
    [RelayCommand] private void ChooseTerminal() { Error = null; Step = 2; }
    [RelayCommand] private void Back() { Error = null; Step = 0; }
    [RelayCommand] private void SelectServer(ServerChoice s) => SelectedServer = s;

    partial void OnSelectedServerChanged(ServerChoice? value)
    {
        foreach (var s in Servers) s.IsSelected = s == value;
    }

    [RelayCommand] private void NewCode() => PairingCode = PinHasher.NewPairingCode();

    private void RefreshServers()
    {
        var found = S.Discovery?.Servers ?? [];
        foreach (var gone in Servers.Where(s => found.All(f => f.Info.ServerId != s.ServerId)).ToList()) Servers.Remove(gone);
        foreach (var f in found)
        {
            if (Servers.Any(s => s.ServerId == f.Info.ServerId)) continue;
            Servers.Add(new ServerChoice(f.Info.ServerId, f.Info.ServerName, f.Info.ChurchName, f.Address.ToString()));
        }
        if (SelectedServer is null && Servers.Count > 0)
            SelectedServer = Servers.FirstOrDefault(s => s.ServerId == S.Config.ServerId) ?? Servers[0];
        NoServersFound = Servers.Count == 0;
    }

    [RelayCommand]
    private async Task FinishLiveOutput()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(ChurchName)) { Error = "Please type the church name."; return; }
        if (NeedsAdmin)
        {
            if (string.IsNullOrWhiteSpace(AdminName)) { Error = "Please type your name for the first admin account."; return; }
            if (!PinHasher.IsValidPin(AdminPin)) { Error = "The PIN must be exactly 4 digits."; return; }
            if (AdminPin != AdminPin2) { Error = "The two PINs are not the same."; return; }
        }
        if (PairingCode.Trim().Length < 4) { Error = "Please choose a pairing code of at least 4 characters."; return; }

        Busy = true;
        try
        {
            using (var store = new LibraryStore(AppPaths.LibraryFile))
            {
                var b = store.Branding;
                store.Branding = b with { ChurchName = ChurchName.Trim(), Address = Address.Trim() };
                if (NeedsAdmin)
                    store.SaveUser(new UserInfo(Guid.NewGuid().ToString("N")[..10], AdminName.Trim(), UserInfo.MakeInitials(AdminName),
                    Role.Admin | Role.LiveOutput | Role.Scripture | Role.Lyrics | Role.Camera | Role.Announcements | Role.LowerThirds | Role.Countdown | Role.Audio,
                    "Studio manager"), AdminPin);
            }
            var cfg = S.Config;
            cfg.Role = PcRole.LiveOutput;
            cfg.PcName = Clean(PcName);
            cfg.PairingCode = PairingCode.Trim();
            cfg.ManualAddress = null;
            cfg.Save();
            _scan.Stop();
            await _main.StartServicesAsync();
        }
        catch (Exception ex)
        {
            Error = "Could not finish set-up: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task FinishTerminal()
    {
        Error = null;
        if (PairingCode.Trim().Length < 4) { Error = "Type the pairing code shown in Settings on the Live Output PC."; return; }
        var manual = string.IsNullOrWhiteSpace(ManualAddress) ? null : ManualAddress.Trim();
        if (manual is not null && ThisPcConfig.ParseAddress(manual, S.Config.Port) is null) { Error = "That address does not look right. Example: 192.168.1.20"; return; }
        if (manual is null && SelectedServer is null) { Error = "No Live Output PC found yet. Start NEXORA on the Live Output PC, or type its address below."; return; }

        var cfg = S.Config;
        cfg.Role = PcRole.Terminal;
        cfg.PcName = Clean(PcName);
        cfg.PairingCode = PairingCode.Trim();
        cfg.ManualAddress = manual;
        cfg.ServerId = manual is null ? SelectedServer?.ServerId : null;
        cfg.Save();
        _scan.Stop();
        await _main.StartServicesAsync();
    }

    private static string Clean(string name) =>
        string.IsNullOrWhiteSpace(name) ? Environment.MachineName.ToUpperInvariant() : name.Trim().ToUpperInvariant();

    public override void OnLeave() => _scan.Stop();
}

public sealed partial class ServerChoice(string serverId, string name, string churchName, string address) : ObservableObject
{
    public string ServerId => serverId;
    public string Name => name;
    public string Sub => $"{churchName} · {address}";
    [ObservableProperty] private bool _isSelected;
}
