using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Nexora.App.Services;
using Nexora.Core.Bible;
using Nexora.Core.Config;
using Nexora.Core.Protocol;
using Nexora.Core.Speech;

namespace Nexora.App.Speech;

public enum ListenSource
{
    /// <summary>The speech feed from NEX-Audio over the network.</summary>
    NexAudio,
    /// <summary>A sound input on this PC (USB sound card from the desk).</summary>
    Device,
    /// <summary>The sound of an NDI source.</summary>
    Ndi,
}

/// <summary>Listening settings for this PC (kept in local/listen.json).</summary>
public sealed class ListenSettings
{
    public ListenSource Source { get; set; } = ListenSource.NexAudio;
    /// <summary>Only take the speech feed from this NEX-Audio sender, or null for the first one heard.</summary>
    public string? NexAudioFrom { get; set; }
    public string? Device { get; set; }
    public string? NdiSource { get; set; }
    /// <summary>A noise reducer on a sound input or a recording (the NEX-Audio feed is already clean).</summary>
    public bool ReduceNoise { get; set; }
    public string? Model { get; set; }
    public bool UseGraphicsCard { get; set; } = true;
    /// <summary>Exact references and spoken navigation go to the screen by themselves after a short hold.</summary>
    public bool AutoLive { get; set; }
    public double HoldSeconds { get; set; } = 1.0;
    /// <summary>"In the Amplified" switches translation.</summary>
    public bool FollowTranslation { get; set; } = true;
    /// <summary>Suggest stories and verses by what the pastor is talking about.</summary>
    public bool Suggest { get; set; } = true;
    /// <summary>How sure a suggestion must be (0.3 to 0.95).</summary>
    public double SuggestLevel { get; set; } = 0.6;
    /// <summary>Words Whisper should expect: names, places, church phrases.</summary>
    public List<string> CustomWords { get; set; } = [];

    private static string FilePath => Path.Combine(AppPaths.LocalDir, "listen.json");

    public static ListenSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ListenSettings>(File.ReadAllText(FilePath), Wire.Options) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public void Save()
    {
        try
        {
            HoldSeconds = Math.Clamp(HoldSeconds, 0.3, 5);
            SuggestLevel = Math.Clamp(SuggestLevel, 0.3, 0.95);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Wire.Options));
        }
        catch (Exception ex) { AppLog.Write("Listen settings: " + ex.Message); }
    }
}

/// <summary>
/// Scripture listening on this PC: the sound source, Whisper, the reference finder and the meaning search,
/// started and stopped from the Scripture section. Events arrive on the UI thread.
/// </summary>
public sealed class Listener
{
    public static Listener Instance { get; } = new();

    private ListenSession? _session;
    private ISoundSource? _source;
    private WhisperTranscriber? _whisper;
    private MeaningSearch? _meaning;
    private Task? _meaningBuild;
    private readonly DispatcherTimer _timer;
    private ReportWriter? _report;

    private Listener()
    {
        Settings = ListenSettings.Load();
        // (The constructor with a callback would start the timer at once; it starts when listening starts.)
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Tick();
    }

    public ListenSettings Settings { get; }

