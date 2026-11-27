using Avalonia.Controls;
using NexAudio.App.Controls;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views.NexAudio;

public partial class RoutingPage : UserControl
{
    public RoutingPage()
    {
        InitializeComponent();
        var grid = this.FindControl<RouteGrid>("Grid")!;
        grid.CellClicked += (r, c) => (DataContext as NexAudioViewModel)?.RouteClicked(r, c);
        grid.Trace += id => (DataContext as NexAudioViewModel)?.TraceStrip(id);
        grid.Hover += text =>
        {
            if (DataContext is NexAudioViewModel vm) vm.RouteHint = text;
        };
    }
}
