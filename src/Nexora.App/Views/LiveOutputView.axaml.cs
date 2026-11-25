using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexora.App.Services;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class LiveOutputView : UserControl
{
    public LiveOutputView()
    {
        InitializeComponent();
        AddMediaButton.Click += async (_, _) =>
        {
            if (DataContext is not LiveOutputViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add videos or pictures",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Videos and pictures") { Patterns = MediaLibrary.Extensions.Select(e => "*" + e).ToList() },
                    FilePickerFileTypes.All,
                ],
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            await vm.AddMediaAsync(paths);
        };
    }
}
