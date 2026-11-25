using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nexora.App.Services;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class LyricsSectionView : UserControl
{
    public LyricsSectionView()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) => await ImportAsync();
        StylePanel.PickImageRequested += async () => await PickBackgroundAsync();
        // Double-click a slide: straight to the screen.
        AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (e.Source is Control c && FindTile(c) is { } tile && Vm is { } vm) vm.SendTileCommand.Execute(tile);
        }, RoutingStrategies.Bubble);
        // Typing in the search box: Enter opens the first song, Down moves out of the box.
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && Vm is { Library.Count: > 0 } vm)
            {
                vm.OpenCommand.Execute(vm.Library[0]);
                Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Focus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Focusable = true;
    }

    private LyricsSectionViewModel? Vm => DataContext as LyricsSectionViewModel;

    private static LyricTile? FindTile(Control? c)
    {
        for (; c is not null; c = c.Parent as Control)
            if (c.Tag is LyricTile t) return t;
        return null;
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task ImportAsync()
    {
        if (Vm is not { } vm || Storage is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import songs (EasyWorship Songs.db, OpenLyrics, OpenSong, SongSelect text, zip)",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Song files") { Patterns = ["*.db", "*.xml", "*.txt", "*.zip", "*.usr", "*.sng", "*"] },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();
        if (paths.Count == 0) return;
        // EasyWorship keeps the words in SongWords.db next to Songs.db: pick either one.
        await vm.ImportAsync(paths);
    }

    private async Task PickBackgroundAsync()
    {
        if (Vm is not { } vm || Storage is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a background picture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try { await vm.UseBackgroundAsync(path); }
        catch (Exception ex) { AppServices.Instance.Live.RaiseToast("The picture could not be added: " + ex.Message, true); }
    }
}
