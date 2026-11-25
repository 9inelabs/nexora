using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// The Live Output control screen: named screens across the top, the terminal routing
/// panel on the left (pin live / take down / bring back), preview and program in the middle,
/// quick screens and the other screens on the right.
/// </summary>
public sealed partial class LiveOutputViewModel : ViewModelBase, IKeyHandler
{
    private readonly DispatcherTimer _clock;

    public LiveOutputViewModel()
    {
        Live.PropertyChanged += OnLive;
        if (S.Output is { } o) o.Changed += OnOutputChanged;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            OnPropertyChanged(nameof(Clock));
            OnPropertyChanged(nameof(OutputStatus));
        });
        _clock.Start();
        Sync();
        InitExtras();
    }

    public ObservableCollection<ScreenTab> Screens { get; } = [];
    public ObservableCollection<SourceCard> Sources { get; } = [];
    public ObservableCollection<MiniScreen> OtherScreens { get; } = [];

    [ObservableProperty] private ScreenTab? _selectedScreen;
    [ObservableProperty] private SourceCard? _previewSource;
    [ObservableProperty] private ScreenFrame? _programFrame;
    [ObservableProperty] private ScreenKind _programKind;
    [ObservableProperty] private string _programInfo = "";
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private string _previewInfo = "Click a terminal to preview what it is sending.";
    [ObservableProperty] private bool _logoActive;
    [ObservableProperty] private bool _blackActive;
    [ObservableProperty] private bool _programIsLive;

    public Branding Branding => Live.Branding;
    public string Clock => DateTime.Now.ToString("hh:mm tt");
    public bool HasSources => Sources.Count > 0;
    public bool NoSources => Sources.Count == 0;
    public bool CanTake => PreviewSource?.HasContent == true && SelectedScreen is not null;

    public string ConnectedText
    {
        get
        {
            var n = Live.Snapshot.Clients.Count(c => c.Kind == ClientKind.Terminal && c.PcId != S.Config.PcId);
            return n == 1 ? "1 terminal connected" : $"{n} terminals connected";
        }
    }

    public bool IsLocal => S.IsLiveOutputPc;
    public string OutputStatus => S.Output switch
    {
        null => "Output windows run on the Live Output PC",
        { IsRunning: true } o => o.Restarts > 0 ? $"Output running · restarted {o.Restarts}×" : "Output running",
        { LastError: { } err } => "Output not running · " + err,
        _ => "Starting output…",
    };
    public bool OutputOk => S.Output?.IsRunning == true;

    // ---------- Commands ----------

    [RelayCommand]
    private void SelectScreen(ScreenTab tab)
    {
        SelectedScreen = tab;
        Sync();
    }

    [RelayCommand]
    private async Task Primary(SourceCard card)
    {
        if (SelectedScreen is null || Live.Client is not { } c) return;
        var action = card.State == RouteState.Live ? RouteAction.TakeDown : RouteAction.Pin;
        await c.RouteAsync(card.Key, SelectedScreen.Id, action);
    }

    [RelayCommand]
    private async Task Unroute(SourceCard card)
    {
        if (SelectedScreen is null || Live.Client is not { } c) return;
        await c.RouteAsync(card.Key, SelectedScreen.Id, RouteAction.Unroute);
    }

    [RelayCommand]
    private async Task Forget(SourceCard card)
    {
        if (Live.Client is not { } c || SelectedScreen is null) return;
        await c.RouteAsync(card.Key, SelectedScreen.Id, RouteAction.Forget);
    }

    [RelayCommand]
    private void Preview(SourceCard card)
    {
        PreviewSource = card;
        foreach (var s in Sources) s.IsPreviewed = s == card;
        UpdatePreview();
    }

    [RelayCommand]
    private async Task Take()
    {
        if (PreviewSource is null || SelectedScreen is null || Live.Client is not { } c) return;
        await c.RouteAsync(PreviewSource.Key, SelectedScreen.Id, RouteAction.Take);
    }

    [RelayCommand] private Task Logo() => Screen(ScreenAction.Logo);
    [RelayCommand] private Task Black() => Screen(ScreenAction.Black);
    [RelayCommand] private Task ClearText() => Screen(ScreenAction.ClearText);
    [RelayCommand] private Task ClearAll() => Screen(ScreenAction.ClearAll);

    [RelayCommand]
    private void RestartOutput() => S.Output?.Restart();

    private async Task Screen(ScreenAction a)
    {
        if (SelectedScreen is null || Live.Client is not { } c) return;
        await c.ScreenAsync(SelectedScreen.Id, a);
    }

    /// <summary>F1–F8: pin / take down terminal 1–8 on the selected screen. Tab: next screen.</summary>
    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (key is >= Key.F1 and <= Key.F8)
        {
            var i = key - Key.F1;
            if (i < Sources.Count) _ = Primary(Sources[i]);
            return true;
        }
        if (key == Key.Tab && mods == KeyModifiers.None && Screens.Count > 1)
        {
            var i = SelectedScreen is null ? 0 : (Screens.IndexOf(SelectedScreen) + 1) % Screens.Count;
            SelectScreen(Screens[i]);
            return true;
        }
        return false;
    }

    // ---------- Sync from the live snapshot ----------

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Live.Snapshot)) Sync();
        if (e.PropertyName is nameof(Live.Library)) OnPropertyChanged(nameof(StreamText));
        if (e.PropertyName is nameof(Live.Engine)) SyncExtras(fromSnapshot: false);
        if (e.PropertyName is nameof(Live.Info))
        {
            OnPropertyChanged(nameof(Branding));
        }
    }

    private void OnOutputChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(OutputStatus));
        OnPropertyChanged(nameof(OutputOk));
    });

    private void Sync()
    {
        var snap = Live.Snapshot;

        // Screens
        var selectedId = SelectedScreen?.Id;
        if (!Screens.Select(s => s.Id).SequenceEqual(snap.Screens.Select(s => s.Id)))
        {
            Screens.Clear();
            foreach (var s in snap.Screens) Screens.Add(new ScreenTab(s.Id));
        }
        foreach (var tab in Screens)
        {
            var info = snap.Screens.First(s => s.Id == tab.Id);
            var frame = snap.FrameFor(tab.Id);
            tab.Update(info, frame);
        }
        SelectedScreen = Screens.FirstOrDefault(s => s.Id == selectedId) ?? Screens.FirstOrDefault();
        foreach (var t in Screens) t.IsSelected = t == SelectedScreen;

        // Terminals (sources)
        var screenId = SelectedScreen?.Id ?? "";
        if (!Sources.Select(s => s.Key).SequenceEqual(snap.Sources.Select(s => s.Key)))
        {
            Sources.Clear();
            foreach (var s in snap.Sources) Sources.Add(new SourceCard(s.Key));
        }
        for (var i = 0; i < Sources.Count; i++)
        {
            var info = snap.Sources.First(s => s.Key == Sources[i].Key);
            Sources[i].Update(info, screenId, i);
        }
        PreviewSource = Sources.FirstOrDefault(s => s.Key == PreviewSource?.Key);
        foreach (var s in Sources) s.IsPreviewed = s == PreviewSource;
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(NoSources));
        OnPropertyChanged(nameof(ConnectedText));

        // Program
        if (SelectedScreen is not null)
        {
            var frame = snap.FrameFor(SelectedScreen.Id);
            ProgramFrame = frame;
            ProgramKind = SelectedScreen.Kind;
            LogoActive = frame?.Master == MasterMode.Logo;
            BlackActive = frame?.Master == MasterMode.Black;
            ProgramIsLive = frame is { Master: MasterMode.None, Items.Count: > 0 };
            ProgramInfo = frame switch
            {
                { Master: MasterMode.Black } => "Black on screen",
                { Master: MasterMode.Logo } => "Logo on screen",
                { Items.Count: 0 } => Branding.LogoWhenIdle ? "Nothing live · showing the logo screen" : "Nothing live",
                _ => string.Join("  ·  ", frame!.Items.Select(i => $"{(i.Follows ? "Pinned" : "Taken")} · {i.SourceLabel}")),
            };
        }

        // Other screens
        OtherScreens.Clear();
        foreach (var s in snap.Screens.Where(s => s.Id != SelectedScreen?.Id))
            OtherScreens.Add(new MiniScreen(s, snap.FrameFor(s.Id)));

        UpdatePreview();
        SyncExtras();
    }

    private void UpdatePreview()
    {
        OnPropertyChanged(nameof(CanTake));
        TakeCommand.NotifyCanExecuteChanged();
        if (PreviewSource?.Info is not { } src)
        {
            PreviewFrame = null;
            PreviewInfo = Sources.Count == 0 ? "No terminals yet." : "Click Preview on a terminal to see what it is sending.";
            return;
        }
        var def = Sections.Get(src.Section);
        PreviewFrame = src.Current is null
            ? new ScreenFrame("preview", MasterMode.None, [])
            : new ScreenFrame("preview", MasterMode.None, [new LayerItem(def.Layer, src.Key, src.Label, src.Section, src.Current, true, 0)]);
        PreviewInfo = src.Current is null ? $"{src.Label} · nothing sent yet" : $"{src.Label} · {src.Current.Label ?? src.Current.Text}";
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnLive;
        if (S.Output is { } o) o.Changed -= OnOutputChanged;
        _clock.Stop();
        LeaveExtras();
    }
}

