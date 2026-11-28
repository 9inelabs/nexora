using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Speech;
using Nexora.Core.Bible;
using Nexora.Core.Speech;

namespace Nexora.App.ViewModels;

/// <summary>
/// Listening: NEXORA hears the pastor, writes down what is said, and offers the verses it hears as cards.
/// Exact references and spoken navigation can go to the screen by themselves after a short hold (a switch, off at first).
/// Suggestions by meaning are only ever cards.
/// </summary>
public sealed partial class ScriptureSectionViewModel
{
    private static Listener L => Listener.Instance;
    private DispatcherTimer? _holdTimer;
    private readonly List<string> _finalLines = [];
    private long _partialId = -1;
    private bool _listenHooked;

    [ObservableProperty] private bool _listenSetupOpen;
    [ObservableProperty] private string _transcriptDone = "";
    [ObservableProperty] private string _transcriptNow = "";
    [ObservableProperty] private HeardCard? _pending;
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private string _speedResult = "";
    [ObservableProperty] private string _reportPath = "";
    [ObservableProperty] private bool _modelBusy;
    [ObservableProperty] private string _modelMessage = "";

    public ObservableCollection<HeardCard> HeardCards { get; } = [];
    public ObservableCollection<MeaningCard> MeaningCards { get; } = [];
    public ObservableCollection<SpeechModelRow> ModelRows { get; } = [];
    public ObservableCollection<string> SoundInputs { get; } = [];
    public ObservableCollection<string> NdiSources { get; } = [];

    public ListenSettings ListenSettings => L.Settings;

    // ---------- What the Listen card shows ----------

    public bool Listening => L.Running;
    public bool ListenStarting => L.Starting;
    public bool ListenOff => !L.Running && !L.Starting;
    public bool ListenPaused => L.Paused;
    public string ListenStatus => L.Paused ? "Paused" : L.Status == "Listening" ? "" : L.Status;
    public string? ListenProblem => L.Problem;
    public bool HasListenProblem => L.Problem is { Length: > 0 };
    public string ListenSource => L.SourceStatus;
    public bool ListenSourceOk => L.SourceOk;
    public string ListenEngine => L.EngineName;
    public string MeaningStatus => L.MeaningStatus;
    public bool ListenSpeaking => L.Speaking;
    public bool HasModel => L.Model is not null;
    public bool HasHeard => HeardCards.Count > 0;
    public bool HasMeaning => MeaningCards.Count > 0;
    public bool HasPending => Pending is not null;
    public bool Replaying => L.Replaying;

    /// <summary>While listening the preview gets smaller, to make room for the words and the cards.</summary>
    public double PreviewMaxWidth => L.Running ? 620 : double.PositiveInfinity;

    /// <summary>The sound level, 0 to 1, for the little meter (-60 dB to 0 dB).</summary>
    public double ListenLevel => Math.Clamp((L.Level + 60) / 60, 0, 1);
    public double ListenLevelWidth => 120 * ListenLevel;

    public string ListenDelay => L.Running && L.DelaySeconds > 0
        ? $"{L.DelaySeconds:0.0} s to write down" + (L.Behind > 3 ? $" · {L.Behind:0} s behind" : "")
        : "";

    public string AutoLiveText => ListenSettings.AutoLive
        ? $"Auto: exact references go to the screen after {ListenSettings.HoldSeconds:0.#} s"
        : "Cards: tap one to load it, Send to put it on the screen";

    // ---------- Setup settings (bound in the panel; saved as they change) ----------

    public bool SourceNexAudio
    {
        get => ListenSettings.Source == Speech.ListenSource.NexAudio;
        set { if (value) SetSource(Speech.ListenSource.NexAudio); }
    }

    public bool SourceDevice
    {
        get => ListenSettings.Source == Speech.ListenSource.Device;
        set { if (value) SetSource(Speech.ListenSource.Device); }
    }

    public bool SourceNdi
    {
        get => ListenSettings.Source == Speech.ListenSource.Ndi;
        set { if (value) SetSource(Speech.ListenSource.Ndi); }
    }

