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

    private static bool Handle(NexAudioViewModel d, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && d.QuickOpen)
        {
            d.QuickPickCommand.Execute(null);
            return true;
        }
        return d.HandleKey(e.Key, e.KeyModifiers);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // F1 to F12 run the macros given those keys.
        if (DataContext is ShellViewModel { Current: NexAudioViewModel desk } && e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (desk.RunMacroKey(e.Key - Key.F1 + 1)) e.Handled = true;
            return;
        }
        // The desk's own shortcuts, except while typing in a box (Esc still closes quick select).
        if (DataContext is ShellViewModel { Current: NexAudioViewModel d })
        {
            var typing = FocusManager?.GetFocusedElement() is TextBox or ComboBox or Slider;
            if ((!typing || e.Key == Key.Escape || e.Key == Key.Enter && d.QuickOpen) && Handle(d, e)) e.Handled = true;
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
