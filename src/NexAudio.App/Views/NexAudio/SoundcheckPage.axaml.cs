using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views.NexAudio;

public partial class SoundcheckPage : UserControl
{
    public SoundcheckPage()
    {
        InitializeComponent();
        // A drag on the position seeks once, when it is let go.
        PositionSlider.AddHandler(PointerPressedEvent, (_, _) => (DataContext as NexAudioViewModel)?.BeginSeek(), RoutingStrategies.Tunnel);
        PositionSlider.AddHandler(PointerReleasedEvent, (_, _) => (DataContext as NexAudioViewModel)?.EndSeek(), RoutingStrategies.Tunnel);
        ImportButton.Click += async (_, _) =>
        {
            if (DataContext is not NexAudioViewModel vm || await PickFolder("A folder of WAV files, one per channel") is not { } f) return;
            vm.ImportTake(f);
        };
        FolderButton.Click += async (_, _) =>
        {
            if (DataContext is not NexAudioViewModel vm || await PickFolder("Where recordings go") is not { } f) return;
            vm.UseTapeFolder(f);
        };
    }

    private async Task<string?> PickFolder(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return null;
        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }
}
