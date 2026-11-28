using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Nexora.Core.Speech;

/// <summary>Speech to text: Whisper on the PC (or a stand-in for tests).</summary>
public interface ITranscriber : IDisposable
{
    /// <summary>A short name for the screen ("Whisper small.en · graphics card").</summary>
    string Name { get; }
    /// <summary>Writes down what is said in <paramref name="audio"/> (16 kHz mono). <paramref name="prompt"/> holds words to expect.</summary>
    Task<string> TranscribeAsync(float[] audio, string prompt, CancellationToken ct);
}

/// <summary>A line of the live transcript. The same <see cref="Id"/> is sent again as more words arrive, then once with <see cref="Final"/>.</summary>
public sealed record TranscriptLine(long Id, string Text, bool Final, TimeSpan At);

public sealed class ListenOptions
{
    /// <summary>Words Whisper should expect (book names, the pastor's name, church words).</summary>
    public string Prompt { get; set; } = DefaultPrompt;
    /// <summary>Extra words added by the church (names, places, phrases).</summary>
    public IReadOnlyList<string> CustomWords { get; set; } = [];
    /// <summary>Transcribe the sentence so far this often while the pastor is still speaking (0: only at pauses).</summary>
    public TimeSpan PartialEvery { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>A pause this long ends a sentence.</summary>
    public TimeSpan EndAfter { get; set; } = TimeSpan.FromMilliseconds(650);
    /// <summary>A sentence is cut at the quietest point once it runs this long.</summary>
    public TimeSpan MaxLength { get; set; } = TimeSpan.FromSeconds(12);
    /// <summary>How far above the room's background noise counts as speech (dB).</summary>
    public double SpeechAboveNoise { get; set; } = 9;
    /// <summary>Scan mode (a recording, as fast as the PC can): no partial lines, and the audio waits for the transcriber.</summary>
    public bool Scan { get; set; }

    public const string DefaultPrompt =
        "Turn with me to John chapter 3 verse 16. Genesis, Exodus, Leviticus, Deuteronomy, Joshua, Judges, Ruth, 1 Samuel, " +
        "2 Kings, 1 Chronicles, Nehemiah, Esther, Job, Psalm 23, Proverbs, Ecclesiastes, Song of Solomon, Isaiah, Jeremiah, " +
        "Lamentations, Ezekiel, Daniel, Hosea, Joel, Amos, Obadiah, Jonah, Micah, Nahum, Habakkuk, Zephaniah, Haggai, " +
        "Zechariah, Malachi, Matthew, Mark, Luke, Acts, Romans, 1 Corinthians 13, Galatians, Ephesians, Philippians, " +
        "Colossians, 1 Thessalonians, 2 Timothy, Titus, Philemon, Hebrews, James, 1 Peter, 1 John, Jude, Revelation. " +
        "Next verse. Amplified, New King James, New Living Translation.";
}

/// <summary>
/// Listening, start to finish: takes the sound (16 kHz mono), finds where the pastor speaks, has the speech
/// written down while each sentence is still going, and reports the references and commands heard.
/// One sentence is worked on at a time, so a slow PC falls a little behind rather than piling up.
/// </summary>
public sealed partial class ListenSession : IDisposable
{
    public const int Rate = 16000;
    private const int Frame = 320; // 20 ms

    private readonly ITranscriber _transcriber;
    private readonly ListenOptions _options;
    private readonly object _gate = new();
    private readonly Thread _worker;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);

    // Speech finding (under _gate).
    private readonly float[] _frame = new float[Frame];
    private int _frameFill;
    private double _noiseDb = -60;
    private readonly Queue<float[]> _preroll = new();
    private List<float>? _speech;
    private int _silentFrames;
    private long _sentenceId;
    private long _samplesIn;
    private int _sinceLastPartial;
    private readonly Queue<(long Id, float[] Audio, long At)> _finished = new();
    private double _levelPeak = -90;

    // Worker state.
    private string _lastFinal = "";
    private readonly Dictionary<long, Dictionary<string, int>> _emitted = [];
    private long _decodeTicks, _decodes;

