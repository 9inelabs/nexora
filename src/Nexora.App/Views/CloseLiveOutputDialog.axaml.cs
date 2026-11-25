using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Nexora.App.Views;

public enum CloseChoice { Cancel, KeepScreens, Everything }

public partial class CloseLiveOutputDialog : Window
{
    public CloseLiveOutputDialog() => InitializeComponent();

    private void OnCancel(object? s, RoutedEventArgs e) => Close(CloseChoice.Cancel);
    private void OnKeep(object? s, RoutedEventArgs e) => Close(CloseChoice.KeepScreens);
    private void OnEverything(object? s, RoutedEventArgs e) => Close(CloseChoice.Everything);
}