    public bool Running => _session is not null;
    public bool Starting { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? Problem { get; private set; }
    public string SourceStatus => _source?.Status ?? "";
    public bool SourceOk => _source?.Ok == true;
    public string EngineName => _whisper?.Name ?? "";
    public double Level { get; private set; } = -90;
    public bool Speaking => _session?.Speaking == true;
    public double DelaySeconds => (_session?.AverageDecodeMs ?? 0) / 1000.0;
    public double Behind => _session?.Behind ?? 0;
    public bool Replaying => _source is FileSource;
    /// <summary>A recording is being read for a report: nothing goes to the screen.</summary>
    public bool Scanning => _report is not null;
    public string MeaningStatus { get; private set; } = "";

    public bool Paused
    {
        get => _session?.Paused == true;
        set
        {
            if (_session is not null) _session.Paused = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Status, level or source changed (on the UI thread, about 8 times a second while listening).</summary>
    public event Action? Changed;
    public event Action<TranscriptLine>? Transcript;
    public event Action<Heard, long>? Heard;
    public event Action<IReadOnlyList<MeaningMatch>, long>? Suggestions;

    /// <summary>The model this PC listens with: the one chosen, or the best one on the PC.</summary>
    public SpeechModel? Model => SpeechModels.Get(Settings.Model) is { OnThisPc: true } m ? m : SpeechModels.Installed.FirstOrDefault();

    // ---------- Start and stop ----------

    /// <summary>Starts listening to the live source, or to a recording (<paramref name="replay"/>).</summary>
    public async Task StartAsync(string? replay = null, bool scan = false)
    {
        if (Starting) return;
        Stop();
        Problem = null;
        if (Model is not { } model)
        {
            Problem = "No speech model on this PC yet. Open Set up and download one (once), or get it from the Live Output PC.";
            Status = "Not set up";
            Changed?.Invoke();
            return;
        }
        Starting = true;
        Status = "Loading the speech model…";
        Changed?.Invoke();
        try
        {
            if (_whisper is null || _whisper.Model != model)
            {
                _whisper?.Dispose();
                _whisper = null;
                _whisper = await Task.Run(() => WhisperTranscriber.Open(model, Settings.UseGraphicsCard));
            }
            EnsureMeaning();
            var options = new ListenOptions
            {
                CustomWords = Settings.CustomWords,
                Scan = scan,
                // A slower PC writes down less often while the pastor speaks, so it never falls behind.
                PartialEvery = _whisper.UsesGraphicsCard ? TimeSpan.FromSeconds(0.8) : model.Id == "base.en" ? TimeSpan.FromSeconds(1.2) : TimeSpan.FromSeconds(1.8),
            };
            var session = new ListenSession(new Shared(_whisper), options);
            session.Transcript += l =>
            {
                _report?.Line(l);
                Dispatcher.UIThread.Post(() => Transcript?.Invoke(l));
            };
            session.Heard += (h, id) =>
            {
                _report?.Heard(h, session.Position);
                Dispatcher.UIThread.Post(() => Heard?.Invoke(h, id));
            };
            session.Sentence += (id, text) => FindMeaning(id, text, session);
            session.Problem += p => Dispatcher.UIThread.Post(() =>
            {
                Problem = "Speech recognition: " + p;
                Changed?.Invoke();
            });
            _session = session;

            ISoundSource source = replay is not null
                ? new FileSource(replay, scan, Settings.ReduceNoise)
                : Settings.Source switch
                {
                    ListenSource.Device when Settings.Device is { Length: > 0 } d => new DeviceSource(d, Settings.ReduceNoise),
                    ListenSource.Ndi when Settings.NdiSource is { Length: > 0 } n => new NdiSoundSource(n),
                    _ => new NexAudioSource(Settings.NexAudioFrom),
                };
            if (replay is not null && scan) _report = new ReportWriter(replay);
            _source = source;
            source.Start(samples => session.Push(samples));
            Status = replay is not null ? (scan ? "Scanning the recording" : "Replaying a recording") : "Listening";
            _timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Listening could not start: " + ex);
            Problem = "Listening could not start: " + ex.Message;
            Status = "Off";
            StopInner();
        }
        finally
        {
            Starting = false;
            Changed?.Invoke();
        }
    }

    public void Stop()
    {
        StopInner();
        Status = "Off";
        Changed?.Invoke();
    }

    private void StopInner()
    {
        _timer.Stop();
        _source?.Dispose();
        _source = null;
        var s = _session;
        _session = null;
        if (s is not null) Task.Run(s.Dispose);
        if (_report is { } r)
        {
            _report = null;
            r.Close();
        }
        Level = -90;
    }

    /// <summary>Lets go of the speech model (when settings change), so the next start loads it again.</summary>
    public void Unload()
    {
        Stop();
        _whisper?.Dispose();
        _whisper = null;
    }

    private void Tick()
    {
        if (_session is not { } s) return;
        Level = s.TakeLevel();
        if (_source is FileSource { Finished: true } f && s.Behind < 0.1 && !s.Speaking)
        {
            // The recording is done: finish the last sentence, then stop.
            var report = _report?.Path;
            Task.Run(() => s.Flush(TimeSpan.FromSeconds(60))).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                Stop();
                Status = "Recording finished";
                if (report is not null) ReportReady?.Invoke(report);
                Changed?.Invoke();
            }));
            _source = null;
            f.Dispose();
        }
        Changed?.Invoke();
    }

