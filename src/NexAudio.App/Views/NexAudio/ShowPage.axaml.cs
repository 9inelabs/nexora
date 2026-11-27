using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views.NexAudio;

public partial class ShowPage : UserControl
{
    private static readonly FilePickerFileType ShowType = new("NEX-Audio show") { Patterns = ["*.nexshow"] };

    public ShowPage()
    {
        InitializeComponent();
        SaveShowButton.Click += async (_, _) =>
        {
            if (DataContext is not NexAudioViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the show",
                SuggestedFileName = vm.ShowNameText == "Untitled show" ? $"Service {DateTime.Now:yyyy-MM-dd}" : vm.ShowNameText,
                DefaultExtension = "nexshow",
                FileTypeChoices = [ShowType],
            });
            if (file?.TryGetLocalPath() is { } path) vm.SaveShowTo(path);
        };
        OpenShowButton.Click += async (_, _) =>
        {
            if (DataContext is not NexAudioViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a show", AllowMultiple = false, FileTypeFilter = [ShowType] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.InspectShow(path);
        };
    }
}
