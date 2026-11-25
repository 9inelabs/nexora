using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class ScriptureSectionView : UserControl
{
    public ScriptureSectionView()
    {
        InitializeComponent();
        RefBox.AddHandler(KeyDownEvent, OnRefKey, RoutingStrategies.Tunnel);
        RefBox.GotFocus += (_, _) => Dispatcher.UIThread.Post(RefBox.SelectAll);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => RefBox.Focus(), DispatcherPriority.Input);
        PickImage.Click += async (_, _) => await PickBackgroundAsync();
        ExportTheme.Click += async (_, _) => await ExportAsync();
        ImportTheme.Click += async (_, _) => await ImportAsync();
        DataContextChanged += (_, _) =>
        {
            if (_hooked is not null) _hooked.ScrollToRow -= OnScrollToRow;
            _hooked = Vm;
            if (_hooked is not null) _hooked.ScrollToRow += OnScrollToRow;
        };
    }

    private ScriptureSectionViewModel? _hooked;

    private void OnScrollToRow(int index) => Dispatcher.UIThread.Post(() =>
    {
        if (VerseList.ContainerFromIndex(index) is not Control c) return;
        // Keep a little of the verse before in sight.
        var top = c.TranslatePoint(new Point(0, 0), VerseList)?.Y ?? 0;
        var bottom = top + c.Bounds.Height;
        var view = VerseScroll.Viewport.Height;
        var off = VerseScroll.Offset.Y;
        if (top < off || bottom > off + view)
            VerseScroll.Offset = new Vector(0, Math.Max(0, top - Math.Min(80, view / 4)));
    }, DispatcherPriority.Background);

    private ScriptureSectionViewModel? Vm => DataContext as ScriptureSectionViewModel;

    private void OnRefKey(object? sender, KeyEventArgs e)
    {
        if (Vm is { } vm && vm.HandleReferenceBoxKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

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
        catch (Exception ex) { Nexora.App.Services.AppServices.Instance.Live.RaiseToast("The picture could not be added: " + ex.Message, true); }
    }

    private async Task ExportAsync()
    {
        if (Vm is not { } vm || Storage is not { } sp) return;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export theme",
            SuggestedFileName = (vm.Editor.Name is { Length: > 0 } n ? n : "theme") + ".nxtheme",
            DefaultExtension = "nxtheme",
            FileTypeChoices = [new FilePickerFileType("NEXORA theme") { Patterns = ["*.nxtheme"] }],
        });
        if (file is null) return;
        try
        {
            await using var s = await file.OpenWriteAsync();
            await using var w = new StreamWriter(s);
            await w.WriteAsync(vm.ExportTheme());
            Nexora.App.Services.AppServices.Instance.Live.RaiseToast("Theme exported.", false);
        }
        catch (Exception ex) { Nexora.App.Services.AppServices.Instance.Live.RaiseToast("The theme could not be saved: " + ex.Message, true); }
    }

    private async Task ImportAsync()
    {
        if (Vm is not { } vm || Storage is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import theme",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("NEXORA theme") { Patterns = ["*.nxtheme", "*.json"] }],
        });
        if (files.Count == 0) return;
        try
        {
            await using var s = await files[0].OpenReadAsync();
            using var r = new StreamReader(s);
            await vm.ImportThemeAsync(await r.ReadToEndAsync());
        }
        catch (Exception ex) { Nexora.App.Services.AppServices.Instance.Live.RaiseToast("That file is not a NEXORA theme. " + ex.Message, true); }
    }
}