    public string NexAudioFrom
    {
        get => ListenSettings.NexAudioFrom ?? "";
        set { ListenSettings.NexAudioFrom = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); SaveListen(restart: true); }
    }

    public string? SoundInput
    {
        get => ListenSettings.Device;
        set { if (value is null || value == ListenSettings.Device) return; ListenSettings.Device = value; SaveListen(restart: true); }
    }

    public string? NdiSource
    {
        get => ListenSettings.NdiSource;
        set { if (value is null || value == ListenSettings.NdiSource) return; ListenSettings.NdiSource = value; SaveListen(restart: true); }
    }

    public bool ReduceNoise
    {
        get => ListenSettings.ReduceNoise;
        set { ListenSettings.ReduceNoise = value; SaveListen(restart: true); }
    }

    public bool UseGraphicsCard
    {
        get => ListenSettings.UseGraphicsCard;
        set
        {
            ListenSettings.UseGraphicsCard = value;
            SaveListen(restart: false);
            ModelMessage = "Restart NEXORA for the graphics card setting to take effect.";
        }
    }

    public bool AutoLive
    {
        get => ListenSettings.AutoLive;
        set
        {
            ListenSettings.AutoLive = value;
            if (!value) CancelPending();
            SaveListen(restart: false);
        }
    }

    public double HoldSeconds
    {
        get => ListenSettings.HoldSeconds;
        set { ListenSettings.HoldSeconds = Math.Round(value, 1); SaveListen(restart: false); }
    }

    public bool FollowTranslation
    {
        get => ListenSettings.FollowTranslation;
        set { ListenSettings.FollowTranslation = value; SaveListen(restart: false); }
    }

    public bool SuggestByMeaning
    {
        get => ListenSettings.Suggest;
        set { ListenSettings.Suggest = value; if (!value) MeaningCards.Clear(); SaveListen(restart: false); OnPropertyChanged(nameof(HasMeaning)); }
    }

    public double SuggestLevel
    {
        get => ListenSettings.SuggestLevel;
        set { ListenSettings.SuggestLevel = Math.Round(value, 2); SaveListen(restart: false); }
    }

    public string SuggestLevelText => ListenSettings.SuggestLevel switch
    {
        < 0.45 => "Many suggestions",
        < 0.7 => "Balanced",
        _ => "Only very sure ones",
    };

    public string CustomWords
    {
        get => string.Join(", ", ListenSettings.CustomWords);
        set
        {
            ListenSettings.CustomWords = value.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            SaveListen(restart: false);
        }
    }

    private void SetSource(Speech.ListenSource s)
    {
        ListenSettings.Source = s;
        SaveListen(restart: true);
        OnPropertyChanged(nameof(SourceNexAudio));
        OnPropertyChanged(nameof(SourceDevice));
        OnPropertyChanged(nameof(SourceNdi));
        if (s == Speech.ListenSource.Device && SoundInputs.Count == 0) RefreshInputs();
        if (s == Speech.ListenSource.Ndi && NdiSources.Count == 0) RefreshInputs();
    }

    private void SaveListen(bool restart)
    {
        ListenSettings.Save();
        OnPropertyChanged(nameof(AutoLiveText));
        OnPropertyChanged(nameof(SuggestLevelText));
        OnPropertyChanged(nameof(HoldSeconds));
        // A new sound source takes effect at once if listening.
        if (restart && L.Running && !L.Replaying) _ = L.StartAsync();
    }

    // ---------- Hooking up ----------

    /// <summary>Called from the constructor: listening carries on while the operator is in another section.</summary>
    private void HookListen()
    {
        if (_listenHooked) return;
        _listenHooked = true;
        L.Changed += OnListenChanged;
        L.Transcript += OnTranscript;
        L.Heard += OnHeard;
        L.Suggestions += OnSuggestions;
        L.ReportReady += OnReportReady;
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        _holdTimer.Tick += (_, _) => HoldTick();
        RefreshModels();
    }

    private void UnhookListen()
    {
        if (!_listenHooked) return;
        _listenHooked = false;
        L.Changed -= OnListenChanged;
        L.Transcript -= OnTranscript;
        L.Heard -= OnHeard;
        L.Suggestions -= OnSuggestions;
        L.ReportReady -= OnReportReady;
        _holdTimer?.Stop();
    }

    private static readonly string[] ListenProps =
    [
        nameof(Listening), nameof(ListenStarting), nameof(ListenOff), nameof(ListenPaused), nameof(ListenStatus), nameof(ListenProblem),
        nameof(HasListenProblem), nameof(ListenSource), nameof(ListenSourceOk), nameof(ListenEngine), nameof(MeaningStatus),
        nameof(ListenSpeaking), nameof(ListenLevel), nameof(ListenLevelWidth), nameof(ListenDelay), nameof(HasModel), nameof(Replaying), nameof(PreviewMaxWidth),
    ];

    private void OnListenChanged()
    {
        foreach (var p in ListenProps) OnPropertyChanged(p);
    }

    private void OnTranscript(TranscriptLine line)
    {
        if (line.Final)
        {
            _finalLines.Add(line.Text);
            while (_finalLines.Count > 4) _finalLines.RemoveAt(0);
            // Only the newest words: about two lines.
            var done = string.Join(" ", _finalLines);
            TranscriptDone = done.Length > 170 ? "…" + done[^170..][(done[^170..].IndexOf(' ') + 1)..] : done;
            if (line.Id == _partialId || _partialId < 0) TranscriptNow = "";
            _partialId = -1;
        }
        else
        {
            _partialId = line.Id;
            TranscriptNow = line.Text;
        }
    }

    private void OnReportReady(string path)
    {
        ReportPath = path;
        Live.RaiseToast("The recording has been scanned. The report is in the listening setup.");
    }

    // ---------- Start, stop, pause ----------

    [RelayCommand]
    private async Task StartListening()
    {
        if (!HasModel)
        {
            OpenListenSetup();
            return;
        }
        ClearHeard();
        await L.StartAsync();
    }

    [RelayCommand]
    private void StopListening()
    {
        CancelPending();
        L.Stop();
    }

    [RelayCommand]
    private void TogglePause()
    {
        L.Paused = !L.Paused;
        if (L.Paused) CancelPending();
    }

    [RelayCommand]
    private void ClearHeard()
    {
        CancelPending();
        HeardCards.Clear();
        MeaningCards.Clear();
        _finalLines.Clear();
        TranscriptDone = "";
        TranscriptNow = "";
        OnPropertyChanged(nameof(HasHeard));
        OnPropertyChanged(nameof(HasMeaning));
    }

    [RelayCommand]
    private void OpenListenSetup()
    {
        RefreshModels();
        RefreshInputs();
        ListenSetupOpen = true;
    }

    [RelayCommand]
    private void CloseListenSetup() => ListenSetupOpen = false;

    // ---------- What was heard ----------

    private void OnHeard(Heard h, long sentence)
    {
        if (L.Paused) return;
        if (h.Kind == HeardKind.Translation)
        {
            if (!ListenSettings.FollowTranslation || S.Bibles.Get(h.Translation) is null || h.Translation == Translation) return;
        }
        var card = new HeardCard(h, sentence);
        UpdateTarget(card);
        if (card.Kind != HeardKind.Translation && card.Target is null) return; // "next verse" with nothing open

        // The same thing heard again (Whisper's partial and final lines) is one card.
        var same = HeardCards.FirstOrDefault(c => c.Sentence == sentence && c.Kind == card.Kind && c.TargetText == card.TargetText);
        if (same is not null) return;

        HeardCards.Insert(0, card);
        while (HeardCards.Count > 5) HeardCards.RemoveAt(HeardCards.Count - 1);
        OnPropertyChanged(nameof(HasHeard));

        if (ListenSettings.AutoLive && card.MayGoLiveAlone && !L.Scanning) StartHold(card);
    }

    private void OnSuggestions(IReadOnlyList<MeaningMatch> found, long sentence)
    {
        if (L.Paused || !ListenSettings.Suggest) return;
        foreach (var m in found.Reverse())
        {
            var key = m.Reference.ToString();
            var old = MeaningCards.FirstOrDefault(c => c.Reference == key);
            if (old is not null) MeaningCards.Remove(old);
            // A verse already offered as a heard reference needs no second card.
            if (HeardCards.Any(c => c.TargetText == key)) continue;
            MeaningCards.Insert(0, new MeaningCard(m));
        }
        while (MeaningCards.Count > 3) MeaningCards.RemoveAt(MeaningCards.Count - 1);
        OnPropertyChanged(nameof(HasMeaning));
    }

    /// <summary>"Next verse" and "verse 13" mean something different once another verse is open, so cards follow.</summary>
    private void RefreshHeardTargets()
    {
        foreach (var c in HeardCards) if (c.Relative) UpdateTarget(c);
    }

    private void UpdateTarget(HeardCard c)
    {
        if (c.Kind == HeardKind.Translation)
        {
            c.Target = null;
            c.TargetText = S.Bibles.Get(c.Heard.Translation)?.Name ?? c.Heard.Translation ?? "";
            return;
        }
        var tr = Current;
        c.Target = SpokenReferences.Resolve(c.Heard, _reference, tr is null ? null : tr.VersesIn);
        c.TargetText = c.Target?.ToString() ?? "Nothing open to move from";
    }

    /// <summary>Tap a card: load it in the preview. The operator sends it with Enter or Send.</summary>
    [RelayCommand]
    private Task UseHeard(HeardCard card) => Apply(card, send: false);

    /// <summary>The card's Send button: load it and put it on the screen at once.</summary>
    [RelayCommand]
    private Task SendHeard(HeardCard card) => Apply(card, send: true);

    [RelayCommand]
    private void DismissHeard(HeardCard card)
    {
        if (Pending == card) CancelPending();
        HeardCards.Remove(card);
        OnPropertyChanged(nameof(HasHeard));
    }

    private async Task Apply(HeardCard card, bool send)
    {
        if (Pending == card) CancelPending();
        if (card.Kind == HeardKind.Translation)
        {
            if (Translations.FirstOrDefault(t => string.Equals(t.Abbrev, card.Heard.Translation, StringComparison.OrdinalIgnoreCase)) is { } chip)
                await PickTranslation(chip);
        }
        else
        {
            UpdateTarget(card);
            if (card.Target is not { } r) return;
            SearchMode = false;
            SetReference(r, updateText: true);
            if (send) await Show();
        }
        card.Used = true;
    }

    [RelayCommand]
    private void UseMeaning(MeaningCard card)
    {
        SearchMode = false;
        SetReference(card.Match.Reference, updateText: true);
        card.Used = true;
    }

    [RelayCommand]
    private async Task SendMeaning(MeaningCard card)
    {
        UseMeaning(card);
        await Show();
    }

    // ---------- Auto-go-live hold ----------

    private DateTime _holdUntil;

    private void StartHold(HeardCard card)
    {
        if (Pending is { } old) old.IsPending = false;
        Pending = card;
        card.IsPending = true;
        _holdUntil = DateTime.UtcNow.AddSeconds(ListenSettings.HoldSeconds);
        HoldTick();
        _holdTimer?.Start();
    }

    private void HoldTick()
    {
        if (Pending is not { } card)
        {
            _holdTimer?.Stop();
            return;
        }
        var left = (_holdUntil - DateTime.UtcNow).TotalSeconds;
        if (left > 0)
        {
            PendingText = $"{card.Title}: going to the screen in {left:0.0} s. Esc stops it, Enter sends now.";
            return;
        }
        _holdTimer?.Stop();
        _ = Apply(card, send: true);
    }

    private void CancelPending()
    {
        if (Pending is { } p) p.IsPending = false;
        Pending = null;
        PendingText = "";
        _holdTimer?.Stop();
    }

    partial void OnPendingChanged(HeardCard? value) => OnPropertyChanged(nameof(HasPending));

    [RelayCommand]
    private void StopPending() => CancelPending();

    /// <summary>Esc and Enter act on a card about to go live before anything else.</summary>
    private bool HandleListenKey(Avalonia.Input.Key key)
    {
        if (Pending is not { } p) return false;
        if (key == Avalonia.Input.Key.Escape)
        {
            CancelPending();
            return true;
        }
        if (key == Avalonia.Input.Key.Enter)
        {
            _ = Apply(p, send: true);
            return true;
        }
        return false;
    }

    // ---------- Speech models ----------

    private void RefreshModels()
    {
        var chosen = L.Model;
        if (ModelRows.Count == 0)
            foreach (var m in SpeechModels.All) ModelRows.Add(new SpeechModelRow(m));
        var suggest = SpeechModels.Suggest(graphicsCard: false);
        foreach (var r in ModelRows)
        {
            r.Here = r.Model.OnThisPc;
            r.Chosen = chosen == r.Model;
            r.Suggested = r.Model == suggest;
        }
        OnPropertyChanged(nameof(HasModel));
        _ = CheckLiveOutputModels();
    }

    private async Task CheckLiveOutputModels()
    {
        if (S.IsLiveOutputPc) return;
        var there = await SpeechModels.OnLiveOutputAsync();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var r in ModelRows) r.OnLiveOutput = there.Contains(r.Model) && !r.Here;
        });
    }

    [RelayCommand]
    private void ChooseModel(SpeechModelRow row)
    {
        if (!row.Here) return;
        ListenSettings.Model = row.Model.Id;
        ListenSettings.Save();
        var was = L.Running && !L.Replaying;
        L.Unload();
        RefreshModels();
        ModelMessage = $"Listening with {row.Model.Name} ({row.Model.Id}).";
        if (was) _ = L.StartAsync();
    }

    private CancellationTokenSource? _download;

    [RelayCommand]
    private async Task DownloadModel(SpeechModelRow row)
    {
        if (ModelBusy) return;
        ModelBusy = true;
        row.Busy = true;
        _download = new CancellationTokenSource();
        try
        {
            // The Live Output PC first: no internet needed, and it is quicker on the church network.
            if (row.OnLiveOutput)
            {
                ModelMessage = $"Copying {row.Model.Name} from the Live Output PC…";
                if (await SpeechModels.FromLiveOutputAsync(row.Model, _download.Token))
                {
                    Done(row);
                    return;
                }
            }
            ModelMessage = $"Downloading {row.Model.Name} ({row.Model.Size}) from the internet, once…";
            var progress = new Progress<double>(p => row.Progress = p);
            await Task.Run(() => SpeechModels.DownloadAsync(row.Model, progress, _download.Token));
            Done(row);
        }
        catch (OperationCanceledException)
        {
            ModelMessage = "Download stopped.";
        }
        catch (Exception ex)
        {
            ModelMessage = ex.Message.Contains("No such host") || ex is System.Net.Http.HttpRequestException
                ? "This PC cannot reach the internet. Download the model on the Live Output PC, then copy it here, or import the file."
                : ex.Message;
        }
        finally
        {
            row.Busy = false;
            row.Progress = 0;
            ModelBusy = false;
            _download = null;
        }
    }

    private void Done(SpeechModelRow row)
    {
        if (L.Model is null || ListenSettings.Model is null)
        {
            ListenSettings.Model = row.Model.Id;
            ListenSettings.Save();
        }
        ModelMessage = $"{row.Model.Name} is ready on this PC.";
        RefreshModels();
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void RemoveModel(SpeechModelRow row)
    {
        if (row.Chosen) L.Unload();
        SpeechModels.Remove(row.Model);
        if (ListenSettings.Model == row.Model.Id)
        {
            ListenSettings.Model = null;
            ListenSettings.Save();
        }
        ModelMessage = $"Removed {row.Model.Name} from this PC.";
        RefreshModels();
    }

    /// <summary>A model file the operator picked (from a USB stick).</summary>
    public void ImportModel(string path)
    {
        try
        {
            var m = SpeechModels.Import(path);
            ModelMessage = $"{m.Name} ({m.Id}) is ready on this PC.";
            if (L.Model is null || ListenSettings.Model is null)
            {
                ListenSettings.Model = m.Id;
                ListenSettings.Save();
            }
        }
        catch (Exception ex) { ModelMessage = ex.Message; }
        RefreshModels();
    }

    [RelayCommand]
    private async Task SpeedTest()
    {
        if (L.Model is not { } m)
        {
            SpeedResult = "Get a speech model first.";
            return;
        }
        ModelBusy = true;
        SpeedResult = $"Testing {m.Name} on this PC (about 20 seconds)…";
        try { SpeedResult = await L.SpeedTestAsync(m); }
        catch (Exception ex) { SpeedResult = "The speed test failed: " + ex.Message; }
        finally { ModelBusy = false; }
    }

    // ---------- Sound inputs ----------

    [RelayCommand]
    private void RefreshInputs()
    {
        SoundInputs.Clear();
        NdiSources.Clear();
        try
        {
            if (OperatingSystem.IsWindows())
                foreach (var d in DirectShow.AudioDevices()) SoundInputs.Add(d);
        }
        catch (Exception) { }
        try
        {
            foreach (var n in Ndi.Sources()) NdiSources.Add(n);
        }
        catch (Exception) { }
        if (ListenSettings.Device is { } dev && !SoundInputs.Contains(dev)) SoundInputs.Add(dev);
        if (ListenSettings.NdiSource is { } src && !NdiSources.Contains(src)) NdiSources.Add(src);
        OnPropertyChanged(nameof(SoundInput));
        OnPropertyChanged(nameof(NdiSource));
    }

    // ---------- Trying it with a recording ----------

    /// <summary>Plays a recorded sermon into the listener at normal speed, as if live (cards and auto-live work).</summary>
    public async Task ReplayAsync(string path)
    {
        if (!HasModel) { ModelMessage = "Get a speech model first."; return; }
        ClearHeard();
        ListenSetupOpen = false;
        await L.StartAsync(path);
    }

    /// <summary>Reads a whole recording as fast as this PC can and writes a report of everything heard.</summary>
    public async Task ScanAsync(string path)
    {
        if (!HasModel) { ModelMessage = "Get a speech model first."; return; }
        ClearHeard();
        ReportPath = "";
        CancelPending();
        // A scan never sends anything to the screen (see OnHeard).
        await L.StartAsync(path, scan: true);
        ModelMessage = "Scanning. The report appears here when it is done; you can keep working.";
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath.Length == 0 || !File.Exists(ReportPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportPath) { UseShellExecute = true }); }
        catch (Exception ex) { Live.RaiseToast("The report could not be opened: " + ex.Message, true); }
    }
}

