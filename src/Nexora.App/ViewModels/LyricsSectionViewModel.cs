using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Services;
using Nexora.Core.Model;
using Nexora.Core.Protocol;
using Nexora.Core.Songs;

namespace Nexora.App.ViewModels;

/// <summary>
/// Song Lyrics: find a song, put it in the list for the day, click a slide (or press its key) and it is on
/// the screen. V then 2 jumps to verse 2; C, B, P, T, E jump to the chorus, bridge, pre-chorus, tag and ending.
/// </summary>
public sealed partial class LyricsSectionViewModel : SectionBase, IKeyHandler
{
    private readonly DispatcherTimer _themeDebounce;
    private readonly DispatcherTimer _jumpTimer;
    private char? _jumpLetter;
    private string? _pendingThemeId;

    public LyricsSectionViewModel() : base(SectionKind.Lyrics)
    {
        Editor.Changed += () => { _themeDebounce?.Stop(); _themeDebounce?.Start(); };
        _themeDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => ApplyEditor());
        _jumpTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Input, (_, _) => FinishJump(null));
        S.Songs.Changed += OnSongsChanged;
        var set = Live.LyricsSettings;
        Animation = set.Animation;
        AnimationMs = set.AnimationMs;
        LoadThemes();
        RefreshLibrary();
        RefreshList();
    }

    public ThemeEditor Editor { get; } = new();

    // ---------- State ----------

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private SongRow? _selectedLibrarySong;
    [ObservableProperty] private Song? _song;
    [ObservableProperty] private string? _arrangement;
    [ObservableProperty] private int _slideIndex = -1;
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private ScriptureTheme? _selectedTheme;
    [ObservableProperty] private LyricAnimation _animation;
    [ObservableProperty] private double _animationMs = 450;
    [ObservableProperty] private bool _overCamera;
    [ObservableProperty] private string _newThemeName = "";
    [ObservableProperty] private bool _styleOpen;
    [ObservableProperty] private string _libraryInfo = "";
    [ObservableProperty] private string _jumpHint = "";
    [ObservableProperty] private bool _blanked;

    // Song editor
    [ObservableProperty] private bool _editing;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editAuthor = "";
    [ObservableProperty] private string _editCopyright = "";
    [ObservableProperty] private string _editCcli = "";
    [ObservableProperty] private string _editKey = "";
    [ObservableProperty] private string _editTags = "";
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private string _editOrder = "";
    [ObservableProperty] private int _editLines;
    [ObservableProperty] private string _editInfo = "";
    private string? _editId;

    // Import
    [ObservableProperty] private bool _importing;
    [ObservableProperty] private string _importStatus = "";

    public ObservableCollection<SongRow> Library { get; } = [];
    public ObservableCollection<ListRow> Today { get; } = [];
    public ObservableCollection<LyricTile> Tiles { get; } = [];
    public ObservableCollection<string> Arrangements { get; } = [];
    public ObservableCollection<ScriptureTheme> Themes { get; } = [];

    public static IReadOnlyList<LyricAnimation> Animations { get; } = Enum.GetValues<LyricAnimation>();
    public static IReadOnlyList<LinesChoice> LinesChoices { get; } =
        [new(0, "Whole part"), new(1, "1 line"), new(2, "2 lines"), new(3, "3 lines"), new(4, "4 lines"), new(6, "6 lines")];

    public bool HasSong => Song is not null;
    public bool HasArrangements => Arrangements.Count > 1;
    public bool HasToday => Today.Count > 0;
    public string SongTitle => Song?.Title ?? "";
    public string SongMeta => Song is null ? "" : string.Join("  ·  ", new[] { Song.Author, Song.Key is { Length: > 0 } k ? "Key " + k : "", Song.Ccli is { Length: > 0 } c ? "CCLI " + c : "" }.Where(x => x.Length > 0));

    public LinesChoice? SelectedLines
    {
        get => LinesChoices.FirstOrDefault(l => l.Lines == EffectiveLines);
        set
        {
            if (value is null || Song is not { } s) return;
            Song = s with { LinesPerSlide = value.Lines };
            if (MayEdit && Live.Client is { } c) _ = c.SendAsync(new SaveSongs([Song]));
            BuildTiles(keepIndex: false);
        }
    }

    public LinesChoice? EditLinesChoice
    {
        get => LinesChoices.FirstOrDefault(l => l.Lines == EditLines);
        set { if (value is not null) EditLines = value.Lines; }
    }

    private int EffectiveLines => Song is { LinesPerSlide: > 0 } s ? s.LinesPerSlide : Song?.LinesPerSlide ?? Live.LyricsSettings.LinesPerSlide;

    // ---------- Library ----------

    partial void OnSearchChanged(string value) => RefreshLibrary();

    partial void OnSongChanged(Song? value)
    {
        OnPropertyChanged(nameof(HasSong));
        OnPropertyChanged(nameof(SongTitle));
        OnPropertyChanged(nameof(SongMeta));
    }

    public bool IsExistingSong => _editId is not null;

    partial void OnArrangementChanged(string? value) => OnPropertyChanged(nameof(ArrangementChoice));

    private void OnSongsChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshLibrary();
        RefreshList();
        if (Song is { } s && S.Songs.Get(s.Id) is { } fresh && fresh != s)
        {
            Song = fresh;
            BuildTiles(keepIndex: true);
        }
    });

    private void RefreshLibrary()
    {
        var all = S.Songs.All;
        var hits = SongText.Search(all, Search).Take(300).ToList();
        Library.Clear();
        foreach (var s in hits) Library.Add(new SongRow(s));
        LibraryInfo = all.Count == 0 ? "No songs yet. Import from EasyWorship or another program, or add one." :
            Search.Trim().Length == 0 ? $"{all.Count} song{(all.Count == 1 ? "" : "s")}" : $"{hits.Count} of {all.Count} songs";
    }

    [RelayCommand]
    private void Open(SongRow row) => OpenSong(row.Song, null);

    [RelayCommand]
    private void OpenFromList(ListRow row)
    {
        if (S.Songs.Get(row.Entry.SongId) is { } s) OpenSong(s, row.Entry.Arrangement);
        foreach (var r in Today) r.IsCurrent = r == row;
    }

    private void OpenSong(Song s, string? arrangement)
    {
        Song = s;
        Arrangements.Clear();
        Arrangements.Add("As written");
        foreach (var a in s.Arrangements) Arrangements.Add(a.Name);
        Arrangement = arrangement ?? s.Arrangements.FirstOrDefault()?.Name;
        OnPropertyChanged(nameof(HasArrangements));
        OnPropertyChanged(nameof(SongTitle));
        OnPropertyChanged(nameof(SongMeta));
        if (s.ThemeId is { } tid && Themes.FirstOrDefault(t => t.Id == tid) is { } th) SelectedTheme = th;
        BuildTiles(keepIndex: false);
        SlideIndex = Tiles.Count > 0 ? 0 : -1;
        UpdatePreview();
    }

    public string? ArrangementChoice
    {
        get => Arrangement ?? "As written";
        set
        {
            Arrangement = value == "As written" ? null : value;
            BuildTiles(keepIndex: false);
        }
    }

    // ---------- The list for the day ----------

    private void RefreshList()
    {
        var keep = Today.FirstOrDefault(r => r.IsCurrent)?.Entry;
        Today.Clear();
        var n = 1;
        foreach (var e in Live.SongList.Entries)
        {
            var song = S.Songs.Get(e.SongId);
            Today.Add(new ListRow(n++, e, song?.Title ?? "(song not on this PC yet)", e.Arrangement ?? "") { IsCurrent = keep == e });
        }
        OnPropertyChanged(nameof(HasToday));
    }

    private Task SaveListAsync(IEnumerable<SongListEntry> entries) =>
        Live.Client?.SendAsync(new SaveSongList(new SongList(SongList.Today, entries.ToList()))) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task AddToList()
    {
        if (Song is not { } s) return;
        await SaveListAsync(Live.SongList.Entries.Append(new SongListEntry(s.Id, Arrangement)));
    }

    [RelayCommand]
    private Task AddRowToList(SongRow row) => SaveListAsync(Live.SongList.Entries.Append(new SongListEntry(row.Song.Id)));

    [RelayCommand]
    private Task RemoveFromList(ListRow row) => SaveListAsync(Live.SongList.Entries.Where((_, i) => i != row.Number - 1));

    [RelayCommand]
    private Task MoveUp(ListRow row)
    {
        var list = Live.SongList.Entries.ToList();
        var i = row.Number - 1;
        if (i <= 0 || i >= list.Count) return Task.CompletedTask;
        (list[i - 1], list[i]) = (list[i], list[i - 1]);
        return SaveListAsync(list);
    }

    [RelayCommand]
    private Task ClearList() => SaveListAsync([]);

    private void NextSong(int delta)
    {
        if (Today.Count == 0) return;
        var cur = Today.ToList().FindIndex(r => r.IsCurrent);
        var i = Math.Clamp(cur + delta, 0, Today.Count - 1);
        OpenFromList(Today[i]);
    }

    // ---------- Slides ----------

    private void BuildTiles(bool keepIndex)
    {
        var keep = SlideIndex;
        Tiles.Clear();
        if (Song is not { } s) return;
        var lines = EffectiveLines;
        var parts = s.Ordered(Arrangement);
        var i = 0;
        var seen = new Dictionary<string, int>();
        foreach (var p in parts)
        {
            var slides = SongText.Slides(p, lines);
            for (var k = 0; k < slides.Count; k++)
            {
                seen.TryGetValue(p.Label, out var times);
                Tiles.Add(new LyricTile(i++, p.Label, SongText.Code(p.Label), slides[k], k == 0, slides.Count > 1 ? $"{k + 1}/{slides.Count}" : ""));
            }
            seen[p.Label] = seen.GetValueOrDefault(p.Label) + 1;
        }
        SlideIndex = keepIndex ? Math.Min(keep, Tiles.Count - 1) : Tiles.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedLines));
        UpdatePreview();
    }

    partial void OnSlideIndexChanged(int value) => UpdatePreview();

    private LyricSlide? SlideAt(int i)
    {
        if (Song is not { } s || i < 0 || i >= Tiles.Count) return null;
        var t = Tiles[i];
        var theme = Editor.ToTheme();
        string? footer = null;
        var set = Live.LyricsSettings;
        if (i == 0 && set.CopyrightOnFirstSlide)
        {
            var bits = new List<string>();
            if (s.Author.Length > 0) bits.Add(s.Author);
            if (s.Copyright.Length > 0) bits.Add(s.Copyright.StartsWith('©') ? s.Copyright : "© " + s.Copyright);
            if (s.Ccli.Length > 0) bits.Add("CCLI Song # " + s.Ccli);
            if (set.CcliLicence.Length > 0) bits.Add("CCLI Licence # " + set.CcliLicence);
            if (bits.Count > 0) footer = $"{s.Title} · " + string.Join(" · ", bits);
        }
        return new LyricSlide(s.Id, s.Title, t.Label, t.Lines, theme, Animation, (int)AnimationMs, footer, i, Tiles.Count);
    }

    private SlideContent? ContentAt(int i)
    {
        if (SlideAt(i) is not { } slide) return null;
        var next = i + 1 < Tiles.Count ? string.Join("\n", Tiles[i + 1].Lines) : NextSongTitle() is { } n ? "Next song: " + n : null;
        return new SlideContent(slide.PlainText, $"{slide.SongTitle} · {slide.PartLabel}", slide.PartLabel, Lyrics: slide, Next: next);
    }

    private string? NextSongTitle()
    {
        var cur = Today.ToList().FindIndex(r => r.IsCurrent);
        return cur >= 0 && cur + 1 < Today.Count ? Today[cur + 1].Title : null;
    }

    private void UpdatePreview()
    {
        foreach (var t in Tiles)
        {
            t.IsSelected = t.Index == SlideIndex;
            t.IsShowing = Showing && !Blanked && t.Index == _shownIndex && Song?.Id == _shownSong;
        }
        var c = ContentAt(SlideIndex);
        PreviewFrame = c is null ? null : PreviewOf(c, withCamera: OverCamera);
        NextText = SlideIndex + 1 < Tiles.Count ? string.Join(" / ", Tiles[SlideIndex + 1].Lines) : NextSongTitle() is { } n ? "Next song: " + n : "End of the song";
    }

    partial void OnOverCameraChanged(bool value) => UpdatePreview();
    partial void OnAnimationChanged(LyricAnimation value) => UpdatePreview();
    partial void OnAnimationMsChanged(double value) => UpdatePreview();

    private int _shownIndex = -1;
    private string? _shownSong;

    [RelayCommand]
    private async Task PickTile(LyricTile tile)
    {
        SlideIndex = tile.Index;
        // Following live: a click is on the screen at once, as operators expect.
        if (Showing) await Show();
    }

    [RelayCommand]
    private async Task SendTile(LyricTile tile)
    {
        SlideIndex = tile.Index;
        await Show();
    }

    [RelayCommand]
    private async Task Show()
    {
        if (ContentAt(SlideIndex) is not { } c) return;
        Blanked = false;
        _shownIndex = SlideIndex;
        _shownSong = Song?.Id;
        await SendAsync(c);
        UpdatePreview();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (SlideIndex + 1 < Tiles.Count) SlideIndex++;
        else if (Showing) return;
        if (Showing) await Show();
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (SlideIndex > 0) SlideIndex--;
        if (Showing) await Show();
    }

    /// <summary>Takes the words off but keeps the background (the band is between songs).</summary>
    [RelayCommand]
    private async Task Blank()
    {
        var theme = Editor.ToTheme();
        var slide = new LyricSlide(Song?.Id ?? "", Song?.Title ?? "", "", [], theme, LyricAnimation.Fade, 300);
        Blanked = true;
        await SendAsync(new SlideContent("", "Blank", "Blank", Lyrics: slide));
        UpdatePreview();
    }

    [RelayCommand]
    private async Task Logo()
    {
        Blanked = true;
        await SendAsync(new SlideContent("Logo", "Logo", "Logo", Logo: true));
        UpdatePreview();
    }

    [RelayCommand]
    private async Task Clear()
    {
        Blanked = false;
        _shownIndex = -1;
        await ClearAsync();
        UpdatePreview();
    }

    // ---------- Jump keys ----------

    private void Jump(char letter)
    {
        if (letter == 'V')
        {
            _jumpLetter = 'V';
            JumpHint = "Verse… press its number";
            _jumpTimer.Stop();
            _jumpTimer.Start();
            return;
        }
        var code = letter switch { 'C' => "C", 'B' => "B", 'P' => "PC", 'T' => "T", 'E' => "E", 'I' => "I", _ => null };
        if (code is null) return;
        GoToPart(code);
    }

    private void FinishJump(int? number)
    {
        _jumpTimer.Stop();
        if (_jumpLetter != 'V') return;
        _jumpLetter = null;
        JumpHint = "";
        GoToPart("V" + (number ?? 1));
    }

    private async void GoToPart(string code)
    {
        // The next time this part comes after the current slide, or the first time it comes at all.
        var matches = Tiles.Where(t => t.IsFirst && (t.Code.Equals(code, StringComparison.OrdinalIgnoreCase) || code.Length == 1 && t.Code.Equals(code + "1", StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0)
        {
            JumpHint = $"This song has no {code}";
            DispatcherTimer.RunOnce(() => JumpHint = "", TimeSpan.FromSeconds(2));
            return;
        }
        var target = matches.FirstOrDefault(t => t.Index > SlideIndex) ?? matches[0];
        SlideIndex = target.Index;
        if (Showing) await Show();
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (Editing) return false;
        if (_jumpLetter == 'V' && key is >= Key.D1 and <= Key.D9)
        {
            FinishJump(key - Key.D0);
            return true;
        }
        if (_jumpLetter == 'V' && key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            FinishJump(key - Key.NumPad0);
            return true;
        }
        if (mods.HasFlag(KeyModifiers.Control) && key is Key.Down or Key.Up)
        {
            NextSong(key == Key.Down ? 1 : -1);
            return true;
        }
        if (mods != KeyModifiers.None && mods != KeyModifiers.Shift) return false;
        switch (key)
        {
            case Key.Right or Key.Down or Key.Space or Key.PageDown:
                _ = Next();
                return true;
            case Key.Left or Key.Up or Key.PageUp:
                _ = Previous();
                return true;
            case Key.Enter:
                _ = Show();
                return true;
            case Key.Escape or Key.F3:
                _ = Clear();
                return true;
            case Key.F1:
                _ = Blank();
                return true;
            case Key.F2:
                _ = Logo();
                return true;
            case Key.V: Jump('V'); return true;
            case Key.C: Jump('C'); return true;
            case Key.B: Jump('B'); return true;
            case Key.P: Jump('P'); return true;
            case Key.T: Jump('T'); return true;
            case Key.E: Jump('E'); return true;
            case Key.I: Jump('I'); return true;
            case >= Key.D1 and <= Key.D9 when Tiles.Count > 0:
                // A number on its own: that slide of the song.
                var n = key - Key.D1;
                if (n < Tiles.Count) { SlideIndex = n; if (Showing) _ = Show(); }
                return true;
        }
        return false;
    }

    // ---------- Themes ----------

    private void LoadThemes()
    {
        var keep = SelectedTheme?.Id ?? _pendingThemeId;
        Themes.Clear();
        foreach (var t in Live.LyricThemes) Themes.Add(t);
        var pick = Themes.FirstOrDefault(t => t.Id == keep) ?? Themes.FirstOrDefault(t => t.Id == Live.LyricsSettings.DefaultThemeId) ?? Themes.FirstOrDefault();
        if (keep == _pendingThemeId && pick?.Id == keep) _pendingThemeId = null;
        if (pick is not null && (SelectedTheme is null || SelectedTheme.Id != pick.Id)) SelectedTheme = pick;
    }

    partial void OnSelectedThemeChanged(ScriptureTheme? value)
    {
        if (value is null) return;
        Editor.Load(value);
        ApplyEditor();
    }

    private async void ApplyEditor()
    {
        _themeDebounce.Stop();
        UpdatePreview();
        if (Showing && !Blanked && _shownIndex >= 0 && _shownSong == Song?.Id && ContentAt(_shownIndex) is { } c) await SendAsync(c);
    }

    [RelayCommand]
    private async Task SaveTheme()
    {
        if (Live.Client is not { } c) return;
        var t = Editor.ToTheme();
        await c.SendAsync(new SaveTheme(ThemeKind.Lyrics, t));
        Live.RaiseToast($"Saved the {t.Name} lyric theme for every PC.");
    }

    [RelayCommand]
    private async Task SaveThemeAsNew()
    {
        if (Live.Client is not { } c) return;
        var name = string.IsNullOrWhiteSpace(NewThemeName) ? Editor.Name + " copy" : NewThemeName.Trim();
        var t = Editor.ToTheme() with { Id = "l-" + Guid.NewGuid().ToString("N")[..8], Name = name };
        _pendingThemeId = t.Id;
        await c.SendAsync(new SaveTheme(ThemeKind.Lyrics, t));
        NewThemeName = "";
    }

    [RelayCommand]
    private async Task DeleteTheme()
    {
        if (Live.Client is not { } c || SelectedTheme is not { } t) return;
        await c.SendAsync(new DeleteTheme(ThemeKind.Lyrics, t.Id));
    }

    [RelayCommand]
    private void ResetTheme()
    {
        if (SelectedTheme is { } t) Editor.Load(t);
        ApplyEditor();
    }

    /// <summary>Makes this theme the one this song always uses.</summary>
    [RelayCommand]
    private async Task UseThemeForSong()
    {
        if (Song is not { } s || SelectedTheme is not { } t || Live.Client is not { } c) return;
        Song = s with { ThemeId = t.Id };
        await c.SendAsync(new SaveSongs([Song]));
        Live.RaiseToast($"{s.Title} will always use {t.Name}.");
    }

    public async Task UseBackgroundAsync(string path)
    {
        if (Live.Client is not { } c) return;
        var name = await c.UploadAsync("media", Path.GetFileName(path), path);
        var local = Path.Combine(Nexora.Core.Config.AppPaths.MediaDir, name);
        if (!File.Exists(local)) File.Copy(path, local, overwrite: true);
        Editor.Background = BackgroundKind.Image;
        Editor.ImageId = name;
    }

    // ---------- Song editor ----------

    [RelayCommand]
    private void NewSong()
    {
        _editId = null;
        EditTitle = EditAuthor = EditCopyright = EditCcli = EditKey = EditTags = EditText = EditOrder = "";
        EditLines = 0;
        OnPropertyChanged(nameof(IsExistingSong));
        EditInfo = "Paste the words. Blank lines separate the parts; a repeated part becomes the chorus. Lines like [Verse 1] or Chorus: name a part.";
        Editing = true;
    }

    [RelayCommand]
    private void EditSong()
    {
        if (Song is not { } s) return;
        _editId = s.Id;
        EditTitle = s.Title;
        EditAuthor = s.Author;
        EditCopyright = s.Copyright;
        EditCcli = s.Ccli;
        EditKey = s.Key;
        EditTags = s.Tags;
        EditText = SongText.ToEditorText(s.Parts);
        EditOrder = s.Arrangements.FirstOrDefault() is { } a ? string.Join(" ", a.Order.Select(SongText.Code)) : "";
        EditLines = s.LinesPerSlide;
        OnPropertyChanged(nameof(EditLinesChoice));
        OnPropertyChanged(nameof(IsExistingSong));
        EditInfo = "Order: the parts as sung, for example V1 C V2 C B C. Leave empty to sing them as written.";
        Editing = true;
    }

    partial void OnEditTextChanged(string value)
    {
        // A pasted song with a title on its first line and no parts named yet: take the title.
        if (_editId is null && EditTitle.Length == 0 && value.Contains('\n'))
        {
            var first = value.Replace("\r", "").Split('\n')[0].Trim();
            if (first.Length is > 0 and < 60 && SongText.LabelOf(first) is null && value.Replace("\r", "").Split('\n').Length > 4 && value.Replace("\r", "").Split('\n')[1].Trim().Length == 0)
                EditTitle = first;
        }
    }

    [RelayCommand]
    private void CancelEdit() => Editing = false;

    [RelayCommand]
    private async Task SaveSong()
    {
        if (Live.Client is not { } c) return;
        var title = EditTitle.Trim();
        var text = EditText.Replace("\r", "");
        if (title.Length == 0)
        {
            EditInfo = "Give the song a title.";
            return;
        }
        // Drop the title line if it was pasted at the top.
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == title) text = string.Join("\n", lines.Skip(1)).TrimStart('\n');
        var parts = SongText.Split(text);
        if (parts.Count == 0)
        {
            EditInfo = "There are no words yet.";
            return;
        }
        var order = SongText.ParseOrder(EditOrder, parts);
        var pastedOrder = _editId is null && order.Count == 0 ? SongText.PastedOrder(text) : [];
        if (pastedOrder.Count > parts.Count) order = pastedOrder;
        var old = _editId is null ? null : S.Songs.Get(_editId);
        var song = (old ?? new Song()) with
        {
            Id = _editId ?? SongImport.NewId(),
            Title = title,
            Author = EditAuthor.Trim(),
            Copyright = EditCopyright.Trim(),
            Ccli = EditCcli.Trim(),
            Key = EditKey.Trim(),
            Tags = EditTags.Trim(),
            Parts = parts,
            Arrangements = order.Count > 0 ? [new SongArrangement("Default", order)] : [],
            LinesPerSlide = EditLines,
            UpdatedUtc = DateTime.UtcNow,
        };
        await c.SendAsync(new SaveSongs([song]));
        Editing = false;
        OpenSong(song, song.Arrangements.FirstOrDefault()?.Name);
        Live.RaiseToast($"Saved {song.Title}.");
    }

    [RelayCommand]
    private async Task DeleteSong()
    {
        if (Song is not { } s || Live.Client is not { } c) return;
        await c.SendAsync(new DeleteSongs([s.Id]));
        Song = null;
        Tiles.Clear();
        PreviewFrame = null;
        OnPropertyChanged(nameof(SongTitle));
        Live.RaiseToast($"Deleted {s.Title}.");
    }

    // ---------- Import ----------

    /// <summary>Imports song files (EasyWorship Songs.db, OpenLyrics, OpenSong, SongSelect text, a zip or a folder).</summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (Live.Client is not { } c || Importing) return;
        Importing = true;
        ImportStatus = "Reading the songs…";
        try
        {
            var results = await Task.Run(() => paths.Select(SongImport.Load).ToList());
            var songs = results.SelectMany(r => r.Songs).ToList();
            var problems = results.SelectMany(r => r.Problems).ToList();
            // Songs already in the library (same title and first line) are updated rather than doubled.
            var existing = S.Songs.All.GroupBy(s => Key(s)).ToDictionary(g => g.Key, g => g.First());
            songs = songs.Select(s => existing.TryGetValue(Key(s), out var old) ? s with { Id = old.Id, ThemeId = old.ThemeId } : s).ToList();
            for (var i = 0; i < songs.Count; i += 200)
            {
                ImportStatus = $"Sending songs {i + 1}–{Math.Min(songs.Count, i + 200)} of {songs.Count}…";
                await c.SendAsync(new SaveSongs(songs.Skip(i).Take(200).ToList()));
                await Task.Delay(150);
            }
            var format = string.Join(", ", results.Select(r => r.Format).Distinct());
            ImportStatus = songs.Count == 0
                ? "No songs found in that file. " + string.Join(" ", problems.Take(3))
                : $"Imported {songs.Count} song{(songs.Count == 1 ? "" : "s")} ({format}).{(problems.Count > 0 ? $" {problems.Count} could not be read." : "")}";
            Live.RaiseToast(ImportStatus, songs.Count == 0);
        }
        catch (Exception ex)
        {
            ImportStatus = "The import failed: " + ex.Message;
            Live.RaiseToast(ImportStatus, true);
        }
        finally { Importing = false; }

        static string Key(Song s) => SongText.Fold(s.Title) + "|" + SongText.Fold(s.FirstLine);
    }

    // ---------- Live ----------

    protected override void OnLive(string? property)
    {
        if (property == nameof(Live.Library))
        {
            LoadThemes();
            RefreshList();
        }
        if (property == nameof(Live.Snapshot)) UpdatePreview();
    }

    protected override void ShowingChanged() => UpdatePreview();

    public override void OnLeave()
    {
        base.OnLeave();
        S.Songs.Changed -= OnSongsChanged;
        _themeDebounce.Stop();
        _jumpTimer.Stop();
    }
}

public sealed record LinesChoice(int Lines, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class SongRow(Song song) : ObservableObject
{
    public Song Song => song;
    public string Title => song.Title;
    public string Sub => song.FirstLine.Length > 60 ? song.FirstLine[..60] + "…" : song.FirstLine;
    public string Author => song.Author;
}

public sealed partial class ListRow(int number, SongListEntry entry, string title, string arrangement) : ObservableObject
{
    public int Number => number;
    public SongListEntry Entry => entry;
    public string Title => title;
    public string Arrangement => arrangement;
    [ObservableProperty] private bool _isCurrent;
}

public sealed partial class LyricTile(int index, string label, string code, IReadOnlyList<string> lines, bool isFirst, string page) : ObservableObject
{
    public int Index => index;
    public string Label => label;
    public string Code => code;
    public IReadOnlyList<string> Lines => lines;
    public string Text => string.Join("\n", lines);
    public bool IsFirst => isFirst;
    public string Page => page;
    public string Header => isFirst ? $"{label}" : $"{label}";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isShowing;
}
