using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexora.App.Services;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class LowerThirdsSectionView : UserControl
{
    public LowerThirdsSectionView()
    {
        InitializeComponent();
        PickPhoto.Click += async (_, _) =>
        {
            if (DataContext is not LowerThirdsSectionViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a photo",
                FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            try { await vm.UsePhotoAsync(path); }
            catch (Exception ex) { AppServices.Instance.Live.RaiseToast("The photo could not be added: " + ex.Message, true); }
        };
    }
}
