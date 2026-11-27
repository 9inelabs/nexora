using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NexAudio.App.ViewModels;
using NexAudio.App.Views;

namespace NexAudio.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DeskServices.Instance.Start();
            var shell = new ShellViewModel();
            desktop.MainWindow = new MainWindow { DataContext = shell };
            // Closing the window leaves the sound running: the engine is its own process.
            desktop.ShutdownRequested += (_, _) => shell.Current?.OnLeave();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
