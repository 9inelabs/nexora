using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views.NexAudio;

public partial class ChannelPage : UserControl
{
    private static readonly FilePickerFileType WavType = new("WAV sample") { Patterns = ["*.wav", "*.WAV"] };

    public ChannelPage()
    {
        InitializeComponent();
        ImportDrumsButton.Click += async (_, _) =>
        {
            if (DataContext is not NexAudioViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add drum samples", AllowMultiple = true, FileTypeFilter = [WavType] });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            await vm.ImportDrums(paths);
        };
    }
}