    /// <summary>A scan of a recording finished; the report is at this path.</summary>
    public event Action<string>? ReportReady;

    // ---------- Meaning search ----------

    private void EnsureMeaning()
    {
        if (_meaningBuild is not null) return;
        _meaningBuild = Task.Run(BuildMeaning);
    }

    private void BuildMeaning()
    {
        try
        {
            MeaningStatus = "Preparing the story and verse search…";
            AppServices.Instance.LoadBiblesAsync().Wait();
            var lib = AppServices.Instance.Bibles;
            var words = new[] { "KJV", "NKJV", "NLT", "BBE", "ESV" };
            var forWords = lib.All.Where(t => words.Contains(t.Abbrev, StringComparer.OrdinalIgnoreCase)).ToList();
            if (forWords.Count == 0) forWords = lib.All.Take(3).ToList();
            var index = VerseIndex.Build(forWords.SelectMany(t => t.AllVerses().Select(v => ((int)(v.Id / 1_000_000), (int)(v.Id / 1000 % 1000), (int)(v.Id % 1000), v.Text))));
            var quoted = new[] { "KJV", "NKJV", "NLT" };
            var forQuotes = lib.All.Where(t => quoted.Contains(t.Abbrev, StringComparer.OrdinalIgnoreCase)).ToList();
            var quotes = QuoteIndex.Build((forQuotes.Count > 0 ? forQuotes : forWords).SelectMany(t => t.AllVerses()));
            var modern = lib.Get("NLT") ?? lib.Get("NKJV") ?? forWords.First();
            string? Text(long id) => modern.Verse(id);
            _meaning = new MeaningSearch(index, Text, quotes: quotes);
            MeaningStatus = "Story and verse search ready.";
            Dispatcher.UIThread.Post(() => Changed?.Invoke());

            // The meaning model: verse vectors are worked out once on this PC (a few minutes), then kept.
            using var model = MeaningModel.TryOpen();
            if (model is null) return;
            var file = Path.Combine(AppPaths.SpeechDir, $"meaning-{modern.Abbrev}.vectors");
            var stamp = MeaningModel.Stamp + "|" + modern.Abbrev + "|" + modern.Info.Hash;
            var vectors = VerseVectors.Load(file, stamp);
            if (vectors is null)
            {
                var all = modern.AllVerses().Where(v => v.Text.Length > 0).OrderBy(v => v.Id).ToList();
                vectors = VerseVectors.Build(model, all, p =>
                {
                    MeaningStatus = $"Learning the verses' meanings on this PC (first time only): {p:P0}";
                    Dispatcher.UIThread.Post(() => Changed?.Invoke());
                });
                vectors.Save(file, stamp);
            }
            var live = MeaningModel.TryOpen();
            _meaning = new MeaningSearch(index, Text, live, vectors, quotes);
            MeaningStatus = "Story, verse and meaning search ready.";
        }
        catch (Exception ex)
        {
            AppLog.Write("Meaning search: " + ex);
            MeaningStatus = _meaning is null ? "Story and verse search could not start: " + ex.Message : "Meaning model not used: " + ex.Message;
        }
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private void FindMeaning(long id, string recent, ListenSession session)
    {
        if (!Settings.Suggest || _meaning is not { } m) return;
        // Only the last 40 words or so: what is being talked about now.
        var words = recent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words.Skip(Math.Max(0, words.Length - 40)));
        try
        {
            var found = m.Search(text, 3, Settings.SuggestLevel);
            if (found.Count == 0) return;
            _report?.Suggested(found, session.Position);
            Dispatcher.UIThread.Post(() => Suggestions?.Invoke(found, id));
        }
        catch (Exception ex) { AppLog.Write("Meaning search: " + ex.Message); }
    }

