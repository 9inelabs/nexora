using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nexora.App.Services;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public partial class MainWindow : Window
{
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        WindowState = WindowState.Maximized;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnText, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool TypingInTextBox => FocusManager?.GetFocusedElement() is TextBox;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (Vm?.Current)
        {
            case LoginViewModel login:
                if (e.Key == Key.Back) { login.Backspace(); e.Handled = true; }
                else if (e.Key == Key.Left) { login.MoveUser(-1); e.Handled = true; }
                else if (e.Key == Key.Right) { login.MoveUser(1); e.Handled = true; }
                else if (e.Key == Key.Enter) { login.SignInCommand.Execute(null); e.Handled = true; }
                break;
            case ShellViewModel shell:
                if (TypingInTextBox && !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is >= Key.F1 and <= Key.F8)) return;
                if (FocusManager?.GetFocusedElement() is ComboBox) return;
                if (shell.HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
                break;
        }
    }

    private void OnText(object? sender, TextInputEventArgs e)
    {
        if (Vm?.Current is LoginViewModel login && !string.IsNullOrEmpty(e.Text) && !TypingInTextBox)
        {
            foreach (var ch in e.Text) login.TypeDigit(ch);
            e.Handled = true;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !AppServices.Instance.IsLiveOutputPc) return;
        // Closing the control screen on the Live Output PC: keep the audience screens on.
        e.Cancel = true;
        var dlg = new CloseLiveOutputDialog();
        var choice = await dlg.ShowDialog<CloseChoice>(this);
        if (choice == CloseChoice.Cancel) return;
        if (choice == CloseChoice.Everything) AppServices.Instance.Output?.StopEngine();
        await AppServices.Instance.StopAsync(stopOutput: false);
        _closingConfirmed = true;
        Close();
    }
}