    public ListenSession(ITranscriber transcriber, ListenOptions? options = null)
    {
        _transcriber = transcriber;
        _options = options ?? new ListenOptions();
        _worker = new Thread(Work) { IsBackground = true, Name = "NEXORA listening", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>A transcript line arrived or grew (on the listening thread).</summary>
    public event Action<TranscriptLine>? Transcript;
    /// <summary>A reference or command was heard, settled (on the listening thread). The long is the sentence it came from.</summary>
    public event Action<Heard, long>? Heard;
    /// <summary>A finished sentence (for meaning search), with the text of the last few sentences together.</summary>
    public event Action<long, string>? Sentence;
    /// <summary>Something went wrong in the transcriber.</summary>
    public event Action<string>? Problem;

    /// <summary>While paused, sound is thrown away (prayer, altar call, side talk).</summary>
    public bool Paused { get; set; }

    /// <summary>The loudest level since the last read, in dBFS (for the meter).</summary>
    public double TakeLevel()
    {
        lock (_gate)
        {
            var l = _levelPeak;
            _levelPeak = -90;
            return l;
        }
    }

    public bool Speaking { get { lock (_gate) return _speech is not null; } }

    /// <summary>How long the transcriber takes for one piece, on average (ms).</summary>
    public double AverageDecodeMs => _decodes == 0 ? 0 : _decodeTicks * 1000.0 / Stopwatch.Frequency / _decodes;

    /// <summary>Seconds of finished speech waiting for the transcriber.</summary>
    public double Behind { get { lock (_gate) return _finished.Sum(f => f.Audio.Length) / (double)Rate; } }

    /// <summary>The time into the sound (seconds of audio taken so far).</summary>
    public TimeSpan Position => TimeSpan.FromSeconds(Interlocked.Read(ref _samplesIn) / (double)Rate);

    // ---------- Sound in ----------

    /// <summary>Takes sound (16 kHz mono, -1 to 1). Call from any one thread. In scan mode it waits while the transcriber is behind.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        if (_options.Scan)
        {
            while (Behind > 30 && !_stop.IsCancellationRequested) Thread.Sleep(20);
        }
        lock (_gate)
        {
            foreach (var s in samples)
            {
                _frame[_frameFill++] = s;
                if (_frameFill == Frame)
                {
                    _frameFill = 0;
                    OnFrame(_frame);
                }
            }
        }
        Interlocked.Add(ref _samplesIn, samples.Length);
    }

    /// <summary>Ends the sentence in progress (end of a recording) and waits until everything is written down.</summary>
    public void Flush(TimeSpan timeout)
    {
        lock (_gate) EndSentence();
        _wake.Set();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (_gate) if (_finished.Count == 0 && !_busy) return;
            Thread.Sleep(20);
        }
    }

    private volatile bool _busy;

    private void OnFrame(float[] f)
    {
        double sum = 0;
        for (var i = 0; i < f.Length; i++) sum += f[i] * f[i];
        var rms = Math.Sqrt(sum / f.Length);
        var db = rms < 1e-6 ? -120 : 20 * Math.Log10(rms);
        if (db > _levelPeak) _levelPeak = db;

        if (Paused)
        {
            _speech = null;
            _preroll.Clear();
            return;
        }

        // The background noise follows quiet frames quickly and loud ones slowly.
        _noiseDb = db < _noiseDb ? _noiseDb + (db - _noiseDb) * 0.2 : _noiseDb + (db - _noiseDb) * 0.002;
        _noiseDb = Math.Clamp(_noiseDb, -90, -25);
        var isSpeech = db > _noiseDb + _options.SpeechAboveNoise && db > -55;

        var copy = (float[])f.Clone();
        if (_speech is null)
        {
            _preroll.Enqueue(copy);
            while (_preroll.Count > 15) _preroll.Dequeue(); // 300 ms before the first word
            if (!isSpeech) return;
            _speech = [];
            foreach (var p in _preroll) _speech.AddRange(p);
            _preroll.Clear();
            _sentenceId++;
            _silentFrames = 0;
            _sinceLastPartial = 0;
            return;
        }

        _speech.AddRange(copy);
        _sinceLastPartial += Frame;
        _silentFrames = isSpeech ? 0 : _silentFrames + 1;
        if (_silentFrames * Frame >= _options.EndAfter.TotalSeconds * Rate)
        {
            EndSentence();
            return;
        }
        if (_speech.Count >= _options.MaxLength.TotalSeconds * Rate) SplitLongSentence();
        else if (!_options.Scan && _options.PartialEvery > TimeSpan.Zero && _sinceLastPartial >= _options.PartialEvery.TotalSeconds * Rate) _wake.Set();
    }

    private void EndSentence()
    {
        if (_speech is null) return;
        // Leave off most of the trailing silence.
        var keep = Math.Max(Frame * 10, _speech.Count - Math.Max(0, _silentFrames - 10) * Frame);
        var audio = _speech.Take(Math.Min(keep, _speech.Count)).ToArray();
        _finished.Enqueue((_sentenceId, audio, _samplesIn));
        _speech = null;
        _silentFrames = 0;
        _wake.Set();
    }