    // ---------- Speed test ----------

    public async Task<string> SpeedTestAsync(SpeechModel model)
    {
        var wasRunning = Running;
        Stop();
        try
        {
            using var w = await Task.Run(() => WhisperTranscriber.Open(model, Settings.UseGraphicsCard));
            var seconds = await w.SpeedTestAsync(CancellationToken.None);
            var verdict = seconds < 1.5 ? "Comfortable: references come up in about 1 to 2 seconds."
                : seconds < 3 ? "Fine: references come up in about 2 to 4 seconds."
                : seconds < 5 ? "Slow: references take 4 to 6 seconds. A smaller model would be quicker."
                : "Too slow for live use on this PC. Use the Fast model, or add a graphics card.";
            return $"{model.Name} ({model.Id}) on {(w.UsesGraphicsCard ? "the graphics card" : "the CPU")}: {seconds:0.0} s for 5 s of speech. {verdict}";
        }
        finally
        {
            _whisper?.Dispose();
            _whisper = null;
            if (wasRunning) _ = StartAsync();
        }
    }

    /// <summary>The live transcriber shared with sessions without letting a session dispose it.</summary>
    private sealed class Shared(ITranscriber inner) : ITranscriber
    {
        public string Name => inner.Name;
        public Task<string> TranscribeAsync(float[] audio, string prompt, CancellationToken ct) => inner.TranscribeAsync(audio, prompt, ct);
        public void Dispose() { }
    }

    /// <summary>A scan of a recording: everything heard, with times, saved as a text file next to the recording's name.</summary>
    private sealed class ReportWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly object _gate = new();

        public ReportWriter(string recording)
        {
            var dir = AppPaths.Ensure(System.IO.Path.Combine(AppPaths.SpeechDir, "reports"));
            Path = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(recording)} {DateTime.Now:yyyy-MM-dd HHmm}.txt");
            _sb.AppendLine($"NEXORA listening report: {System.IO.Path.GetFileName(recording)}");
            _sb.AppendLine($"Made {DateTime.Now:d MMMM yyyy, h:mm tt}");
            _sb.AppendLine();
            _sb.AppendLine("Lines marked >> are references or commands NEXORA heard; ?? are suggestions. Check them against");
            _sb.AppendLine("what the pastor really said to see how well listening works with this voice and this room.");
            _sb.AppendLine();
        }

        public string Path { get; }

        private static string T(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

        public void Line(TranscriptLine l)
        {
            if (!l.Final) return;
            lock (_gate) _sb.AppendLine($"[{T(l.At)}] {l.Text}");
        }

        public void Heard(Heard h, TimeSpan at)
        {
            lock (_gate) _sb.AppendLine($"   >> {h.Label}  ({h.Kind}, {h.Confidence:P0} sure)");
        }

        public void Suggested(IReadOnlyList<MeaningMatch> found, TimeSpan at)
        {
            lock (_gate)
                foreach (var f in found) _sb.AppendLine($"   ?? {f.Reference}  {f.Title}  ({f.Score:P0}, {f.Why})");
        }

        public void Close()
        {
            try
            {
                lock (_gate) File.WriteAllText(Path, _sb.ToString());
            }
            catch (Exception ex) { AppLog.Write("Listening report: " + ex.Message); }
        }
    }
}
