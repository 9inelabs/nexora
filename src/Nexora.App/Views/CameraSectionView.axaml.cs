using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class CameraSectionView : UserControl
{
    public CameraSectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CameraSectionViewModel vm) vm.ProgramSurface = ProgramSurface;
        };
        AddFile.Click += async (_, _) =>
        {
            if (DataContext is not CameraSectionViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a video to use as a practice camera",
                FileTypeFilter = [new FilePickerFileType("Videos") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.wmv", "*.m4v"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.AddTestFile(path);
        };
    }
}
