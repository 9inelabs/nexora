using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// Minister Timer: set how many minutes a guest minister has and send a big timer to the Stage Monitor.
/// Green from the start, yellow when the time is getting low, red for the last minutes and over time.
/// The timer runs on the Live Output PC's clock, so it stays right if this PC is switched off.
/// </summary>
public sealed partial class MinisterTimerSectionViewModel : SectionBase, IKeyHandler
{
    private readonly DispatcherTimer _tick;
    private MinisterTimerShow? _timer;
    private bool _loading;
    private DateTime _sentUntil = DateTime.MinValue;

    public MinisterTimerSectionViewModel() : base(SectionKind.MinisterTimer)
    {
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _tick.Start();
        LoadMinisters();
        if (Current?.Minister is { } t)
        {
            // A timer already running (this PC restarted, or another PC started it).
            _timer = t;
            _loading = true;
            Name = t.Name;
            Purpose = t.Purpose;
            Minutes = Math.Max(1, t.AllowedSeconds / 60);
            YellowAt = t.YellowAtSeconds / 60;
            RedAt = t.RedAtSeconds / 60;
            CountOver = t.CountOver;
            Flash = t.Flash;
            FullScreen = t.Look == MinisterLook.FullScreen;
            _loading = false;
        }
        Tick();
        UpdatePreview();
    }

    public static IReadOnlyList<int> Presets { get; } = [5, 10, 15, 20, 30, 45];

    public ObservableCollection<MinisterRow> Ministers { get; } = [];

    [ObservableProperty] private string _name = "Guest Minister";
    [ObservableProperty] private string _purpose = "Sermon";
    [ObservableProperty] private int _minutes = 30;
    [ObservableProperty] private int _yellowAt = 10;
    [ObservableProperty] private int _redAt = 5;
    [ObservableProperty] private bool _countOver = true;
    [ObservableProperty] private bool _flash = true;
    [ObservableProperty] private bool _fullScreen = true;
    [ObservableProperty] private MinisterRow? _selectedMinister;
    [ObservableProperty] private bool _listDirty;

    [ObservableProperty] private string _timeText = "30:00";
    [ObservableProperty] private string _stateText = "Ready";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _hasTimer;
    [ObservableProperty] private MinisterColour _colour;
    [ObservableProperty] private ScreenFrame? _previewFrame;

    public bool BarLook { get => !FullScreen; set => FullScreen = !value; }
    public string MinutesText => $"{Minutes} min";
    public string RestartText => $"Restart from {CountdownShow.Format(Minutes * 60)}";
    public string GreenRule => $"Green from the start until {YellowAt}:00 left";
    public string YellowRule => $"Yellow from {YellowAt}:00 left";
    public string RedRule => $"Red from {RedAt}:00 left and over time";
    public IBrush TimeBrush => MinisterRenderer.BrushFor(Colour);
    public double GreenShare => Math.Max(1, Minutes - YellowAt);
    public double YellowShare => Math.Max(0.01, Math.Min(YellowAt, Minutes) - RedAt);
    public double RedShare => Math.Max(0.01, Math.Min(RedAt, Minutes));

