using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Core.Model;
using Nexora.Core.Net;

namespace NexAudio.App.ViewModels;

/// <summary>NEX-Audio's sign-in: the church's NEXORA users and PINs, checked by the Live Output PC (or by this PC when offline).</summary>
public sealed partial class SignInViewModel : ViewModelBase
{
    public SignInViewModel()
    {
        for (var i = 0; i < 4; i++) PinSlots.Add(new PinSlot());
        Live.PropertyChanged += OnLive;
        PairingCode = Live.Config.PairingCode;
        Address = Live.Config.ManualAddress ?? "";
        RefreshUsers();
        UpdateSlots();
    }

    public ObservableCollection<UserChoice> Users { get; } = [];
    public ObservableCollection<PinSlot> PinSlots { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSignIn))] private UserChoice? _selectedUser;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSignIn))] private string _pin = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSignIn))] private bool _busy;
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private bool _editingConnection;

    public bool CanSignIn => SelectedUser is not null && Pin.Length == 4 && !Busy;
    public string LinkText => Live.LinkText;
    public bool LinkOk => Live.Status == ConnectionStatus.Connected;
    public bool LinkBad => Live.Status is ConnectionStatus.Rejected or ConnectionStatus.Reconnecting;
    public bool LinkWaiting => !LinkOk && !LinkBad;
    public string ChurchName => Live.Branding?.ChurchName is { Length: > 0 } n ? n : "NEX-Audio";
    public bool NoUsers => Users.Count == 0;
    public string NoUsersText => Live.IsConnected
        ? "No users yet. Ask the admin to add you in NEXORA's Settings."
        : "The Live Output PC has not been reached yet, and nobody has signed in on this PC before.";
    public bool ShowConnection => EditingConnection || !Live.HasPairingCode || Live.Status == ConnectionStatus.Rejected;
    public bool OfflineNote => !Live.IsConnected && Users.Count > 0;
    public string Version => DeskServices.Version;
    public string PcLine => Live.Config.PcName;

    partial void OnEditingConnectionChanged(bool value) => OnPropertyChanged(nameof(ShowConnection));

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Session.Users)) RefreshUsers();
        if (e.PropertyName is nameof(Session.Status) or nameof(Session.LinkText) or nameof(Session.Branding))
        {
            OnPropertyChanged(nameof(LinkText));
            OnPropertyChanged(nameof(LinkOk));
            OnPropertyChanged(nameof(LinkBad));
            OnPropertyChanged(nameof(LinkWaiting));
            OnPropertyChanged(nameof(ChurchName));
            OnPropertyChanged(nameof(ShowConnection));
            OnPropertyChanged(nameof(OfflineNote));
            OnPropertyChanged(nameof(NoUsersText));
        }
    }

    private void RefreshUsers()
    {
        var keep = SelectedUser?.Id;
        Users.Clear();
        // Only people who may use the desk: admins, sound, and the stream volunteers.
        foreach (var u in Live.Users.Where(u => Sections.UserMayOpen(u.Roles, SectionKind.Audio)))
            Users.Add(new UserChoice(u));
        SelectedUser = Users.FirstOrDefault(u => u.Id == keep);
        foreach (var u in Users) u.IsSelected = u == SelectedUser;
        OnPropertyChanged(nameof(NoUsers));
        OnPropertyChanged(nameof(OfflineNote));
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

    public void MoveUser(int delta)
    {
        if (Users.Count == 0) return;
        var i = SelectedUser is null ? (delta > 0 ? -1 : Users.Count) : Users.IndexOf(SelectedUser);
        SelectUser(Users[Math.Clamp(i + delta, 0, Users.Count - 1)]);
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
        Busy = true;
        Error = null;
        try
        {
            Error = await Live.SignInAsync(SelectedUser.User, Pin);
            if (Error is not null)
            {
                Pin = "";
                UpdateSlots();
            }
        }
        finally { Busy = false; }
    }

    [RelayCommand] private void EditConnection() => EditingConnection = !EditingConnection;

    [RelayCommand]
    private void SaveConnection()
    {
        if (PairingCode.Trim().Length == 0)
        {
            Error = "Type the pairing code from NEXORA's Settings on the Live Output PC.";
            return;
        }
        Error = null;
        Live.SetConnection(PairingCode, Address);
        EditingConnection = false;
        OnPropertyChanged(nameof(ShowConnection));
    }

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
