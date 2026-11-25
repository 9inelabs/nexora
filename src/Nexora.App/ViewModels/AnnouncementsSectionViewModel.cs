using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// Announcements: notice slides (words on a template, or a ready-made picture), shown one at a time or
/// as a loop that runs by itself on the Live Output PC. Slides past their last day leave the loop.
/// </summary>
public sealed partial class AnnouncementsSectionViewModel : SectionBase, IKeyHandler
{
    private readonly DispatcherTimer _themeDebounce;
    private string? _pendingThemeId;
    private bool _loading;

    public AnnouncementsSectionViewModel() : base(SectionKind.Announcements)
    {
        _themeDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => { _themeDebounce?.Stop(); UpdatePreview(); });
        Editor.Changed += () => { _themeDebounce.Stop(); _themeDebounce.Start(); };
        LoadThemes();
        LoadSlides();
    }

    public ThemeEditor Editor { get; } = new();
    public ObservableCollection<AnnouncementRow> Slides { get; } = [];
    public ObservableCollection<ScriptureTheme> Themes { get; } = [];

    [ObservableProperty] private AnnouncementRow? _selected;
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private ScriptureTheme? _selectedTheme;
    [ObservableProperty] private bool _styleOpen;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string _newThemeName = "";
    [ObservableProperty] private bool _looping;
    [ObservableProperty] private TransitionKind _loopTransition = TransitionKind.Fade;

    // The slide being edited
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string? _imageId;
    [ObservableProperty] private bool _imageOnly;
    [ObservableProperty] private double _seconds = 10;
    [ObservableProperty] private string _expires = "";

    public bool HasSlides => Slides.Count > 0;
    public bool HasSelected => Selected is not null;
    public bool HasImage => !string.IsNullOrEmpty(ImageId);
    public string LoopInfo
    {
        get
        {
            var live = LiveSlides().ToList();
            var total = live.Sum(s => Math.Max(2, s.Seconds));
            return live.Count == 0 ? "No slides to loop." : $"{live.Count} slide{(live.Count == 1 ? "" : "s")} · {total / 60}:{total % 60:00} a round";
        }
    }

    public static IReadOnlyList<TransitionKind> Transitions { get; } = [TransitionKind.Fade, TransitionKind.Slide, TransitionKind.Cut];

    // ---------- Slides ----------

    private void LoadSlides()
    {
        _loading = true;
        var keep = Selected?.Slide.Id;
        Slides.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var s in Live.Announcements) Slides.Add(new AnnouncementRow(s, today));
        Selected = Slides.FirstOrDefault(r => r.Slide.Id == keep) ?? Slides.FirstOrDefault();
        _loading = false;
        Dirty = false;
        OnPropertyChanged(nameof(HasSlides));
        OnPropertyChanged(nameof(LoopInfo));
        LoadSelected();
    }

    partial void OnSelectedChanged(AnnouncementRow? value)
    {
        foreach (var r in Slides) r.IsCurrent = r == value;
        OnPropertyChanged(nameof(HasSelected));
        if (!_loading) LoadSelected();
    }

    private void LoadSelected()
    {
        var s = Selected?.Slide;
        _loading = true;
        Title = s?.Title ?? "";
        Details = s?.Details ?? "";
        DateText = s?.DateText ?? "";
        ImageId = s?.ImageId;
        ImageOnly = s?.ImageOnly ?? false;
        Seconds = s?.Seconds ?? 10;
        Expires = s?.Expires?.ToString("yyyy-MM-dd") ?? "";
        _loading = false;
        UpdatePreview();
    }

    partial void OnTitleChanged(string value) => Edited();
    partial void OnDetailsChanged(string value) => Edited();
    partial void OnDateTextChanged(string value) => Edited();
    partial void OnImageIdChanged(string? value) { OnPropertyChanged(nameof(HasImage)); Edited(); }
    partial void OnImageOnlyChanged(bool value) => Edited();
    partial void OnSecondsChanged(double value) => Edited();
    partial void OnExpiresChanged(string value) => Edited();

    private void Edited()
    {
        if (_loading || Selected is null) return;
        var idx = Slides.IndexOf(Selected);
        var updated = new AnnouncementSlide(Selected.Slide.Id, Title.Trim(), Details.Trim(), DateText.Trim(), ImageId, ImageOnly && ImageId is not null,
            (int)Math.Clamp(Seconds, 2, 600), ParseDate(Expires));
        var row = new AnnouncementRow(updated, DateOnly.FromDateTime(DateTime.Now)) { IsCurrent = true };
        _loading = true;
        Slides[idx] = row;
        Selected = row;
        _loading = false;
        Dirty = true;
        OnPropertyChanged(nameof(LoopInfo));
        UpdatePreview();
    }

    private static DateOnly? ParseDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        string[] formats = ["yyyy-MM-dd", "d/M/yyyy", "d/M/yy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "d MMM", "d MMMM"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
        {
            if (!text.Any(char.IsDigit) || text.Count(char.IsDigit) <= 2) d = new DateOnly(DateTime.Now.Year, d.Month, d.Day);
            return d;
        }
        return DateOnly.TryParse(text, CultureInfo.CurrentCulture, out var d2) ? d2 : null;
    }

    [RelayCommand]
    private void Pick(AnnouncementRow row) => Selected = row;

    [RelayCommand]
    private void AddSlide()
    {
        var s = new AnnouncementSlide("a-" + Guid.NewGuid().ToString("N")[..10], "New announcement", "", "", null, false, 10, null);
        var row = new AnnouncementRow(s, DateOnly.FromDateTime(DateTime.Now));
        Slides.Add(row);
        Selected = row;
        Dirty = true;
        OnPropertyChanged(nameof(HasSlides));
        OnPropertyChanged(nameof(LoopInfo));
    }

    [RelayCommand]
    private void RemoveSlide(AnnouncementRow row)
    {
        var i = Slides.IndexOf(row);
        Slides.Remove(row);
        if (Selected == row) Selected = Slides.Count == 0 ? null : Slides[Math.Clamp(i, 0, Slides.Count - 1)];
        Dirty = true;
        OnPropertyChanged(nameof(HasSlides));
        OnPropertyChanged(nameof(LoopInfo));
    }

    [RelayCommand]
    private void MoveUp(AnnouncementRow row)
    {
        var i = Slides.IndexOf(row);
        if (i <= 0) return;
        Slides.Move(i, i - 1);
        Dirty = true;
    }

    [RelayCommand]
    private void ClearPicture() => ImageId = null;

    [RelayCommand]
    private async Task Save()
    {
        if (Live.Client is not { } c) return;
        await c.SendAsync(new SaveAnnouncements(Slides.Select(r => r.Slide).ToList()));
        Dirty = false;
        Live.RaiseToast("Announcements saved for every PC.");
    }

    /// <summary>Uploads a picture to the Live Output PC (copied to every PC) and puts it on this slide.</summary>
    public async Task UsePictureAsync(string path, bool forBackground)
    {
        if (Live.Client is not { } c) return;
        var name = await c.UploadAsync("media", Path.GetFileName(path), path);
        var local = Path.Combine(Nexora.Core.Config.AppPaths.MediaDir, name);
        if (!File.Exists(local)) File.Copy(path, local, overwrite: true);
        if (forBackground)
        {
            Editor.Background = BackgroundKind.Image;
            Editor.ImageId = name;
        }
        else ImageId = name;
    }

    private IEnumerable<AnnouncementSlide> LiveSlides()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return Slides.Select(r => r.Slide).Where(s => !s.IsExpired(today));
    }

    // ---------- Preview and sending ----------

    private AnnouncementShow? ShowFor(AnnouncementSlide slide) =>
        new([slide], Editor.ToTheme(), false, 0, DateTime.UtcNow, LoopTransition);

    private void UpdatePreview()
    {
        PreviewFrame = Selected is { } r && ShowFor(r.Slide) is { } show
            ? PreviewOf(new SlideContent(r.Slide.Title, "Announcement", r.Slide.Title, Announcement: show), Layer.Overlays)
            : null;
    }

    [RelayCommand]
    private async Task Show()
    {
        if (Selected is not { } r || ShowFor(r.Slide) is not { } show) return;
        Looping = false;
        await SendAsync(new SlideContent(r.Slide.Title, "Announcement", r.Slide.Title, Announcement: show));
    }

    [RelayCommand]
    private async Task StartLoop()
    {
        var list = LiveSlides().ToList();
        if (list.Count == 0) return;
        if (Dirty) await Save();
        var start = Selected is { } r ? Math.Max(0, list.FindIndex(s => s.Id == r.Slide.Id)) : 0;
        var show = new AnnouncementShow(list, Editor.ToTheme(), true, start, DateTime.UtcNow, LoopTransition);
        Looping = true;
        await SendAsync(new SlideContent($"Announcements loop ({list.Count})", "Announcements", "Loop", Announcement: show));
    }

    [RelayCommand]
    private async Task Clear()
    {
        Looping = false;
        await ClearAsync();
    }

    [RelayCommand]
    private async Task Next() => await Step(1);

    [RelayCommand]
    private async Task Previous() => await Step(-1);

    private async Task Step(int d)
    {
        if (Slides.Count == 0) return;
        var i = Selected is null ? 0 : Math.Clamp(Slides.IndexOf(Selected) + d, 0, Slides.Count - 1);
        Selected = Slides[i];
        if (Showing && !Looping) await Show();
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.Down or Key.Right: _ = Next(); return true;
            case Key.Up or Key.Left: _ = Previous(); return true;
            case Key.Enter: _ = Show(); return true;
            case Key.L: _ = StartLoop(); return true;
            case Key.Escape: _ = Clear(); return true;
        }
        return false;
    }

    // ---------- Templates ----------

    private void LoadThemes()
    {
        var keep = SelectedTheme?.Id ?? _pendingThemeId;
        Themes.Clear();
        foreach (var t in Live.AnnouncementThemes) Themes.Add(t);
        var pick = Themes.FirstOrDefault(t => t.Id == keep) ?? Themes.FirstOrDefault();
        if (pick?.Id == _pendingThemeId) _pendingThemeId = null;
        if (pick is not null && (SelectedTheme is null || SelectedTheme.Id != pick.Id)) SelectedTheme = pick;
    }

    partial void OnSelectedThemeChanged(ScriptureTheme? value)
    {
        if (value is null) return;
        Editor.Load(value);
        UpdatePreview();
    }

    partial void OnLoopTransitionChanged(TransitionKind value) => UpdatePreview();

    [RelayCommand]
    private async Task SaveTheme()
    {
        if (Live.Client is not { } c) return;
        var t = Editor.ToTheme();
        await c.SendAsync(new SaveTheme(ThemeKind.Announcements, t));
        Live.RaiseToast($"Saved the {t.Name} template for every PC.");
    }

    [RelayCommand]
    private async Task SaveThemeAsNew()
    {
        if (Live.Client is not { } c) return;
        var name = string.IsNullOrWhiteSpace(NewThemeName) ? Editor.Name + " copy" : NewThemeName.Trim();
        var t = Editor.ToTheme() with { Id = "n-" + Guid.NewGuid().ToString("N")[..8], Name = name };
        _pendingThemeId = t.Id;
        await c.SendAsync(new SaveTheme(ThemeKind.Announcements, t));
        NewThemeName = "";
    }

    [RelayCommand]
    private async Task DeleteTheme()
    {
        if (Live.Client is not { } c || SelectedTheme is not { } t) return;
        await c.SendAsync(new DeleteTheme(ThemeKind.Announcements, t.Id));
    }

    protected override void OnLive(string? property)
    {
        if (property != nameof(Live.Library)) return;
        LoadThemes();
        if (!Dirty) LoadSlides();
    }
}

public sealed partial class AnnouncementRow(AnnouncementSlide slide, DateOnly today) : ObservableObject
{
    public AnnouncementSlide Slide => slide;
    public string Title => slide.ImageOnly ? "Picture: " + (slide.Title.Length > 0 ? slide.Title : slide.ImageId) : slide.Title;
    public string Sub => slide.IsExpired(today) ? $"Ended {slide.Expires:d MMM}" :
        slide.Expires is { } e ? $"{slide.Seconds} s · until {e:d MMM}" : $"{slide.Seconds} s";
    public bool Expired => slide.IsExpired(today);
    [ObservableProperty] private bool _isCurrent;
}