public sealed partial class ScreenTab(string id) : ObservableObject
{
    public string Id => id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private ScreenKind _kind;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isSelected;
    public string Icon => Kind == ScreenKind.Stage ? "stage" : "monitor";

    public void Update(ScreenInfo info, ScreenFrame? frame)
    {
        Name = info.Name;
        Kind = info.Kind;
        Sub = $"{(info.Kind == ScreenKind.Stage ? "Stage look" : "Full themes")} · {(info.Display == 0 ? "window" : $"display {info.Display}")}";
        IsLive = frame is { Master: MasterMode.None, Items.Count: > 0 };
        OnPropertyChanged(nameof(Icon));
    }
}

public sealed partial class SourceCard(string key) : ObservableObject
{
    public string Key => key;
    public SourceInfo? Info { get; private set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _icon = "square";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private RouteState _state;
    [ObservableProperty] private bool _isPreviewed;
    [ObservableProperty] private string _shortcut = "";
    [ObservableProperty] private bool _hasContent;

    public bool IsLive => State == RouteState.Live;
    public bool IsStandby => State == RouteState.Standby;
    public bool IsNotRouted => State == RouteState.NotRouted;
    public bool IsOffline => !Online;
    public string PrimaryLabel => State switch
    {
        RouteState.Live => "Take down",
        RouteState.Standby => "Bring back",
        _ => "Pin live",
    };
    public string PrimaryIcon => State == RouteState.Live ? "takedown" : "pin";
    public string StateText => State switch
    {
        RouteState.Live => "Pinned",
        RouteState.Standby => "Standby",
        _ => Online ? "Ready" : "Offline",
    };

    partial void OnStateChanged(RouteState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsStandby));
        OnPropertyChanged(nameof(IsNotRouted));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(PrimaryIcon));
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(StateText));
    }

    public void Update(SourceInfo info, string screenId, int index)
    {
        Info = info;
        Title = Sections.Get(info.Section).Title;
        Sub = $"{info.UserName ?? "—"} · {info.PcName}";
        Current = info.Current is null ? "Nothing sent yet" : info.Current.Label ?? info.Current.Text;
        HasContent = info.Current is not null;
        Icon = Icons.ForSection(info.Section);
        Online = info.Online;
        State = info.RouteOn(screenId);
        Shortcut = index < 8 ? $"F{index + 1}" : "";
    }
}

public sealed class MiniScreen(ScreenInfo info, ScreenFrame? frame)
{
    public string Name => info.Name;
    public ScreenKind Kind => info.Kind;
    public ScreenFrame? Frame => frame;
    public string Info => frame switch
    {
        { Master: MasterMode.Black } => "Black",
        { Master: MasterMode.Logo } => "Logo",
        { Items.Count: 0 } => "Idle",
        _ => string.Join(" · ", frame!.Items.Select(i => i.SourceLabel)),
    };
    public string Display => info.Display == 0 ? "Window" : $"Display {info.Display}";
}
