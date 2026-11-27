using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NexAudio.App.ViewModels;

/// <summary>Sign-in until someone is signed in, then the desk. Signing out leaves the sound running.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase? _current;

    public ShellViewModel()
    {
        Live.PropertyChanged += OnLive;
        Show();
    }

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Session.User)) Show();
    }

    private void Show()
    {
        var wantDesk = Live.User is not null;
        if (wantDesk && Current is NexAudioViewModel) return;
        if (!wantDesk && Current is SignInViewModel) return;
        Current?.OnLeave();
        Current = wantDesk ? new NexAudioViewModel() : new SignInViewModel();
    }
}
