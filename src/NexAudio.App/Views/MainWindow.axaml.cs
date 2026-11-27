using Avalonia.Controls;
using Avalonia.Input;
using NexAudio.App.ViewModels;

namespace NexAudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Typing the PIN works without clicking into anything first.
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // F1 to F12 run the macros given those keys.
        if (DataContext is ShellViewModel { Current: NexAudioViewModel desk } && e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (desk.RunMacroKey(e.Key - Key.F1 + 1)) e.Handled = true;
            return;
        }
        if (DataContext is not ShellViewModel { Current: SignInViewModel s }) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case >= Key.D0 and <= Key.D9:
                s.TypeDigit((char)('0' + (e.Key - Key.D0)));
                e.Handled = true;
                break;
            case >= Key.NumPad0 and <= Key.NumPad9:
                s.TypeDigit((char)('0' + (e.Key - Key.NumPad0)));
                e.Handled = true;
                break;
            case Key.Back:
                s.Backspace();
                e.Handled = true;
                break;
            case Key.Left:
                s.MoveUser(-1);
                e.Handled = true;
                break;
            case Key.Right:
                s.MoveUser(1);
                e.Handled = true;
                break;
        }
    }
}
