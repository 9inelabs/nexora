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
        vm.PickBibleFile = async () =>
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a Bible file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Bible files") { Patterns = ["*.xml", "*.zip", "*.usfm", "*.sfm", "*.sqlite", "*.sqlite3", "*.db", "*.SQLite3", "*.json", "*.gz", "*.csv", "*.tsv", "*.txt", "*.osis", "*.nxbible"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };
        vm.ListDisplays = () =>
        {
            if (top is not Window w) return [];
            return OutputWindow.OrderedDisplays(w.Screens.All)
                .Select(s => $"{s.Bounds.Width}×{s.Bounds.Height}{(s.IsPrimary ? " · main monitor" : "")}")
                .ToList();
        };
        vm.SaveFileAs = async (name, ext) =>
        {
            var f = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = name,
                DefaultExtension = ext,
                FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = ["*." + ext] }],
            });
            return f?.TryGetLocalPath();
        };
        vm.OpenFileOf = async (title, patterns) =>
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All],
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };
        vm.PickFolder = async title =>
        {
            var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return dirs.Count == 0 ? null : dirs[0].TryGetLocalPath();
        };
        vm.LoadDisplays();
    }
}
