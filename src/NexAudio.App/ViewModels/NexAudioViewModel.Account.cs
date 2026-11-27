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
        UpdateText = found is { } f
            ? $"NEX-Audio {f.Version} is ready on {f.From}. Stop the sound, then press Update."
            : $"NEX-Audio is up to date ({DeskServices.Version}).";
    }

    [RelayCommand]
    private async Task Update()
    {
        UpdateText = await DeskUpdater.InstallAsync(SoundOn, t => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateText = t));
    }
}
