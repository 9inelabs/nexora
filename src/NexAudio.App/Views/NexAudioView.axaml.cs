using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NexAudio.App.ViewModels;
using NexAudio.App.Views.NexAudio;

namespace NexAudio.App.Views;

public partial class NexAudioView : UserControl
{
    private NexAudioViewModel? _vm;
    private readonly Dictionary<string, Window> _windows = [];

    public NexAudioView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PopOutRequested -= PopOut;
            _vm.PropertyChanged -= OnVm;
        }
        _vm = DataContext as NexAudioViewModel;
        if (_vm is not null)
        {
            _vm.PopOutRequested += PopOut;
            _vm.PropertyChanged += OnVm;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Signing out closes the pages that were on the other screen.
        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NexAudioViewModel.QuickOpen) && _vm?.QuickOpen == true)
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("QuickBox")?.Focus(), DispatcherPriority.Input);
    }

    private void OnQuickBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && _vm is not null) _vm.QuickOpen = false;
    }

    /// <summary>Opens a page in a window of its own, on another screen when there is one. Pressing again brings it forward.</summary>
    private void PopOut(string what)
    {
        if (_vm is null) return;
        if (_windows.TryGetValue(what, out var open))
        {
            open.Activate();
            return;
        }
        Control page = what switch
        {
            "routing" => new RoutingPage(),
            "channel" => new ChannelPage(),
            _ => new BridgePage(),
        };
        var title = what switch { "routing" => "Routing", "channel" => "Channel", _ => "Meter bridge" };
        var w = new Window
        {
            Title = $"NEX-Audio · {title}",
            Width = what == "bridge" ? 1500 : 1300,
            Height = what == "bridge" ? 520 : 860,
            MinWidth = 640,
            MinHeight = 320,
            DataContext = _vm,
            Content = new Border { Padding = new Thickness(12), Child = page },
            Background = this.TryFindResource("Ground", out var g) && g is Avalonia.Media.IBrush b ? b : null,
            Icon = (TopLevel.GetTopLevel(this) as Window)?.Icon,
        };
        PlaceOnOtherScreen(w);
        w.Closed += (_, _) => _windows.Remove(what);
        _windows[what] = w;
        w.Show();
    }

    private void PlaceOnOtherScreen(Window w)
    {
        if (TopLevel.GetTopLevel(this) is not Window main) return;
        var screens = main.Screens.All;
        var here = main.Screens.ScreenFromWindow(main);
        var other = screens.FirstOrDefault(s => here is null || !s.Bounds.Equals(here.Bounds));
        if (other is null)
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        var a = other.WorkingArea;
        w.Position = new PixelPoint(a.X + 40, a.Y + 40);
        w.WindowState = WindowState.Maximized;
    }
}