    partial void OnFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(BarLook));
        Edited();
    }

    partial void OnMinutesChanged(int value)
    {
        if (value < 1) { Minutes = 1; return; }
        if (value > 360) { Minutes = 360; return; }
        OnPropertyChanged(nameof(MinutesText));
        OnPropertyChanged(nameof(RestartText));
        Shares();
        Edited();
    }

    partial void OnYellowAtChanged(int value)
    {
        if (value < 1) { YellowAt = 1; return; }
        if (value > 120) { YellowAt = 120; return; }
        if (RedAt > value) RedAt = value;
        OnPropertyChanged(nameof(GreenRule));
        OnPropertyChanged(nameof(YellowRule));
        Shares();
        Edited();
    }

    partial void OnRedAtChanged(int value)
    {
        if (value < 1) { RedAt = 1; return; }
        if (value > YellowAt) { RedAt = YellowAt; return; }
        OnPropertyChanged(nameof(RedRule));
        Shares();
        Edited();
    }

    partial void OnNameChanged(string value) => Edited();
    partial void OnPurposeChanged(string value) => Edited();
    partial void OnCountOverChanged(bool value) => Edited();
    partial void OnFlashChanged(bool value) => Edited();
    partial void OnColourChanged(MinisterColour value) => OnPropertyChanged(nameof(TimeBrush));

    private void Shares()
    {
        OnPropertyChanged(nameof(GreenShare));
        OnPropertyChanged(nameof(YellowShare));
        OnPropertyChanged(nameof(RedShare));
    }

    private void Edited()
    {
        if (_loading) return;
        UpdatePreview();
    }

    // ---------- Today's ministers ----------

    private void LoadMinisters()
    {
        Ministers.Clear();
        foreach (var m in Live.Ministers) Ministers.Add(new MinisterRow(m));
        ListDirty = false;
        MarkOnNow();
    }

    partial void OnSelectedMinisterChanged(MinisterRow? value)
    {
        if (value is null) return;
        _loading = true;
        Name = value.Name;
        Purpose = value.Purpose;
        Minutes = value.Minutes;
        _loading = false;
        UpdatePreview();
    }

    [RelayCommand]
    private void SelectMinister(MinisterRow row)
    {
        SelectedMinister = null;
        SelectedMinister = row;
    }

    [RelayCommand]
    private void AddMinister()
    {
        var row = new MinisterRow(new MinisterEntry("m-" + Guid.NewGuid().ToString("N")[..8],
            Name.Trim().Length == 0 ? "Minister" : Name.Trim(), Purpose.Trim(), Minutes));
        Ministers.Add(row);
        ListDirty = true;
        _ = SaveMinisters();
    }

    [RelayCommand]
    private void UpdateMinister(MinisterRow row)
    {
        row.Set(Name.Trim().Length == 0 ? "Minister" : Name.Trim(), Purpose.Trim(), Minutes);
        _ = SaveMinisters();
    }

    [RelayCommand]
    private void RemoveMinister(MinisterRow row)
    {
        Ministers.Remove(row);
        _ = SaveMinisters();
    }

    [RelayCommand]
    private void ClearMinisters()
    {
        Ministers.Clear();
        _ = SaveMinisters();
    }

    private async Task SaveMinisters()
    {
        ListDirty = false;
        if (Live.Client is not { } c || !MayEdit) return;
        await c.SendAsync(new SaveMinisters(Ministers.Select(r => r.Entry).ToList()));
    }

    private void MarkOnNow()
    {
        var live = _timer;
        foreach (var r in Ministers) r.OnNow = live is not null && r.Name == live.Name && r.Purpose == live.Purpose;
    }

    // ---------- Timer ----------

    private MinisterTimerShow Build(bool running, double remaining) =>
        new(Name.Trim().Length == 0 ? "Minister" : Name.Trim(), Purpose.Trim(), Minutes * 60, running, remaining,
            YellowAt * 60, RedAt * 60, CountOver, Flash, FullScreen ? MinisterLook.FullScreen : MinisterLook.Bar);

    private void Tick()
    {
        // What the Live Output PC has. Just after sending, until its answer arrives, keep what was sent.
        var live = Current?.Minister;
        if (live is null && (IsTrainee || DateTime.UtcNow < _sentUntil)) live = _timer;
        if (live is not null && Current?.Minister is not null) _sentUntil = DateTime.MinValue;
        var changed = !ReferenceEquals(live, _timer) && live != _timer;
        _timer = live;
        var now = DateTime.UtcNow;
        HasTimer = live is not null;
        if (live is null)
        {
            TimeText = CountdownShow.Format(Minutes * 60);
            Colour = MinisterColour.Green;
            Running = false;
            StateText = "Not on the Stage Monitor";
            MessageText = $"{Minutes} minutes, ready to start";
        }
        else
        {
            TimeText = live.Text(now);
            Colour = live.Colour(now);
            Running = live.Running;
            MessageText = live.Message(now, DateTime.Now, Live.StageLayout.Use24Hour);
            StateText = !live.Running ? "Paused" : Colour switch
            {
                MinisterColour.Yellow => "Yellow · time is getting low",
                MinisterColour.Red => "Red · last minutes",
                MinisterColour.Over => "Over time",
                _ => "Green · running",
            };
        }
        if (changed)
        {
            MarkOnNow();
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var show = _timer ?? Build(false, Minutes * 60);
        if (_timer is null || !Running)
        {
            // While setting up, the preview shows the look and colours being chosen.
            show = (_timer ?? show) with
            {
                Name = Name.Trim().Length == 0 ? "Minister" : Name.Trim(), Purpose = Purpose.Trim(),
                YellowAtSeconds = YellowAt * 60, RedAtSeconds = RedAt * 60, CountOver = CountOver, Flash = Flash,
                Look = FullScreen ? MinisterLook.FullScreen : MinisterLook.Bar,
                AllowedSeconds = _timer?.AllowedSeconds ?? Minutes * 60,
            };
        }
        var items = new List<LayerItem>();
        if (show.Look == MinisterLook.Bar)
            items.Add(new LayerItem(Layer.Lyrics, "preview-words", "Lyrics", SectionKind.Lyrics,
                new SlideContent("The words on screen show here", "", Next: "And what comes next"), true, 0));
        items.Add(new LayerItem(Layer.Overlays, "preview", "Minister Timer", Kind, new SlideContent(TitleText(show), Minister: show), true, 1));
        PreviewFrame = new ScreenFrame("preview", MasterMode.None, items);
    }

    private static string TitleText(MinisterTimerShow t) => MinisterRenderer.TitleOf(t) is { Length: > 0 } s ? s : "Minister";

    private async Task Push(MinisterTimerShow? timer)
    {
        _timer = timer;
        _sentUntil = timer is null ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(2);
        if (timer is null) await ClearAsync();
        else await SendAsync(new SlideContent($"{TitleText(timer)} {timer.Text(DateTime.UtcNow)}", TitleText(timer), "Minister Timer", Minister: timer));
        MarkOnNow();
        Tick();
        UpdatePreview();
    }

    [RelayCommand]
    private Task Start() => Push(Build(true, Minutes * 60));

    [RelayCommand]
    private Task UsePreset(int minutes)
    {
        Minutes = minutes;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void MoreMinutes() => Minutes += Minutes >= 60 ? 5 : 1;

    [RelayCommand]
    private void FewerMinutes() => Minutes -= Minutes > 60 ? 5 : 1;

    [RelayCommand] private void MoreYellow() => YellowAt++;
    [RelayCommand] private void LessYellow() => YellowAt--;
    [RelayCommand] private void MoreRed() => RedAt++;
    [RelayCommand] private void LessRed() => RedAt--;

    [RelayCommand]
    private async Task Pause()
    {
        if (_timer is not { Running: true } t) return;
        await Push(t with { Running = false, RemainingSeconds = t.Remaining(DateTime.UtcNow), EndsUtc = null });
    }

    [RelayCommand]
    private async Task Resume()
    {
        if (_timer is not { Running: false } t) return;
        await Push(t with { Running = true, EndsUtc = null });
    }

    [RelayCommand]
    private Task Restart() => Push(Build(true, Minutes * 60));

    [RelayCommand]
    private Task AddMinute() => Adjust(60);

    [RelayCommand]
    private Task RemoveMinute() => Adjust(-60);

    private async Task Adjust(int seconds)
    {
        if (_timer is not { } t) return;
        // The bar keeps measuring against the time first given, unless more time is added than that.
        var left = t.Remaining(DateTime.UtcNow) + seconds;
        var allowed = Math.Max(t.AllowedSeconds, (int)Math.Ceiling(left));
        if (t.Running && t.EndsUtc is { } e) await Push(t with { EndsUtc = e.AddSeconds(seconds), AllowedSeconds = allowed });
        else await Push(t with { RemainingSeconds = left, AllowedSeconds = allowed });
    }

    /// <summary>Sends the chosen look and colour rules to the running timer without restarting it.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (_timer is not { } t) return;
        await Push(t with
        {
            Name = Name.Trim().Length == 0 ? "Minister" : Name.Trim(), Purpose = Purpose.Trim(),
            YellowAtSeconds = YellowAt * 60, RedAtSeconds = RedAt * 60, CountOver = CountOver, Flash = Flash,
            Look = FullScreen ? MinisterLook.FullScreen : MinisterLook.Bar,
        });
    }

    [RelayCommand]
    private Task Stop() => Push(null);

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.Space:
                if (!HasTimer) _ = Start();
                else _ = Running ? Pause() : Resume();
                return true;
            case Key.Enter when !HasTimer: _ = Start(); return true;
            case Key.R: _ = Restart(); return true;
            case Key.Escape: _ = Stop(); return true;
            case Key.Add or Key.OemPlus: _ = AddMinute(); return true;
            case Key.Subtract or Key.OemMinus: _ = RemoveMinute(); return true;
        }
        return false;
    }

    protected override void OnLive(string? property)
    {
        if (property == nameof(Live.Library) && !ListDirty) LoadMinisters();
        if (property == nameof(Live.Snapshot)) Tick();
    }

    public override void OnLeave()
    {
        base.OnLeave();
        _tick.Stop();
    }
}

public sealed partial class MinisterRow(MinisterEntry entry) : ObservableObject
{
    [ObservableProperty] private MinisterEntry _entry = entry;
    [ObservableProperty] private bool _onNow;

    public string Name => Entry.Name;
    public string Purpose => Entry.Purpose;
    public int Minutes => Entry.Minutes;
    public string Detail => Purpose.Length == 0 ? $"{Minutes} min" : $"{Purpose} · {Minutes} min";

    public void Set(string name, string purpose, int minutes)
    {
        Entry = Entry with { Name = name, Purpose = purpose, Minutes = minutes };
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Purpose));
        OnPropertyChanged(nameof(Minutes));
        OnPropertyChanged(nameof(Detail));
    }
}