/// <summary>A reference or command NEXORA heard, as a card.</summary>
public sealed partial class HeardCard(Heard heard, long sentence) : ObservableObject
{
    public Heard Heard => heard;
    public long Sentence => sentence;
    public HeardKind Kind => heard.Kind;
    public string Title => heard.Label;
    public string Said => "“" + heard.Phrase + "”";
    public bool IsCommand => heard.Kind != HeardKind.Reference;
    public bool Relative => heard.Kind is not (HeardKind.Reference or HeardKind.Translation);
    public bool IsTranslation => heard.Kind == HeardKind.Translation;

    /// <summary>Exact references, spoken navigation and translations may go live by themselves; a book and chapter alone may not.</summary>
    public bool MayGoLiveAlone => heard.Kind switch
    {
        HeardKind.Reference => heard.Exact,
        _ => true,
    };

    public string Kindly => heard.Kind switch
    {
        HeardKind.Reference => heard.Exact ? "Heard" : "Heard (chapter)",
        HeardKind.Translation => "Translation",
        _ => "Said",
    };

    [ObservableProperty] private BibleReference? _target;
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _used;

    public bool ShowTarget => Relative || IsTranslation;
    partial void OnTargetTextChanged(string value) => OnPropertyChanged(nameof(ShowTarget));
}

