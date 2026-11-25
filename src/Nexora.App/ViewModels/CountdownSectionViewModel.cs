using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// Countdown: timers that run on the Live Output PC's clock (so they stay right if this PC is switched
/// off), the pastor's sermon points for the Stage Monitor, and how the Stage Monitor is arranged.
/// </summary>
public sealed partial class CountdownSectionViewModel : SectionBase, IKeyHandler
{
    private readonly DispatcherTimer _tick;
    private CountdownShow? _timer;
    private bool _loading;

    public CountdownSectionViewModel() : base(SectionKind.Countdown)
    {
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _tick.Start();
        LoadLibrary();
        // Pick up a timer already running (this PC restarted, or another Countdown PC started it).
        if (Current is { } c)
        {
            _timer = c.Countdown;
            if (c.Points is { } p) { PointsText = string.Join("\n", p.Points); CurrentPoint = p.Current; }
        }
        Tick();
    }

    public ObservableCollection<CountdownPreset> Presets { get; } = [];
    public ObservableCollection<PointRow> Points { get; } = [];

    [ObservableProperty] private string _label = "Countdown";
    [ObservableProperty] private string _minutes = "5";
    [ObservableProperty] private string _toTime = "";
    [ObservableProperty] private bool _modeToTime;
    [ObservableProperty] private CountdownLook _look = CountdownLook.Badge;
    [ObservableProperty] private ZeroAction _atZero = ZeroAction.None;
    [ObservableProperty] private double _warnAt = 60;
    [ObservableProperty] private bool _overtime = true;

    [ObservableProperty] private string _timeText = "–:––";
    [ObservableProperty] private string _stateText = "No timer";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _hasTimer;
    [ObservableProperty] private CountdownState _state;
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private ScreenFrame? _stagePreview;

    [ObservableProperty] private string _pointsText = "";
    [ObservableProperty] private int _currentPoint = -1;
    [ObservableProperty] private bool _pointsOnStage;

    // Stage Monitor layout
    [ObservableProperty] private bool _stageClock = true;
    [ObservableProperty] private bool _stageCountdown = true;
    [ObservableProperty] private bool _stageNext = true;
    [ObservableProperty] private bool _stagePoints = true;
    [ObservableProperty] private bool _stageBooth = true;
    [ObservableProperty] private bool _stage24h;
    [ObservableProperty] private double _stageScale = 1;
    [ObservableProperty] private bool _stageDirty;

    public static IReadOnlyList<CountdownLook> Looks { get; } = Enum.GetValues<CountdownLook>();
    public static IReadOnlyList<ZeroAction> ZeroActions { get; } = Enum.GetValues<ZeroAction>();
    public bool ModeDuration { get => !ModeToTime; set => ModeToTime = !value; }
    public StageLayout Layout => new(StageClock, StageCountdown, StageNext, StagePoints, StageBooth, StageScale, Stage24h);
    public bool HasPoints => Points.Count > 0;
    public Avalonia.Media.IBrush TimeBrush => State switch
    {
        CountdownState.Warning => Controls.OverlayRenderer.Warn,
        CountdownState.Zero or CountdownState.Overtime => Controls.OverlayRenderer.Red,
        _ => Avalonia.Media.Brushes.WhiteSmoke,
    };

    partial void OnModeToTimeChanged(bool value) => OnPropertyChanged(nameof(ModeDuration));
    partial void OnStateChanged(CountdownState value) => OnPropertyChanged(nameof(TimeBrush));

    private void LoadLibrary()
    {
        _loading = true;
        Presets.Clear();
        foreach (var p in Live.CountdownPresets) Presets.Add(p);
        var l = Live.StageLayout;
        StageClock = l.Clock;
        StageCountdown = l.Countdown;
        StageNext = l.Next;
        StagePoints = l.SermonPoints;
        StageBooth = l.Booth;
        Stage24h = l.Use24Hour;
        StageScale = l.TextScale;
        if (PointsText.Length == 0 && Live.SermonPoints.Count > 0) PointsText = string.Join("\n", Live.SermonPoints);
        _loading = false;
        StageDirty = false;
        RebuildPoints();
    }

    // ---------- Timer ----------

