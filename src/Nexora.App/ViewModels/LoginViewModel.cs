using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Net;

namespace Nexora.App.ViewModels;

/// <summary>Centred card: church branding, choose your name, type your 4-digit PIN.</summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        for (var i = 0; i < 4; i++) PinSlots.Add(new PinSlot());
        Live.PropertyChanged += OnLive;
        RefreshUsers();
        UpdateSlots();
    }

    public ObservableCollection<UserChoice> Users { get; } = [];
    public ObservableCollection<PinSlot> PinSlots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private UserChoice? _selectedUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private string _pin = "";

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;

    public bool CanSignIn => SelectedUser is not null && Pin.Length == 4 && !Busy && Live.IsConnected;

    public Branding Branding => Live.Branding;
    public string ChurchName => Branding.ChurchName;
    public string Address => Branding.Address;
    public string ChurchInitials => Branding.Initials;
    public Bitmap? Logo => ScreenView.LogoBitmap(Branding);
    public bool HasLogo => Logo is not null;
    public bool ShowBranding => Branding.ShowOnSignIn;

    public string PcLine => S.Config.PcName;
    public string LinkText => (S.IsLiveOutputPc, Live.Status) switch
    {
        (true, ConnectionStatus.Connected) => "Live Output · ready",
        (false, ConnectionStatus.Connected) => $"Connected to {Live.LinkText}",
        _ => Live.LinkText,
    };
    public bool LinkOk => Live.Status == ConnectionStatus.Connected;
    public bool LinkBad => Live.Status is ConnectionStatus.Rejected or ConnectionStatus.Reconnecting;
    public bool LinkWaiting => Live.Status is ConnectionStatus.Searching or ConnectionStatus.Connecting;
    public bool ShowConnectionHelp => !S.IsLiveOutputPc && Live.Status is ConnectionStatus.Rejected or ConnectionStatus.Searching;
    public bool NoUsers => Live.IsConnected && Users.Count == 0;
    public string Version => Nexora.App.Services.AppServices.Version;

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Live.Info))
        {
            RefreshUsers();
            OnPropertyChanged(nameof(Branding));
            OnPropertyChanged(nameof(ChurchName));
            OnPropertyChanged(nameof(Address));
            OnPropertyChanged(nameof(ChurchInitials));
            OnPropertyChanged(nameof(Logo));
            OnPropertyChanged(nameof(HasLogo));
            OnPropertyChanged(nameof(ShowBranding));
        }
        if (e.PropertyName is nameof(Live.LinkText) or nameof(Live.Status) or nameof(Live.IsConnected))
        {
            OnPropertyChanged(nameof(LinkText));
            OnPropertyChanged(nameof(LinkOk));
            OnPropertyChanged(nameof(LinkBad));
            OnPropertyChanged(nameof(LinkWaiting));
            OnPropertyChanged(nameof(ShowConnectionHelp));
            OnPropertyChanged(nameof(CanSignIn));
            OnPropertyChanged(nameof(NoUsers));
        }
    }

    private void RefreshUsers()
    {
        var keep = SelectedUser?.Id;
        Users.Clear();
        foreach (var u in Live.Info?.Users ?? [])
            Users.Add(new UserChoice(u));
        SelectedUser = Users.FirstOrDefault(u => u.Id == keep);
        foreach (var u in Users) u.IsSelected = u == SelectedUser;
        OnPropertyChanged(nameof(NoUsers));
    }

    [RelayCommand]
    private void SelectUser(UserChoice user)
    {
        SelectedUser = user;
        foreach (var u in Users) u.IsSelected = u == user;
        Pin = "";
        Error = null;
        UpdateSlots();
    }

    public void TypeDigit(char d)
    {
        if (Busy || !char.IsAsciiDigit(d)) return;
        if (SelectedUser is null)
        {
            Error = "Choose your name first.";
            return;
        }
        if (Pin.Length >= 4) return;
        Error = null;
        Pin += d;
        UpdateSlots();
        if (Pin.Length == 4) _ = SignIn();
    }

    public void Backspace()
    {
        if (Pin.Length == 0 || Busy) return;
        Pin = Pin[..^1];
        UpdateSlots();
    }

    /// <summary>Left and right arrows move between names, for keyboard-only sign-in.</summary>
    public void MoveUser(int delta)
    {
        if (Users.Count == 0) return;
        var i = SelectedUser is null ? (delta > 0 ? -1 : Users.Count) : Users.IndexOf(SelectedUser);
        i = Math.Clamp(i + delta, 0, Users.Count - 1);
        SelectUser(Users[i]);
    }

    private void UpdateSlots()
    {
        for (var i = 0; i < 4; i++)
        {
            PinSlots[i].Filled = i < Pin.Length;
            PinSlots[i].Active = SelectedUser is not null && i == Pin.Length;
        }
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (SelectedUser is null || Pin.Length != 4 || Busy) return;
        if (Live.Client is not { } client) return;
        Busy = true;
        Error = null;
        try
        {
            var r = await client.LoginAsync(SelectedUser.Id, Pin);
            if (r.Ok)
            {
                // The client's own user update reaches the UI a moment later; don't open the picker without it.
                Live.User = r.User;
                _main.GoSectionPicker();
                return;
            }
            Error = r.Error;
            Pin = "";
            UpdateSlots();
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(CanSignIn));
        }
    }

    [RelayCommand]
    private Task ConnectionSettings() => _main.GoFirstRunAsync(terminalStep: true);

    public override void OnLeave() => Live.PropertyChanged -= OnLive;
}

public sealed partial class UserChoice(UserInfo user) : ObservableObject
{
    public string Id => user.Id;
    public string Name => user.Name.Split(' ')[0];
    public string Initials => user.Initials;
    public UserInfo User => user;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PinSlot : ObservableObject
{
    [ObservableProperty] private bool _filled;
    [ObservableProperty] private bool _active;
}