/// <summary>A verse or story the pastor seems to be talking about (only ever a suggestion).</summary>
public sealed partial class MeaningCard(MeaningMatch match) : ObservableObject
{
    public MeaningMatch Match => match;
    public string Reference => match.Reference.ToString();
    public string Title => match.Title;
    public bool HasTitle => match.Title.Length > 0;
    public string Why => match.Why;
    public string Sure => $"{match.Score:P0}";
    [ObservableProperty] private bool _used;
}

/// <summary>A speech model in the setup list.</summary>
public sealed partial class SpeechModelRow(SpeechModel model) : ObservableObject
{
    public SpeechModel Model => model;
    public string Name => model.Name;
    public string Detail => $"{model.Id} · {model.Size}";
    public string Best => model.Best;

    [ObservableProperty] private bool _here;
    [ObservableProperty] private bool _chosen;
    [ObservableProperty] private bool _suggested;
    [ObservableProperty] private bool _onLiveOutput;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progress;

    public bool CanGet => !Here && !Busy;
    public bool CanChoose => Here && !Chosen;
    public string GetText => OnLiveOutput ? "Copy from Live Output" : "Download";
    public string State => Chosen ? "In use" : Here ? "On this PC" : OnLiveOutput ? "On the Live Output PC" : "Not on this PC";
    public double ProgressPercent => Progress * 100;

    partial void OnHereChanged(bool value) => Notify();
    partial void OnChosenChanged(bool value) => Notify();
    partial void OnOnLiveOutputChanged(bool value) => Notify();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressPercent));

    private void Notify()
    {
        OnPropertyChanged(nameof(CanGet));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(GetText));
        OnPropertyChanged(nameof(State));
    }
}
