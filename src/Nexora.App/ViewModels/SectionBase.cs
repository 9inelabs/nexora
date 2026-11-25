using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexora.Core.Model;

namespace Nexora.App.ViewModels;

/// <summary>
/// What every section screen has: the on-air bars at the top, the trainee note, sending and clearing.
/// </summary>
public abstract partial class SectionBase : ViewModelBase
{
    protected SectionBase(SectionKind kind)
    {
        Kind = kind;
        Live.PropertyChanged += OnLiveChanged;
        UpdateAir();
    }

    public SectionKind Kind { get; }
    public string SourceKey => Sections.SourceKey(S.Config.PcId, Kind);
    public bool IsTrainee => Live.User?.IsTrainee == true;
    public bool IsAdmin => Live.User?.IsAdmin == true;
    public bool MayEdit => Live.User is { } u && !u.IsTrainee && (u.IsAdmin || u.Roles.HasFlag(Sections.Get(Kind).Role));
    public Branding Branding => Live.Branding;

    [ObservableProperty] private bool _notRouted = true;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private ScreenFrame? _onAirFrame;
    [ObservableProperty] private ScreenKind _onAirKind;
    [ObservableProperty] private string _onAirName = "";
    [ObservableProperty] private bool _showing;

    public ObservableCollection<AirBar> Bars { get; } = [];

    partial void OnShowingChanged(bool value) => ShowingChanged();

    protected virtual void ShowingChanged() { }

    /// <summary>The content this section has on the Live Output PC right now.</summary>
    protected SlideContent? Current => Live.Snapshot.Sources.FirstOrDefault(s => s.Key == SourceKey)?.Current;

    protected async Task SendAsync(SlideContent content)
    {
        Showing = true;
        if (IsTrainee || Live.Client is not { } c) return;
        await c.SendContentAsync(Kind, content);
    }

    protected async Task ClearAsync()
    {
        Showing = false;
        if (Live.Client is { } c) await c.ClearContentAsync(Kind);
    }

    /// <summary>A preview frame with one item, drawn exactly as the screen would.</summary>
    protected ScreenFrame PreviewOf(SlideContent content, Layer? layer = null, bool withCamera = false)
    {
        var items = new List<LayerItem>();
        if (withCamera) items.Add(new LayerItem(Layer.Camera, "preview-cam", "Camera", SectionKind.Camera, new SlideContent("Camera"), true, 0));
        items.Add(new LayerItem(layer ?? Sections.Get(Kind).Layer, "preview", Sections.Get(Kind).Title, Kind, content, true, 1));
        return new ScreenFrame("preview", MasterMode.None, items);
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Snapshot)) UpdateAir();
        if (e.PropertyName == nameof(Live.Info)) OnPropertyChanged(nameof(Branding));
        if (e.PropertyName == nameof(Live.User))
        {
            OnPropertyChanged(nameof(IsTrainee));
            OnPropertyChanged(nameof(IsAdmin));
            OnPropertyChanged(nameof(MayEdit));
        }
        OnLive(e.PropertyName);
    }

    /// <summary>Something on the Live Output PC changed (Snapshot, Library, User…).</summary>
    protected virtual void OnLive(string? property) { }

    private void UpdateAir()
    {
        var snap = Live.Snapshot;
        var src = snap.Sources.FirstOrDefault(s => s.Key == SourceKey);
        Bars.Clear();
        ScreenInfo? firstLive = null;
        foreach (var screen in snap.Screens)
        {
            var state = src?.RouteOn(screen.Id) ?? RouteState.NotRouted;
            if (state == RouteState.NotRouted) continue;
            Bars.Add(new AirBar(screen.Name, state == RouteState.Live));
            if (state == RouteState.Live) firstLive ??= screen;
        }
        NotRouted = Bars.Count == 0;
        IsLive = firstLive is not null;
        if (firstLive is not null)
        {
            OnAirFrame = snap.FrameFor(firstLive.Id);
            OnAirKind = firstLive.Kind;
            OnAirName = firstLive.Name;
        }
        if (src?.Current is null && Showing && src is not null) Showing = false;
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnLiveChanged;
    }
}
