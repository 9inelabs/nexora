using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexora.App.Output;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        AttachedToVisualTree += (_, _) => Hook();
    }

    private void Hook()
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        vm.PickImage = async () =>
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the church logo",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
            });
            if (files.Count == 0) return null;
            await using var s = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            return ms.ToArray();
        };
        vm.ListDisplays = () =>
        {
            if (top is not Window w) return [];
            return OutputWindow.OrderedDisplays(w.Screens.All)
                .Select(s => $"{s.Bounds.Width}×{s.Bounds.Height}{(s.IsPrimary ? " · main monitor" : "")}")
                .ToList();
        };
        vm.LoadDisplays();
    }
}
