using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexora.App.Services;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class AnnouncementsSectionView : UserControl
{
    public AnnouncementsSectionView()
    {
        InitializeComponent();
        PickPicture.Click += async (_, _) => await PickAsync(false);
        StylePanel.PickImageRequested += async () => await PickAsync(true);
    }

    private async Task PickAsync(bool background)
    {
        if (DataContext is not AnnouncementsSectionViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = background ? "Choose a background picture" : "Choose a picture for this slide",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try { await vm.UsePictureAsync(path, background); }
        catch (Exception ex) { AppServices.Instance.Live.RaiseToast("The picture could not be added: " + ex.Message, true); }
    }
}
