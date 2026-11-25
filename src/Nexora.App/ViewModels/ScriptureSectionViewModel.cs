using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Services;
using Nexora.Core.Bible;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// The Scripture section: type a reference the way people speak, see the chapter, preview the
/// slide exactly as the screen will show it, press Enter to send and the arrow keys to step.
/// </summary>
public sealed partial class ScriptureSectionViewModel : ViewModelBase, IKeyHandler
{
    private readonly DispatcherTimer _themeDebounce;
    private BibleReference? _reference;
    private IReadOnlyList<ScriptureSlide> _slides = [];
    private bool _suppressRefParse;

    public ScriptureSectionViewModel()
    {
        Editor.Changed += OnEditorChanged;
        _themeDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => ApplyEditor());
        Live.PropertyChanged += OnLive;
        LibrarySync.BiblesUpdated += OnBiblesUpdated;
        S.Bibles.Changed += OnBiblesUpdated;
        LoadDay();
        _ = InitAsync();
        UpdateAir();
    }

    private async Task InitAsync()
    {
        await S.LoadBiblesAsync();
        Dispatcher.UIThread.Post(() =>
        {
            Preparing = false;
            RefreshTranslations();
            LoadThemes();
            if (string.IsNullOrWhiteSpace(RefText)) RefText = "John 3:16";
            else ParseRef();
        });
    }

    public string SourceKey => Sections.SourceKey(S.Config.PcId, SectionKind.Scripture);
    public bool IsTrainee => Live.User?.IsTrainee == true;
    public Branding Branding => Live.Branding;
    public ThemeEditor Editor { get; } = new();

    // ---------- State ----------

    [ObservableProperty] private bool _preparing = true;
    [ObservableProperty] private string _refText = "";
    [ObservableProperty] private string _refMessage = "";
    [ObservableProperty] private bool _refOk;
    [ObservableProperty] private string _chapterTitle = "";
    [ObservableProperty] private string _translation = "";
    [ObservableProperty] private string? _secondTranslation;
    [ObservableProperty] private bool _onePerSlide;
    [ObservableProperty] private int _slideIndex;
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private string _slideInfo = "";
    [ObservableProperty] private string _nextLine = "";
    [ObservableProperty] private bool _showing;
    [ObservableProperty] private bool _searchMode;
    [ObservableProperty] private string _searchInfo = "";
    [ObservableProperty] private ScriptureTheme? _selectedTheme;
    [ObservableProperty] private string _newThemeName = "";
    [ObservableProperty] private bool _showGuides;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private bool _planEditing;
    [ObservableProperty] private bool _styleOpen = true;
    [ObservableProperty] private string _lookupTime = "";

    // On-air state, as in every section.
    [ObservableProperty] private bool _notRouted = true;
    [ObservableProperty] private ScreenFrame? _onAirFrame;
    [ObservableProperty] private ScreenKind _onAirKind;
    [ObservableProperty] private string _onAirName = "";
    [ObservableProperty] private bool _hasOnAir;
    [ObservableProperty] private bool _isLive;

    public ObservableCollection<TranslationChip> Translations { get; } = [];
    public ObservableCollection<TranslationChip> SecondChoices { get; } = [];
    public ObservableCollection<BookChip> Suggestions { get; } = [];
    public ObservableCollection<VerseRow> Verses { get; } = [];
    public ObservableCollection<PlanItem> Plan { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<ScriptureTheme> Themes { get; } = [];
    public ObservableCollection<AirBar> Bars { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 1 && !RefOk;
    public bool HasHistory => History.Count > 0;
    public bool HasPlan => Plan.Count > 0;
    public bool SideBySide => SecondTranslation is not null;
    public bool MayEditThemes => Live.User is { } u && !u.IsTrainee && (u.IsAdmin || u.Roles.HasFlag(Role.Scripture));
    public bool IsAdmin => Live.User?.IsAdmin == true;

    // ---------- Reference box ----------

    partial void OnRefTextChanged(string value)
    {
        if (!_suppressRefParse) ParseRef();
    }

    private void ParseRef()
    {
        var text = RefText.Trim();
        Suggestions.Clear();
        if (text.Length == 0)
        {
            RefOk = false;
            RefMessage = "Type a reference, for example jn 3 16, ps 23 or 1co 13 4-7. Or type words to search.";
            OnPropertyChanged(nameof(HasSuggestions));
            return;
        }

        var sw = Stopwatch.StartNew();
        var r = ReferenceParser.Parse(text);
        if (r.Ok)
        {
            SearchMode = false;
            SetReference(r.Reference!);
            LookupTime = $"{sw.ElapsedMilliseconds} ms";
            return;
        }

        RefOk = false;
        foreach (var b in ReferenceParser.Suggest(text)) Suggestions.Add(new BookChip(b));
        OnPropertyChanged(nameof(HasSuggestions));

        // Words that are not a reference: search the whole Bible.
        var looksLikeWords = text.Contains(' ') && !char.IsDigit(text[^1]) || text.StartsWith('"');
        if (looksLikeWords && Suggestions.Count == 0) RunSearch(text);
        else RefMessage = r.Error ?? "";
    }

    private void RunSearch(string text)
    {
        var tr = Current;
        if (tr is null) return;
        var sw = Stopwatch.StartNew();
        var hits = tr.Search(text, 150);
        SearchMode = true;
        Verses.Clear();
        foreach (var h in hits)
        {
            var book = BibleBooks.Get(h.Book);
            Verses.Add(new VerseRow(h.Chapter, h.Verse, h.Text) { Label = $"{book.ShortName} {h.Chapter}:{h.Verse}", Book = h.Book });
        }
        ChapterTitle = "Word search";
        SearchInfo = hits.Count == 0 ? "No verses have all these words." : $"{hits.Count}{(hits.Count == 150 ? "+" : "")} verses · {sw.ElapsedMilliseconds} ms";
        RefMessage = hits.Count == 0 ? $"No verses in the {tr.Abbrev} have all these words." : "Pick a verse below, or keep typing.";
    }

    [RelayCommand]
    private void PickBook(BookChip chip)
    {
        RefText = chip.Book.Name + " ";
    }

    private void SetReference(BibleReference r, bool updateText = false)
    {
        _reference = r;
        RefOk = true;
        if (updateText)
        {
            _suppressRefParse = true;
            RefText = r.ToString();
            _suppressRefParse = false;
        }
        OnPropertyChanged(nameof(HasSuggestions));
        RebuildSlides(keepIndex: false);
        LoadChapter(r.Book, r.First.StartChapter);
        var tr = Current;
        RefMessage = tr is null ? "No Bible translations on this PC yet." : $"✓ {r} · {tr.Abbrev}";
    }

    private BibleTranslation? Current => S.Bibles.Get(Translation) ?? S.Bibles.All.FirstOrDefault();
    private BibleTranslation? Second => SecondTranslation is null ? null : S.Bibles.Get(SecondTranslation);

    private void LoadChapter(BibleBook book, int chapter)
    {
        var tr = Current;
        Verses.Clear();
        ChapterTitle = $"{book.Name} {chapter}";
        if (tr is null) return;
        var count = Math.Max(tr.VersesIn(book.Number, chapter), book.VersesIn(chapter));
        for (var v = 1; v <= count; v++)
        {
            var text = tr.Verse(book.Number, chapter, v);
            if (text is null) continue;
            Verses.Add(new VerseRow(chapter, v, text.Length == 0 ? "(printed with the verse before)" : text) { Book = book.Number });
        }
        MarkRows();
    }

    private void MarkRows()
    {
        var onSlide = CurrentSlide?.Columns.FirstOrDefault()?.Verses.SelectMany(v => Enumerable.Range(v.Verse, (v.ThroughVerse ?? v.Verse) - v.Verse + 1).Select(n => (v.Chapter, n))).ToHashSet() ?? [];
        var selected = _reference?.Verses().ToHashSet() ?? [];
        foreach (var row in Verses)
        {
            if (SearchMode) { row.IsSelected = false; row.IsCurrent = false; continue; }
            row.IsSelected = selected.Contains((row.Chapter, row.Verse));
            row.IsCurrent = onSlide.Contains((row.Chapter, row.Verse));
            row.IsShowing = Showing && row.IsCurrent;
        }
        var first = Verses.ToList().FindIndex(r => r.IsCurrent || r.IsSelected);
        if (first >= 0) ScrollToRow?.Invoke(first);
    }

    /// <summary>Asks the view to bring a row of the chapter list into sight.</summary>
    public event Action<int>? ScrollToRow;

    [RelayCommand]
    private void PickVerse(VerseRow row)
    {
        var book = BibleBooks.Get(row.Book);
        var r = new BibleReference(book, [new VerseRange(row.Chapter, row.Verse, row.Chapter, row.Verse)]);
        if (SearchMode)
        {
            SearchMode = false;
            SetReference(r, updateText: true);
            return;
        }
        SetReference(r, updateText: true);
    }

    // ---------- Slides ----------

    public ScriptureSlide? CurrentSlide => SlideIndex >= 0 && SlideIndex < _slides.Count ? _slides[SlideIndex] : null;

    private ScriptureTheme Theme => Editor.ToTheme();

    private void RebuildSlides(bool keepIndex)
    {
        var tr = Current;
        if (_reference is null || tr is null)
        {
            _slides = [];
            UpdatePreview();
            return;
        }
        var old = SlideIndex;
        _slides = ScriptureSlides.Build(_reference.Book, _reference.Verses().ToList(), tr, Second, Theme, OnePerSlide);
        SlideIndex = keepIndex ? Math.Clamp(old, 0, Math.Max(0, _slides.Count - 1)) : 0;
        UpdatePreview();
    }

    partial void OnSlideIndexChanged(int value) => UpdatePreview();
    partial void OnOnePerSlideChanged(bool value) { RebuildSlides(false); }

    private void UpdatePreview()
    {
        var slide = CurrentSlide;
        PreviewFrame = slide is null ? null : new ScreenFrame("preview", MasterMode.None,
            [new LayerItem(Layer.Scripture, "local", "Scripture", SectionKind.Scripture, ContentOf(slide), true, 0)]);
        SlideInfo = _slides.Count > 1 ? $"Slide {SlideIndex + 1} of {_slides.Count}" : "";
        NextLine = SlideIndex + 1 < _slides.Count
            ? $"{_slides[SlideIndex + 1].Reference} · {Trim(_slides[SlideIndex + 1].PlainText)}"
            : NextVerseRef() is { } n && Current?.Verse(n.Book.Number, n.First.StartChapter, n.First.StartVerse) is { } t
                ? $"{n} · {Trim(t)}"
                : "End of the book";
        MarkRows();
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private static SlideContent ContentOf(ScriptureSlide s)
    {
        var tr = string.Join(" | ", s.Columns.Select(c => c.Translation));
        return new SlideContent(s.PlainText, $"{s.Reference} · {tr}", s.Reference, s);
    }

    // ---------- Sending ----------

    [RelayCommand]
    private async Task Show()
    {
        if (CurrentSlide is not { } slide || Live.Client is not { } c) return;
        Showing = true;
        MarkRows();
        AddHistory(slide);
        if (!IsTrainee) await c.SendContentAsync(SectionKind.Scripture, ContentOf(slide));
    }

    [RelayCommand]
    private async Task Clear()
    {
        Showing = false;
        MarkRows();
        if (Live.Client is { } c) await c.ClearContentAsync(SectionKind.Scripture);
    }

    [RelayCommand]
    private Task Next() => Step(1);

    [RelayCommand]
    private Task Previous() => Step(-1);

    private async Task Step(int delta)
    {
        if (_reference is null) return;
        var i = SlideIndex + delta;
        if (i >= 0 && i < _slides.Count)
        {
            SlideIndex = i;
        }
        else
        {
            // Past the end of the selection: carry on through the chapter (and into the next one).
            var r = delta > 0 ? NextVerseRef() : PreviousVerseRef();
            if (r is null) return;
            SetReference(r, updateText: true);
            if (delta < 0) SlideIndex = Math.Max(0, _slides.Count - 1);
        }
        if (Showing) await Show();
    }

    private (int Chapter, int Verse) LastShown()
    {
        var v = _slides.Count > 0 ? _slides[^1].Columns[0].Verses[^1] : null;
        if (v is not null) return (v.Chapter, v.ThroughVerse ?? v.Verse);
        var last = _reference!.Ranges[^1];
        return (last.EndChapter, last.EndVerse);
    }

    private BibleReference? NextVerseRef()
    {
        if (_reference is null) return null;
        var book = _reference.Book;
        var tr = Current;
        var (c, v) = LastShown();
        for (var guard = 0; guard < 400; guard++)
        {
            v++;
            if (v > Math.Max(book.VersesIn(c), tr?.VersesIn(book.Number, c) ?? 0)) { c++; v = 1; }
            if (c > book.Chapters) return null;
            if (tr?.Verse(book.Number, c, v) is { Length: > 0 }) return new BibleReference(book, [new VerseRange(c, v, c, v)]);
        }
        return null;
    }

    private BibleReference? PreviousVerseRef()
    {
        if (_reference is null) return null;
        var book = _reference.Book;
        var tr = Current;
        var first = _slides.Count > 0 ? _slides[0].Columns[0].Verses[0] : null;
        var (c, v) = first is null ? (_reference.First.StartChapter, _reference.First.StartVerse) : (first.Chapter, first.Verse);
        for (var guard = 0; guard < 400; guard++)
        {
            v--;
            if (v < 1) { c--; if (c < 1) return null; v = Math.Max(book.VersesIn(c), tr?.VersesIn(book.Number, c) ?? 0); }
            if (tr?.Verse(book.Number, c, v) is { Length: > 0 }) return new BibleReference(book, [new VerseRange(c, v, c, v)]);
        }
        return null;
    }

    // ---------- Translations ----------

    private void RefreshTranslations()
    {
        var all = S.Bibles.All;
        if (all.Count == 0) return;
        if (string.IsNullOrEmpty(Translation) || S.Bibles.Get(Translation) is null)
            Translation = S.Bibles.Get(Live.ScriptureSettings.DefaultTranslation)?.Abbrev ?? all[0].Abbrev;
        Translations.Clear();
        SecondChoices.Clear();
        SecondChoices.Add(new TranslationChip("", "Off") { IsSelected = SecondTranslation is null });
        var n = 0;
        foreach (var t in all)
        {
            n++;
            Translations.Add(new TranslationChip(t.Abbrev, t.Name) { IsSelected = t.Abbrev == Translation, Key = n <= 9 ? $"Alt+{n}" : "" });
            SecondChoices.Add(new TranslationChip(t.Abbrev, t.Name) { IsSelected = t.Abbrev == SecondTranslation });
        }
    }

    [RelayCommand]
    private async Task PickTranslation(TranslationChip chip)
    {
        Translation = chip.Abbrev;
        if (SecondTranslation == chip.Abbrev) SecondTranslation = null;
        await AfterTranslationChange();
    }

    [RelayCommand]
    private async Task PickSecond(TranslationChip chip)
    {
        SecondTranslation = chip.Abbrev.Length == 0 || chip.Abbrev == Translation ? null : chip.Abbrev;
        await AfterTranslationChange();
    }

    private async Task AfterTranslationChange()
    {
        foreach (var t in Translations) t.IsSelected = t.Abbrev == Translation;
        foreach (var t in SecondChoices) t.IsSelected = (t.Abbrev.Length == 0 && SecondTranslation is null) || t.Abbrev == SecondTranslation;
        OnPropertyChanged(nameof(SideBySide));
        if (_reference is not null)
        {
            RebuildSlides(keepIndex: true);
            LoadChapter(_reference.Book, CurrentSlide?.Columns[0].Verses[0].Chapter ?? _reference.First.StartChapter);
            RefMessage = $"✓ {_reference} · {Current?.Abbrev}";
        }
        else if (SearchMode) ParseRef();
        if (Showing) await Show();
    }

    private void OnBiblesUpdated() => Dispatcher.UIThread.Post(() =>
    {
        RefreshTranslations();
        if (_reference is not null) RebuildSlides(true);
    });

    // ---------- Themes ----------

    private void LoadThemes()
    {
        var keep = SelectedTheme?.Id;
        Themes.Clear();
        foreach (var t in Live.ScriptureThemes) Themes.Add(t);
        var pick = Themes.FirstOrDefault(t => t.Id == keep) ?? Themes.FirstOrDefault(t => t.Id == Live.ScriptureSettings.DefaultThemeId) ?? Themes.FirstOrDefault();
        if (pick is not null && (SelectedTheme is null || SelectedTheme.Id != pick.Id)) SelectedTheme = pick;
    }

    partial void OnSelectedThemeChanged(ScriptureTheme? value)
    {
        if (value is null) return;
        Editor.Load(value);
        ApplyEditor();
    }

    private void OnEditorChanged()
    {
        _themeDebounce.Stop();
        _themeDebounce.Start();
    }

    private async void ApplyEditor()
    {
        _themeDebounce.Stop();
        RebuildSlides(keepIndex: true);
        // While showing, style changes follow on the screen.
        if (Showing) await Show();
    }

    public bool ThemeIsDirty => SelectedTheme is { } t && JsonSerializer.Serialize(t, Wire.Options) != JsonSerializer.Serialize(Editor.ToTheme() with { Name = t.Name }, Wire.Options);

    [RelayCommand]
    private async Task SaveTheme()
    {
        if (Live.Client is not { } c) return;
        var t = Editor.ToTheme();
        await c.SendAsync(new SaveScriptureTheme(t));
        S.Live.RaiseToast($"Saved the {t.Name} theme for every PC.");
    }

    [RelayCommand]
    private async Task SaveThemeAsNew()
    {
        if (Live.Client is not { } c) return;
        var name = string.IsNullOrWhiteSpace(NewThemeName) ? Editor.Name + " copy" : NewThemeName.Trim();
        var t = Editor.ToTheme() with { Id = "t-" + Guid.NewGuid().ToString("N")[..8], Name = name };
        await c.SendAsync(new SaveScriptureTheme(t));
        NewThemeName = "";
        _pendingThemeId = t.Id;
        S.Live.RaiseToast($"Added the {name} theme.");
    }

    private string? _pendingThemeId;

    [RelayCommand]
    private async Task DeleteTheme()
    {
        if (Live.Client is not { } c || SelectedTheme is not { } t) return;
        await c.SendAsync(new DeleteScriptureTheme(t.Id));
    }

    [RelayCommand]
    private void ResetTheme()
    {
        if (SelectedTheme is { } t) Editor.Load(t);
        ApplyEditor();
    }

    public string ExportTheme() => JsonSerializer.Serialize(Editor.ToTheme(), new JsonSerializerOptions(Wire.Options) { WriteIndented = true });

    public async Task ImportThemeAsync(string json)
    {
        var t = JsonSerializer.Deserialize<ScriptureTheme>(json, Wire.Options) ?? throw new InvalidDataException("That file is not a NEXORA theme.");
        t = t with { Id = "t-" + Guid.NewGuid().ToString("N")[..8] };
        if (Live.Client is { } c) await c.SendAsync(new SaveScriptureTheme(t));
        _pendingThemeId = t.Id;
    }

    /// <summary>Uploads a background picture to the Live Output PC (copied to every PC) and uses it.</summary>
    public async Task UseBackgroundAsync(string path)
    {
        if (Live.Client is not { } c) return;
        var name = await c.UploadAsync("media", Path.GetFileName(path), path);
        // Keep a local copy at once so the preview does not wait for the round trip.
        var local = Path.Combine(AppPaths.MediaDir, name);
        if (!File.Exists(local)) File.Copy(path, local, overwrite: true);
        Editor.Background = BackgroundKind.Image;
        Editor.ImageId = name;
    }

    // ---------- Pastor's list and history ----------

    [RelayCommand]
    private void EditPlan() => PlanEditing = true;

    [RelayCommand]
    private void AddPlan()
    {
        var found = ReferenceParser.ExtractAll(PlanText);
        foreach (var (r, note) in found) Plan.Add(new PlanItem(Plan.Count + 1, r.ToString(), note));
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(PlanText))
        {
            S.Live.RaiseToast("No Bible references were found in that text.");
            return;
        }
        PlanText = "";
        PlanEditing = false;
        OnPropertyChanged(nameof(HasPlan));
        SaveDay();
    }

    [RelayCommand]
    private void CancelPlan()
    {
        PlanText = "";
        PlanEditing = false;
    }

    [RelayCommand]
    private void RemovePlan(PlanItem item)
    {
        Plan.Remove(item);
        for (var i = 0; i < Plan.Count; i++) Plan[i].Number = i + 1;
        OnPropertyChanged(nameof(HasPlan));
        SaveDay();
    }

    [RelayCommand]
    private void ClearPlan()
    {
        Plan.Clear();
        OnPropertyChanged(nameof(HasPlan));
        SaveDay();
    }

    [RelayCommand]
    private void PickPlan(PlanItem item)
    {
        foreach (var p in Plan) p.IsCurrent = p == item;
        RefText = item.Reference;
    }

    /// <summary>Moves to the next item of the pastor's list (Page Down / Ctrl+Down).</summary>
    private void NextPlan(int delta)
    {
        if (Plan.Count == 0) return;
        var i = Plan.ToList().FindIndex(p => p.IsCurrent) + delta;
        if (i < 0 || i >= Plan.Count) return;
        PickPlan(Plan[i]);
    }

    [RelayCommand]
    private void PickHistory(HistoryItem item)
    {
        if (S.Bibles.Get(item.Translation) is not null && item.Translation != Translation)
        {
            Translation = item.Translation;
            foreach (var t in Translations) t.IsSelected = t.Abbrev == Translation;
        }
        RefText = item.Reference;
    }

    private void AddHistory(ScriptureSlide slide)
    {
        var tr = slide.Columns[0].Translation;
        if (History.Count > 0 && History[0].Reference == slide.Reference && History[0].Translation == tr) return;
        History.Insert(0, new HistoryItem(slide.Reference, tr, DateTime.Now.ToString("h:mm tt")));
        while (History.Count > 200) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(HasHistory));
        SaveDay();
    }

    private sealed record DayFile(DateTime Date, List<PlanItem.Saved> Plan, List<HistoryItem> History, string? Translation);

    private static string DayPath => Path.Combine(AppPaths.LocalDir, "scripture-today.json");

    private void LoadDay()
    {
        try
        {
            if (!File.Exists(DayPath)) return;
            var d = JsonSerializer.Deserialize<DayFile>(File.ReadAllText(DayPath), Wire.Options);
            if (d is null) return;
            if (d.Translation is not null) Translation = d.Translation;
            if (d.Date.Date != DateTime.Today) return; // a new day starts with an empty list
            foreach (var p in d.Plan) Plan.Add(new PlanItem(Plan.Count + 1, p.Reference, p.Note));
            foreach (var h in d.History) History.Add(h);
        }
        catch (Exception) { }
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void SaveDay()
    {
        try
        {
            var d = new DayFile(DateTime.Today, Plan.Select(p => new PlanItem.Saved(p.Reference, p.Note)).ToList(), History.ToList(), Translation);
            File.WriteAllText(DayPath, JsonSerializer.Serialize(d, Wire.Options));
        }
        catch (Exception) { }
    }

    // ---------- Keyboard ----------

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Alt) && key is >= Key.D1 and <= Key.D9)
        {
            var i = key - Key.D1;
            if (i < Translations.Count) _ = PickTranslation(Translations[i]);
            return true;
        }
        if (mods.HasFlag(KeyModifiers.Control) && key is Key.Down or Key.Up)
        {
            NextPlan(key == Key.Down ? 1 : -1);
            return true;
        }
        switch (key)
        {
            case Key.Down or Key.Right:
                _ = Next();
                return true;
            case Key.Up or Key.Left:
                _ = Previous();
                return true;
            case Key.PageDown:
                NextPlan(1);
                return true;
            case Key.PageUp:
                NextPlan(-1);
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

    /// <summary>Keys pressed while typing in the reference box: Enter, Esc and the up/down arrows still work.</summary>
    public bool HandleReferenceBoxKey(Key key, KeyModifiers mods)
    {
        if (key is Key.Left or Key.Right) return false; // move the caret
        if (key == Key.Enter && SearchMode && Verses.Count > 0)
        {
            PickVerse(Verses[0]);
            return true;
        }
        if (key == Key.Enter && !RefOk && Suggestions.Count > 0)
        {
            PickBook(Suggestions[0]);
            return true;
        }
        return HandleKey(key, mods);
    }

    // ---------- Live ----------

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Snapshot)) UpdateAir();
        if (e.PropertyName == nameof(Live.Info)) OnPropertyChanged(nameof(Branding));
        if (e.PropertyName == nameof(Live.User))
        {
            OnPropertyChanged(nameof(MayEditThemes));
            OnPropertyChanged(nameof(IsTrainee));
            OnPropertyChanged(nameof(IsAdmin));
        }
        if (e.PropertyName == nameof(Live.Library))
        {
            LoadThemes();
            if (_pendingThemeId is { } id && Themes.FirstOrDefault(t => t.Id == id) is { } created)
            {
                _pendingThemeId = null;
                SelectedTheme = created;
            }
            RefreshTranslations();
        }
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
        IsLive = firstLive is not null;
        if (firstLive is not null)
        {
            OnAirFrame = snap.FrameFor(firstLive.Id);
            OnAirKind = firstLive.Kind;
            OnAirName = firstLive.Name;
        }
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnLive;
        LibrarySync.BiblesUpdated -= OnBiblesUpdated;
        S.Bibles.Changed -= OnBiblesUpdated;
        _themeDebounce.Stop();
        SaveDay();
    }
}

public sealed partial class TranslationChip(string abbrev, string name) : ObservableObject
{
    public string Abbrev => abbrev;
    public string Name => name;
    public string Text => abbrev.Length == 0 ? name : abbrev;
    public string Key { get; init; } = "";
    public string Tip => Key.Length > 0 ? $"{name} ({Key})" : name;
    [ObservableProperty] private bool _isSelected;
}

public sealed record BookChip(BibleBook Book)
{
    public string Name => Book.Name;
}

public sealed partial class VerseRow(int chapter, int verse, string text) : ObservableObject
{
    public int Chapter => chapter;
    public int Verse => verse;
    public string Text => text;
    public int Book { get; init; }
    public string? Label { get; init; }
    public string Number => Label ?? verse.ToString();
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isShowing;
}

public sealed partial class PlanItem(int number, string reference, string note) : ObservableObject
{
    public sealed record Saved(string Reference, string Note);

    [ObservableProperty] private int _number = number;
    public string Reference => reference;
    public string Note => note;
    public bool HasNote => note.Length > 0;
    [ObservableProperty] private bool _isCurrent;
}

public sealed record HistoryItem(string Reference, string Translation, string Time);
