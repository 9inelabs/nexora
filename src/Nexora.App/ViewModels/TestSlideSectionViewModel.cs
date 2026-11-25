using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Model;

namespace Nexora.App.ViewModels;

/// <summary>
/// Phase 1 operator screen for any section: a list of test slides.
/// Enter shows the selected slide; while showing, the arrow keys move and the screen follows.
/// The real Scripture, Lyrics and other tools replace this in later phases.
/// </summary>
public sealed partial class TestSlideSectionViewModel : ViewModelBase, IKeyHandler
{
    public TestSlideSectionViewModel(SectionKind kind)
    {
        Kind = kind;
        var i = 0;
        foreach (var (text, caption) in SampleSlides(kind)) Slides.Add(new SlideRow(++i, text, caption));
        SelectedSlide = Slides.FirstOrDefault();
        Live.PropertyChanged += OnLive;
        UpdateAir();
    }

    public SectionKind Kind { get; }
    public string Title => Sections.Get(Kind).Title;
    public string Icon => Icons.ForSection(Kind);
    public string SourceKey => Sections.SourceKey(S.Config.PcId, Kind);
    public bool IsTrainee => Live.User?.IsTrainee == true;

    public ObservableCollection<SlideRow> Slides { get; } = [];
    public ObservableCollection<AirBar> Bars { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFrame))]
    private SlideRow? _selectedSlide;

    [ObservableProperty] private SlideRow? _showing;
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private string _newCaption = "";
    [ObservableProperty] private bool _notRouted = true;
    [ObservableProperty] private ScreenFrame? _onAirFrame;
    [ObservableProperty] private ScreenKind _onAirKind;
    [ObservableProperty] private string _onAirName = "";
    [ObservableProperty] private bool _hasOnAir;

    public Branding Branding => Live.Branding;

    public ScreenFrame? PreviewFrame => SelectedSlide is null ? null : new ScreenFrame("preview", MasterMode.None,
    [
        new LayerItem(Sections.Get(Kind).Layer, "local", Title, Kind, SelectedSlide.Content, true, 0),
    ]);

    partial void OnSelectedSlideChanged(SlideRow? value)
    {
        foreach (var s in Slides) s.IsSelected = s == value;
    }

    [RelayCommand]
    private async Task Show()
    {
        if (SelectedSlide is null || Live.Client is not { } c) return;
        Showing = SelectedSlide;
        foreach (var s in Slides) s.IsShowing = s == Showing;
        if (!IsTrainee) await c.SendContentAsync(Kind, SelectedSlide.Content);
    }

    [RelayCommand]
    private async Task Clear()
    {
        Showing = null;
        foreach (var s in Slides) s.IsShowing = false;
        if (Live.Client is { } c) await c.ClearContentAsync(Kind);
    }

    [RelayCommand]
    private Task Next() => Move(1);

    [RelayCommand]
    private Task Previous() => Move(-1);

    [RelayCommand]
    private Task Pick(SlideRow row)
    {
        SelectedSlide = row;
        return Show();
    }

    private async Task Move(int delta)
    {
        if (Slides.Count == 0) return;
        var i = SelectedSlide is null ? 0 : Slides.IndexOf(SelectedSlide) + delta;
        if (i < 0 || i >= Slides.Count) return;
        SelectedSlide = Slides[i];
        // Once something is showing, the screen follows every arrow press.
        if (Showing is not null) await Show();
    }

    [RelayCommand]
    private void AddSlide()
    {
        if (string.IsNullOrWhiteSpace(NewText)) return;
        var row = new SlideRow(Slides.Count + 1, NewText.Trim(), string.IsNullOrWhiteSpace(NewCaption) ? null : NewCaption.Trim());
        Slides.Add(row);
        SelectedSlide = row;
        NewText = "";
        NewCaption = "";
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Down or Key.Right or Key.PageDown:
                _ = Next();
                return true;
            case Key.Up or Key.Left or Key.PageUp:
                _ = Previous();
                return true;
            case Key.Enter:
                _ = Show();
                return true;
            case Key.Escape:
                _ = Clear();
                return true;
        }
        return false;
    }

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Snapshot)) UpdateAir();
        if (e.PropertyName == nameof(Live.Info)) OnPropertyChanged(nameof(Branding));
    }

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
        HasOnAir = firstLive is not null;
        if (firstLive is not null)
        {
            OnAirFrame = snap.FrameFor(firstLive.Id);
            OnAirKind = firstLive.Kind;
            OnAirName = firstLive.Name;
        }
    }

    public override void OnLeave() => Live.PropertyChanged -= OnLive;

    private static IEnumerable<(string, string?)> SampleSlides(SectionKind kind) => kind switch
    {
        SectionKind.Scripture =>
        [
            ("For God so loved the world, that he gave his only begotten Son, that whosoever believeth in him should not perish, but have everlasting life.", "John 3:16 · KJV"),
            ("For God sent not his Son into the world to condemn the world; but that the world through him might be saved.", "John 3:17 · KJV"),
            ("He that believeth on him is not condemned: but he that believeth not is condemned already, because he hath not believed in the name of the only begotten Son of God.", "John 3:18 · KJV"),
            ("And this is the condemnation, that light is come into the world, and men loved darkness rather than light, because their deeds were evil.", "John 3:19 · KJV"),
            ("For every one that doeth evil hateth the light, neither cometh to the light, lest his deeds should be reproved.", "John 3:20 · KJV"),
            ("But he that doeth truth cometh to the light, that his deeds may be made manifest, that they are wrought in God.", "John 3:21 · KJV"),
        ],
        SectionKind.Lyrics =>
        [
            ("Amazing grace! How sweet the sound\nThat saved a wretch like me!", "Amazing Grace · Verse 1"),
            ("I once was lost, but now am found;\nWas blind, but now I see.", "Amazing Grace · Verse 1"),
            ("'Twas grace that taught my heart to fear,\nAnd grace my fears relieved;", "Amazing Grace · Verse 2"),
            ("How precious did that grace appear\nThe hour I first believed.", "Amazing Grace · Verse 2"),
            ("Through many dangers, toils and snares,\nI have already come;", "Amazing Grace · Verse 3"),
            ("'Tis grace hath brought me safe thus far,\nAnd grace will lead me home.", "Amazing Grace · Verse 3"),
        ],
        SectionKind.Camera =>
        [
            ("CAM 1 · Wide stage", "Test pattern"),
            ("CAM 2 · Pulpit", "Test pattern"),
            ("CAM 3 · Choir", "Test pattern"),
        ],
        SectionKind.Announcements =>
        [
            ("Youth Conference", "Saturday 30 November · 10 AM"),
            ("Midweek Bible Study", "Wednesdays · 6 PM"),
            ("Welcome to our visitors", "Please stay for refreshments after service"),
        ],
        SectionKind.LowerThirds =>
        [
            ("Pastor Samuel Mensah", "Senior Pastor"),
            ("Deaconess Ama Owusu", "Women's Fellowship"),
            ("Bro. Joel Asante", "Worship Leader"),
        ],
        SectionKind.Countdown =>
        [
            ("05:00", "Service starts in"),
            ("10:00", "Sermon ends in"),
            ("02:00", "Offering"),
        ],
        _ => [("Test slide", null)],
    };
}

public sealed partial class SlideRow(int number, string text, string? caption) : ObservableObject
{
    public int Number => number;
    public string Text => text;
    public string? Caption => caption;
    public bool HasCaption => !string.IsNullOrWhiteSpace(caption);
    public SlideContent Content => new(text, caption, caption ?? (text.Length > 48 ? text[..48] + "…" : text).Replace('\n', ' '));
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isShowing;
}

public sealed record AirBar(string ScreenName, bool IsLive)
{
    public bool IsStandby => !IsLive;
    public string Text => IsLive ? $"ON AIR · {ScreenName}" : $"STANDBY · {ScreenName}";
}
