using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NexAudio.App.ViewModels;

/// <summary>Who is signed in, signing out, and updating NEX-Audio (from the menu at the top right).</summary>
public sealed partial class NexAudioViewModel
{
    public string UserName => Live.User?.Name ?? "";
    public string UserInitials => Live.User?.Initials ?? "?";
    public bool SignedInOffline => Live.Offline;
    public string AccountNote => Live.Offline
        ? "Signed in on this PC only: the Live Output PC could not be reached."
        : Live.LinkText;
    public string VersionText => $"NEX-Audio {DeskServices.Version}";

    [ObservableProperty] private string _updateText = "";

    /// <summary>A newer NEX-Audio is ready: the version, shown as a button at the top right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText))]
    private string? _updateVersion;

    public bool HasUpdate => UpdateVersion is not null;
    public string UpdateButtonText => $"Update to {UpdateVersion}";
    private Avalonia.Threading.DispatcherTimer? _updateWatch;

    /// <summary>Looks for a new version soon after the desk opens, then every 30 minutes, like NEXORA.</summary>
    private void WatchForUpdates()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LookForUpdate()));
        _updateWatch = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMinutes(30), Avalonia.Threading.DispatcherPriority.Background, (_, _) => _ = LookForUpdate());
        _updateWatch.Start();
    }

    private async Task LookForUpdate()
    {
        try
        {
            if (await DeskUpdater.FindAsync() is { } f) UpdateVersion = f.Version;
        }
        catch (Exception) { }
    }

    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SignedInOffline));
        OnPropertyChanged(nameof(AccountNote));
        if (e.PropertyName == nameof(Session.User))
        {
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(UserInitials));
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(CanMix));
            OnPropertyChanged(nameof(CanStream));
        }
    }

    [RelayCommand]
    private async Task SignOut() => await Live.SignOutAsync();

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        UpdateText = "Looking for a new version…";
        var found = await DeskUpdater.FindAsync();
        UpdateVersion = found?.Version;
        UpdateText = found is { } f
            ? $"NEX-Audio {f.Version} is ready on {f.From}. Stop the sound, then press Update."
            : $"NEX-Audio is up to date ({DeskServices.Version}).";
    }

    [RelayCommand]
    private async Task Update()
    {
        if (SoundOn)
        {
            UpdateText = Notice = "Stop the sound first (top right), then press Update. NEX-Audio never updates during a service.";
            return;
        }
        UpdateText = Notice = await DeskUpdater.InstallAsync(SoundOn, t => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateText = Notice = t));
    }
}