    /// <summary>A sentence that runs on: cut at its quietest 20 ms in the last 3 seconds, so no word is split.</summary>
    private void SplitLongSentence()
    {
        var s = _speech!;
        var from = Math.Max(Frame * 25, s.Count - 3 * Rate);
        var bestAt = s.Count;
        var bestDb = double.MaxValue;
        for (var at = from; at + Frame <= s.Count; at += Frame)
        {
            double sum = 0;
            for (var i = at; i < at + Frame; i++) sum += s[i] * s[i];
            if (sum < bestDb) { bestDb = sum; bestAt = at; }
        }
        var head = s.Take(bestAt).ToArray();
        var tail = s.Skip(bestAt).ToList();
        _finished.Enqueue((_sentenceId, head, _samplesIn));
        _sentenceId++;
        _speech = tail;
        _sinceLastPartial = tail.Count;
        _wake.Set();
    }

    // ---------- Writing it down ----------

    private void Work()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            _wake.WaitOne(100);
            if (ct.IsCancellationRequested) return;
            (long Id, float[] Audio, long At)? job = null;
            var final = true;
            lock (_gate)
            {
                if (_finished.Count > 0) job = _finished.Dequeue();
                else if (_speech is { Count: > Rate / 2 } && !_options.Scan && _options.PartialEvery > TimeSpan.Zero
                         && _sinceLastPartial >= _options.PartialEvery.TotalSeconds * Rate)
                {
                    job = (_sentenceId, _speech.ToArray(), _samplesIn);
                    _sinceLastPartial = 0;
                    final = false;
                }
                _busy = job is not null;
            }
            if (job is not { } j) continue;
            try
            {
                Decode(j.Id, j.Audio, final, TimeSpan.FromSeconds(j.At / (double)Rate), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Problem?.Invoke(ex.Message);
            }
            finally { _busy = false; }
            // Anything else ready? Go again without waiting.
            lock (_gate) if (_finished.Count > 0) _wake.Set();
        }
    }

    private void Decode(long id, float[] audio, bool final, TimeSpan at, CancellationToken ct)
    {
        var prompt = BuildPrompt();
        var t0 = Stopwatch.GetTimestamp();
        var text = Clean(_transcriber.TranscribeAsync(audio, prompt, ct).GetAwaiter().GetResult());
        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref _decodes);

        if (text.Length == 0 && !final) return;
        Transcript?.Invoke(new TranscriptLine(id, text, final, at));
        if (text.Length == 0) return;

        // Report each thing heard once per sentence; while the sentence is still going, wait on the last words.
        if (!_emitted.TryGetValue(id, out var seen)) _emitted[id] = seen = [];
        var counts = new Dictionary<string, int>();
        foreach (var h in SpokenReferences.Find(text))
        {
            if (!final && h.AtEnd) continue;
            var key = h.Kind + "|" + h.Label;
            var n = counts.GetValueOrDefault(key) + 1;
            counts[key] = n;
            if (seen.GetValueOrDefault(key) >= n) continue;
            seen[key] = n;
            Heard?.Invoke(h, id);
        }
        if (final)
        {
            _lastFinal = (_lastFinal + " " + text).Trim();
            if (_lastFinal.Length > 600) _lastFinal = _lastFinal[^600..];
            Sentence?.Invoke(id, _lastFinal);
            foreach (var old in _emitted.Keys.Where(k => k < id - 20).ToList()) _emitted.Remove(old);
        }
    }

    private string BuildPrompt()
    {
        var p = _options.Prompt;
        if (_options.CustomWords.Count > 0) p += " " + string.Join(", ", _options.CustomWords) + ".";
        // The end of what was said last helps Whisper keep the thread.
        if (_lastFinal.Length > 0) p += " " + (_lastFinal.Length > 160 ? _lastFinal[^160..] : _lastFinal);
        return p;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Bracketed();

    private static readonly string[] Phantoms =
    [
        "thank you.", "thanks for watching!", "thanks for watching.", "thank you for watching.", "please subscribe.",
        "you", "bye.", "subtitles by the amara.org community", ".",
    ];

    /// <summary>Removes what Whisper writes for non-speech ("[BLANK_AUDIO]", "(music)") and its usual phantom lines.</summary>
    public static string Clean(string text)
    {
        var t = Bracketed().Replace(text ?? "", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (Phantoms.Contains(t.ToLowerInvariant())) return "";
        if (!t.Any(char.IsLetterOrDigit)) return "";
        return t;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        _worker.Join(3000);
        _transcriber.Dispose();
        _wake.Dispose();
    }
}
