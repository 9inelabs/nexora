using Avalonia.Controls;
using Avalonia.Input;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views.NexAudio;

public partial class MixerPage : UserControl
{
    public MixerPage() => InitializeComponent();

    /// <summary>A double-tap on a bus tag opens that bus to edit, like the pencil.</summary>
    private void OnBusDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BusTagView tag })
        {
            tag.EditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
