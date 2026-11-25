using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase? _current;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;
    private IDisposable? _toastTimer;

    public MainViewModel()
    {
        Live.Notice += n => ShowToast(n.Text, n.IsError);
        Live.PropertyChanged += OnLiveChanged;
    }

    partial void OnCurrentChanging(ViewModelBase? value) => Current?.OnLeave();

    public async Task BootAsync()
    {
        if (S.Config.Role == PcRole.NotSet)
        {
            S.EnsureDiscovery();
            Current = new FirstRunViewModel(this);
            return;
        }
        await StartServicesAsync();
    }

    public async Task StartServicesAsync()
    {
        try
        {
            await S.StartAsync();
            Current = new LoginViewModel(this);
        }
        catch (Exception ex)
        {
            AppLog.Write("Start failed: " + ex);
            Current = new StartupErrorViewModel(this, ex.Message);
        }
    }

    public void GoLogin(string? message = null)
    {
        Current = new LoginViewModel(this);
        if (message is not null) ShowToast(message, false);
    }

    public void GoSectionPicker(IEnumerable<SectionKind>? preselect = null) =>
        Current = new SectionPickerViewModel(this, preselect);

    public void GoShell(IReadOnlyList<SectionKind> sections, bool liveOutput, bool settings = false) =>
        Current = new ShellViewModel(this, sections, liveOutput, settings);

    public async Task GoFirstRunAsync(bool terminalStep = false)
    {
        await S.StopAsync(stopOutput: true);
        S.EnsureDiscovery();
        Current = new FirstRunViewModel(this, terminalStep);
    }

    public async Task SignOutAsync()
    {
        if (Live.Client is { } c) await c.LogoutAsync();
        GoLogin();
    }

    public void ShowToast(string text, bool isError)
    {
        Toast = text;
        ToastIsError = isError;
        _toastTimer?.Dispose();
        _toastTimer = DispatcherTimer.RunOnce(() => Toast = null, TimeSpan.FromSeconds(isError ? 6 : 4));
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The Live Output PC restarted and does not know us any more: back to sign-in.
        if (e.PropertyName is nameof(LiveModel.User) or nameof(LiveModel.IsConnected) && Live.User is null && Live.IsConnected
            && Current is ShellViewModel or SectionPickerViewModel)
            GoLogin("Please sign in again. The Live Output PC was restarted.");
    }
}

public sealed partial class StartupErrorViewModel(MainViewModel main, string message) : ViewModelBase
{
    public string Message { get; } = message;
    public string LogsFolder => AppPaths.LogsDir;

    [RelayCommand]
    private Task Retry() => main.StartServicesAsync();

    [RelayCommand]
    private Task SetUpAgain() => main.GoFirstRunAsync();
}
