using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Nexora.App.Output;
using Nexora.App.ViewModels;
using Nexora.App.Views;

namespace Nexora.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Logos and pictures are shrunk a lot on screen; smooth scaling keeps their edges clean.
        Avalonia.Controls.Image.SourceProperty.Changed.AddClassHandler<Avalonia.Controls.Image>((img, _) =>
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality)); // mipmaps: clean when shrunk a lot
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.IsOutputEngine)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                new OutputEngine(desktop).Start();
            }
            else
            {
                var vm = new MainViewModel();
                desktop.MainWindow = new MainWindow { DataContext = vm };
                _ = vm.BootAsync();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