    private void Tick()
    {
        // The timer as the Live Output PC has it (its end time is set by that PC's clock).
        var live = Current?.Countdown ?? _timer;
        _timer = live;
        var now = DateTime.UtcNow;
        HasTimer = live is not null;
        if (live is null)
        {
            TimeText = "–:––";
            StateText = "No timer";
            Running = false;
            State = CountdownState.Normal;
        }
        else
        {
            TimeText = live.Text(now);
            State = live.State(now);
            Running = live.Running;
            StateText = !live.Running ? "Paused" : State switch
            {
                CountdownState.Warning => "Nearly time",
                CountdownState.Zero => "Time",
                CountdownState.Overtime => "Over time",
                _ => live.Mode == CountdownMode.ToTime ? $"Until {live.ToTime}" : "Running",
            };
        }
    }

    private CountdownShow Build(bool running, double remaining, DateTime? ends = null) =>
        new(Label.Trim().Length == 0 ? "Countdown" : Label.Trim(), ModeToTime ? CountdownMode.ToTime : CountdownMode.Duration, running, remaining,
            ModeToTime ? ToTime.Trim() : null, (int)WarnAt, Overtime, Look, AtZero, ends);

    private static double ParseMinutes(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Contains(':'))
        {
            var p = text.Split(':');
            double total = 0;
            foreach (var part in p) total = total * 60 + (double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0);
            return p.Length == 3 ? total : total; // m:ss → seconds; h:mm:ss → seconds
        }
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m * 60 : 0;
    }

    private async Task Push(CountdownShow? timer)
    {
        _timer = timer;
        var pts = PointsOnStage && Points.Count > 0 ? new SermonPoints(Points.Select(p => p.Text).ToList(), CurrentPoint) : null;
        if (timer is null && pts is null)
        {
            await ClearAsync();
            Tick();
            return;
        }
        var text = timer is null ? "Sermon points" : $"{timer.Label} {timer.Text(DateTime.UtcNow)}";
        await SendAsync(new SlideContent(text, timer?.Label ?? "Sermon points", timer?.Label ?? "Points", Countdown: timer, Points: pts));
        Tick();
    }

    [RelayCommand]
    private async Task Start()
    {
        if (ModeToTime)
        {
            if (CountdownShow.NextLocal(ToTime, DateTime.Now) is null)
            {
                Live.RaiseToast("Type a time like 9:00, 10:30 or 6pm.", true);
                return;
            }
            await Push(Build(true, 0));
            return;
        }
        var secs = ParseMinutes(Minutes);
        if (secs <= 0)
        {
            Live.RaiseToast("Type how many minutes, for example 5 or 2:30.", true);
            return;
        }
        await Push(Build(true, secs));
    }

    [RelayCommand]
    private async Task UsePreset(CountdownPreset p)
    {
        Label = p.Label;
        ModeToTime = p.Mode == CountdownMode.ToTime;
        ToTime = p.ToTime ?? "";
        Minutes = p.Mode == CountdownMode.Duration ? CountdownShow.Format(p.Seconds) : Minutes;
        WarnAt = p.WarnAtSeconds;
        Overtime = p.CountUpOvertime;
        Look = p.AudienceLook;
        AtZero = p.AtZero;
        await Start();
    }

    [RelayCommand]
    private async Task Pause()
    {
        if (_timer is not { Running: true } t) return;
        var left = t.Remaining(DateTime.UtcNow);
        await Push(t with { Running = false, RemainingSeconds = left, EndsUtc = null, Mode = CountdownMode.Duration });
    }

    [RelayCommand]
    private async Task Resume()
    {
        if (_timer is not { Running: false } t) return;
        await Push(t with { Running = true, EndsUtc = null });
    }

    [RelayCommand]
    private Task AddMinute() => Adjust(60);

    [RelayCommand]
    private Task RemoveMinute() => Adjust(-60);

    private async Task Adjust(int seconds)
    {
        if (_timer is not { } t) return;
        if (t.Running && t.EndsUtc is { } e) await Push(t with { EndsUtc = e.AddSeconds(seconds), Mode = CountdownMode.Duration });
        else await Push(t with { RemainingSeconds = Math.Max(0, t.RemainingSeconds + seconds) });
    }

    [RelayCommand]
    private async Task ChangeLook()
    {
        if (_timer is { } t) await Push(t with { AudienceLook = Look, AtZero = AtZero, WarnAtSeconds = (int)WarnAt, CountUpOvertime = Overtime });
    }

    [RelayCommand]
    private Task Stop() => Push(null);

    [RelayCommand]
    private async Task SavePreset()
    {
        if (Live.Client is not { } c) return;
        var secs = (int)ParseMinutes(Minutes);
        var p = new CountdownPreset("c-" + Guid.NewGuid().ToString("N")[..8], Label.Trim().Length == 0 ? "Countdown" : Label.Trim(),
            ModeToTime ? CountdownMode.ToTime : CountdownMode.Duration, secs, ModeToTime ? ToTime.Trim() : null, (int)WarnAt, Overtime, Look, AtZero);
        await c.SendAsync(new SaveCountdownPresets(Live.CountdownPresets.Append(p).ToList()));
        Live.RaiseToast($"Saved the {p.Label} timer.");
    }

    [RelayCommand]
    private async Task DeletePreset(CountdownPreset p)
    {
        if (Live.Client is { } c) await c.SendAsync(new SaveCountdownPresets(Live.CountdownPresets.Where(x => x.Id != p.Id).ToList()));
    }

    // ---------- Sermon points ----------

    partial void OnPointsTextChanged(string value) => RebuildPoints();

    private void RebuildPoints()
    {
        Points.Clear();
        var n = 0;
        foreach (var line in PointsText.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(40))
            Points.Add(new PointRow(n++, line) { IsCurrent = n - 1 == CurrentPoint });
        OnPropertyChanged(nameof(HasPoints));
    }

    partial void OnCurrentPointChanged(int value)
    {
        foreach (var p in Points) p.IsCurrent = p.Index == value;
    }

    [RelayCommand]
    private async Task PickPoint(PointRow row)
    {
        CurrentPoint = row.Index;
        PointsOnStage = true;
        await Push(_timer);
    }

    [RelayCommand]
    private async Task NextPoint()
    {
        if (Points.Count == 0) return;
        CurrentPoint = Math.Min(Points.Count - 1, CurrentPoint + 1);
        PointsOnStage = true;
        await Push(_timer);
    }

    [RelayCommand]
    private async Task PreviousPoint()
    {
        if (Points.Count == 0) return;
        CurrentPoint = Math.Max(0, CurrentPoint - 1);
        PointsOnStage = true;
        await Push(_timer);
    }

    [RelayCommand]
    private async Task ShowPoints()
    {
        PointsOnStage = true;
        if (CurrentPoint < 0) CurrentPoint = 0;
        await Push(_timer);
    }

    [RelayCommand]
    private async Task HidePoints()
    {
        PointsOnStage = false;
        await Push(_timer);
    }

    // ---------- Stage Monitor layout ----------

    partial void OnStageClockChanged(bool value) => StageEdited();
    partial void OnStageCountdownChanged(bool value) => StageEdited();
    partial void OnStageNextChanged(bool value) => StageEdited();
    partial void OnStagePointsChanged(bool value) => StageEdited();
    partial void OnStageBoothChanged(bool value) => StageEdited();
    partial void OnStage24hChanged(bool value) => StageEdited();
    partial void OnStageScaleChanged(double value) => StageEdited();

    private void StageEdited()
    {
        if (_loading) return;
        StageDirty = true;
        OnPropertyChanged(nameof(Layout));
    }

    [RelayCommand]
    private async Task SaveStage()
    {
        if (Live.Client is not { } c) return;
        await c.SendAsync(new SaveStage(Layout, Points.Select(p => p.Text).ToList()));
        StageDirty = false;
        Live.RaiseToast("Stage Monitor layout and sermon points saved.");
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.Space when HasTimer:
                _ = Running ? Pause() : Resume();
                return true;
            case Key.Enter: _ = Start(); return true;
            case Key.Escape: _ = Stop(); return true;
            case Key.Down or Key.PageDown: _ = NextPoint(); return true;
            case Key.Up or Key.PageUp: _ = PreviousPoint(); return true;
            case Key.Add or Key.OemPlus: _ = AddMinute(); return true;
            case Key.Subtract or Key.OemMinus: _ = RemoveMinute(); return true;
        }
        return false;
    }

    protected override void OnLive(string? property)
    {
        if (property == nameof(Live.Library) && !StageDirty) LoadLibrary();
        if (property == nameof(Live.Snapshot)) Tick();
    }

    public override void OnLeave()
    {
        base.OnLeave();
        _tick.Stop();
    }
}

public sealed partial class PointRow(int index, string text) : ObservableObject
{
    public int Index => index;
    public string Number => (index + 1).ToString();
    public string Text => text;
    [ObservableProperty] private bool _isCurrent;
}
