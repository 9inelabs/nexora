using System.Text.Json;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Record;

public enum TapeState
{
    Stopped,
    Recording,
    Playing,
    Paused,
}

/// <summary>Where each channel is recorded from.</summary>
public enum RecordTap
{
    /// <summary>The input as it arrives from the X32, before anything NEX-Audio does. Needed for virtual soundcheck.</summary>
    Input,
    /// <summary>After the channel's own processing (EQ, dynamics, effects), before its fader.</summary>
    Processed,
}

public sealed class TakeTrack
{
    /// <summary>The channel (and input) number, from 1. 0 for the main mix.</summary>
    public int Input { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The file, inside the take's folder, or a full path for an imported take.</summary>
    public string File { get; set; } = "";
    public int Channels { get; set; } = 1;
}

/// <summary>One recording: a WAV file per channel, all the same length, in one folder.</summary>
public sealed class Take
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public string RecordedBy { get; set; } = "";
    public int SampleRate { get; set; }
    public RecordFormat Format { get; set; }
    public RecordTap Tap { get; set; }
    public long Frames { get; set; }
    /// <summary>Brought in from another program's files rather than recorded here.</summary>
    public bool Imported { get; set; }
    /// <summary>Still being recorded (or the recording was cut short).</summary>
    public bool Open { get; set; }
    public List<TakeTrack> Tracks { get; set; } = [];
}

public sealed record TakeInfo(string Id, string Name, DateTime StartedUtc, string RecordedBy, int SampleRate, int Tracks, double Seconds, long Bytes,
    RecordTap Tap, bool Imported, bool Open, bool CanSoundcheck);

/// <summary>The recorder's own settings. Saved to audio/recorder.json.</summary>
public sealed record TapeSettings
{
    /// <summary>Where takes go. Empty = the NEX-Audio data folder. A second drive is best for long services.</summary>
    public string? Folder { get; init; }
    public RecordFormat Format { get; init; } = RecordFormat.Pcm24;
    public RecordTap Tap { get; init; } = RecordTap.Input;
    /// <summary>Also record the main mix as a stereo file.</summary>
    public bool RecordMain { get; init; } = true;
    /// <summary>The channels (from 1) that record. Null = every channel.</summary>
    public List<int>? Armed { get; init; }
    /// <summary>Playback starts again at the beginning when it reaches the end.</summary>
    public bool Loop { get; init; }
}

public sealed record TapeStatus(
    TapeState State,
    /// <summary>The channels play the recording instead of the microphones (virtual soundcheck).</summary>
    bool Playback,
    string? TakeId,
    string? TakeName,
    double Position,
    double Length,
    IReadOnlyList<int> LiveInputs,
    IReadOnlyList<int> PlaybackInputs,
    long Dropouts,
    double FreeGb,
    double MinutesLeft,
    string? Note,
    bool Ready,
    double SampleRate,
    int Inputs,
    string Folder,
    TapeSettings Settings,
    IReadOnlyList<TakeInfo> Takes)
{
    public static TapeStatus Empty { get; } = new(TapeState.Stopped, false, null, null, 0, 0, [], [], 0, 0, 0, null, false, 0, 0, "", new TapeSettings(), []);
}

/// <summary>
/// NEX-Audio's multitrack recorder and player. It records every channel to its own WAV file, and plays
/// a recording back into the channels instead of the microphones, so the desk can be mixed again with
/// no band in the room (virtual soundcheck). The sound thread only ever copies into and out of lock-free
/// rings; a disk thread does the reading and writing.
/// </summary>
public sealed class Multitrack : IDisposable
{
    private const int BlockFrames = 4096;
    private readonly string _dataDir;
    private readonly string _settingsFile;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private TapeSettings _settings;

    private int _inputs;
    private double _rate;
    private int _maxFrames;
    private volatile bool _ready;

    private volatile RecSession? _rec;
    private volatile PlaySession? _play;
    private volatile bool _playback;
    private volatile bool _paused;
    private volatile bool[] _live = [];
    private float[] _discard = [];
    private string? _note;

    public Multitrack(string dataDir, Action<string>? log = null)
    {
        _dataDir = dataDir;
        _settingsFile = Path.Combine(dataDir, "recorder.json");
        _log = log ?? (_ => { });
        _settings = Load<TapeSettings>(_settingsFile) ?? new TapeSettings();
    }

    /// <summary>Something the recorder screen shows changed.</summary>
    public event Action? Changed;

    private void OnChanged()
    {
        Invalidate();
        Changed?.Invoke();
    }

    public TapeSettings Settings { get { lock (_gate) return _settings; } }
    public string Folder => string.IsNullOrWhiteSpace(Settings.Folder) ? Path.Combine(_dataDir, "recordings") : Settings.Folder!;
    public bool Playback => _playback;
    public bool Recording => _rec is not null;
    /// <summary>A recording is playing now.</summary>
    public bool Rolling => _play is not null && !_paused;

    // ---- The sound engine ----------------------------------------------------------------------

    /// <summary>The driver opened: how many inputs, at what rate.</summary>
    public void Prepare(int inputs, double sampleRate, int maxFrames)
    {
        lock (_gate)
        {
            _inputs = inputs;
            _rate = sampleRate;
            _maxFrames = maxFrames;
            _discard = new float[maxFrames];
            if (_live.Length != inputs) _live = new bool[inputs];
            _ready = true;
        }
        OnChanged();
    }

    /// <summary>The driver closed: a recording in progress is finished and kept.</summary>
    public void Detach()
    {
        _ready = false;
        if (_rec is not null) StopRecording("The sound engine stopped, so the recording was finished and kept.");
        if (_play is not null) Stop();
        OnChanged();
    }

    /// <summary>Sound thread, before the mix: records the inputs, and plays the recording into the channels.</summary>
    public void PreMix(float[][] input, int frames)
    {
        var rec = _rec;
        if (rec is not null && !rec.Processed) rec.Capture(i => i < input.Length ? input[i] : null, frames);

        if (!_playback) return;
        var live = _live;
        var play = _play;
        var take = play is not null && !_paused && play.HasFrames(frames);
        if (play is not null && !_paused && !take && play.Primed)
        {
            // The end of the recording: stop there. Otherwise the disk fell behind for a moment.
            if (play.Ended) _paused = true;
            else Interlocked.Increment(ref play.Underruns);
        }
        for (var t = 0; t < (play?.Inputs.Length ?? 0); t++)
        {
            var idx = play!.Inputs[t];
            var into = idx >= 0 && idx < input.Length && !(idx < live.Length && live[idx]) ? input[idx] : _discard;
            if (into.Length < frames) into = _discard;
            if (take) play.Rings[t].Read(into, 0, frames);
            else if (into != _discard) Array.Clear(into, 0, frames);
        }
        if (take) play!.Advance(frames);
        // Channels the recording has no track for stay live. With nothing loaded, the recorded channels are quiet.
        if (play is null)
            foreach (var idx in _lastTracks)
                if (idx >= 0 && idx < input.Length && !(idx < live.Length && live[idx])) Array.Clear(input[idx], 0, frames);
    }

    private volatile int[] _lastTracks = [];

    /// <summary>Sound thread, after the mix: records processed channels and the main mix.</summary>
    public void PostMix(MixEngine mix, int frames)
    {
        var rec = _rec;
        if (rec is null) return;
        if (rec.Processed) rec.Capture(mix.ChannelPre, frames);
        if (rec.Main is not null && mix.MainOut is { } m) rec.CaptureMain(m.L, m.R, frames);
        rec.EndBlock(frames);
    }

    // ---- Recording -------------------------------------------------------------------------------

    public Take StartRecording(string name, string by, Func<int, string> channelName, int channelCount)
    {
        lock (_gate)
        {
            if (!_ready) throw new InvalidOperationException("Start the sound first (Setup > Driver), then record.");
            if (_rec is not null) throw new InvalidOperationException("NEX-Audio is already recording.");
            if (_playback) throw new InvalidOperationException("The channels are playing a recording. Go back to the live inputs before recording.");
            var s = _settings;
            var count = Math.Min(channelCount, _inputs);
            var armed = (s.Armed ?? Enumerable.Range(1, count).ToList()).Where(n => n >= 1 && n <= count).Distinct().Order().ToList();
            if (armed.Count == 0 && !s.RecordMain) throw new InvalidOperationException("No channels are armed to record.");
            var stamp = DateTime.Now;
            var title = string.IsNullOrWhiteSpace(name) ? $"Service {stamp:ddd d MMM HH.mm}" : name.Trim();
            if (title.Length > 60) title = title[..60];
            var id = stamp.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(Folder, id + " " + SafeName(title));
            Directory.CreateDirectory(dir);
            var free = FreeBytes(dir);
            var perSecond = (armed.Count + (s.RecordMain ? 2 : 0)) * (s.Format == RecordFormat.Pcm24 ? 3 : 4) * _rate;
            if (free >= 0 && free < perSecond * 120)
                throw new InvalidOperationException("There is not enough room on the disk for even two minutes of recording. Free some space or pick another folder.");

            var take = new Take
            {
                Id = id, Name = title, StartedUtc = DateTime.UtcNow, RecordedBy = by, SampleRate = (int)_rate, Format = s.Format, Tap = s.Tap, Open = true,
            };
            foreach (var n in armed)
                take.Tracks.Add(new TakeTrack { Input = n, Name = channelName(n), File = $"{n:00} {SafeName(channelName(n))}.wav" });
            if (s.RecordMain) take.Tracks.Add(new TakeTrack { Input = 0, Name = "Main mix", File = "Main mix.wav", Channels = 2 });
            WriteTake(dir, take);
            _rec = new RecSession(dir, take, (int)_rate, _maxFrames, s.Format, s.Tap == RecordTap.Processed, _log);
            _note = null;
            _log($"Recording \"{title}\": {armed.Count} channels{(s.RecordMain ? " and the main mix" : "")}.");
        }
        OnChanged();
        return _rec!.Take;
    }

    public void StopRecording(string? note = null)
    {
        RecSession? rec;
        lock (_gate)
        {
            rec = _rec;
            _rec = null;
        }
        if (rec is null) return;
        rec.Finish();
        rec.Take.Frames = rec.FramesWritten;
        rec.Take.Open = false;
        WriteTake(rec.Dir, rec.Take);
        _note = note ?? (rec.Overflows > 0 ? $"Saved, but the disk fell behind {rec.Overflows} times. Record to a faster or emptier drive." : null);
        _log($"Recording \"{rec.Take.Name}\" saved: {rec.FramesWritten / Math.Max(1, _rate):0} s.");
        OnChanged();
    }

    // ---- Playback (virtual soundcheck) -------------------------------------------------------------

    /// <summary>Turns virtual soundcheck on or off: the recorded channels play the recording instead of the microphones.</summary>
    public void SetPlayback(bool on)
    {
        if (on && _rec is not null) throw new InvalidOperationException("Stop recording first.");
        _playback = on;
        if (!on) Stop();
        _log(on ? "Virtual soundcheck on: the channels play the recording." : "Back to the live inputs.");
        OnChanged();
    }

    /// <summary>Keeps one channel on its live input during virtual soundcheck (for a talkback mic, say).</summary>
    public void SetLive(int channel, bool live)
    {
        var copy = (bool[])_live.Clone();
        if (channel < 1 || channel > copy.Length) return;
        copy[channel - 1] = live;
        _live = copy;
        OnChanged();
    }

    public void Load(string takeId, double seconds = 0)
    {
        if (_rec is not null) throw new InvalidOperationException("Stop recording first.");
        if (!_ready) throw new InvalidOperationException("Start the sound first, then play a recording.");
        var (dir, take) = FindTake(takeId) ?? throw new InvalidOperationException("That recording could not be found.");
        if (Math.Abs(take.SampleRate - _rate) > 1)
            throw new InvalidOperationException($"This recording is at {take.SampleRate / 1000.0:0.#} kHz but the desk runs at {_rate / 1000:0.#} kHz. Set the X32 to {take.SampleRate / 1000.0:0.#} kHz to play it.");
        var tracks = take.Tracks.Where(t => t.Input >= 1 && t.Input <= _inputs).ToList();
        if (tracks.Count == 0) throw new InvalidOperationException("This recording has no channel tracks to play.");
        var old = _play;
        _play = null;
        old?.Dispose();
        var p = new PlaySession(dir, take, tracks, _maxFrames, Settings.Loop, (long)(seconds * _rate));
        _lastTracks = p.Inputs;
        _paused = true;
        _play = p;
        OnChanged();
    }

    public void Play(string? takeId = null)
    {
        if (takeId is not null && takeId != _play?.Take.Id) Load(takeId);
        else if (_play is { Ended: true } p && !p.HasFrames(1)) Load(p.Take.Id);
        if (_play is null) throw new InvalidOperationException("Pick a recording to play.");
        if (!_playback) SetPlayback(true);
        _paused = false;
        OnChanged();
    }

    public void Pause()
    {
        _paused = true;
        OnChanged();
    }

    public void Stop()
    {
        var p = _play;
        if (p is null) return;
        _paused = true;
        // Back to the start, ready to play again.
        try { Load(p.Take.Id); }
        catch (Exception)
        {
            _play = null;
            p.Dispose();
        }
        OnChanged();
    }

    public void Seek(double seconds)
    {
        var p = _play;
        if (p is null) return;
        var wasPaused = _paused;
        Load(p.Take.Id, Math.Max(0, seconds));
        _paused = wasPaused;
        OnChanged();
    }

    public void Configure(Func<TapeSettings, TapeSettings> change)
    {
        lock (_gate)
        {
            if (_rec is not null) throw new InvalidOperationException("Stop recording before changing the recorder's settings.");
            var s = change(_settings);
            if (!string.IsNullOrWhiteSpace(s.Folder))
            {
                try { Directory.CreateDirectory(s.Folder!); }
                catch (Exception ex) { throw new InvalidOperationException($"NEX-Audio cannot use the folder {s.Folder}: {ex.Message}"); }
            }
            _settings = s;
            Save(_settingsFile, s);
        }
        OnChanged();
    }

    // ---- Takes -----------------------------------------------------------------------------------

    private IReadOnlyList<TakeInfo>? _takes;
    private DateTime _takesAt;

    /// <summary>The recordings in the folder (looked up again after a change, or every few seconds).</summary>
    public IReadOnlyList<TakeInfo> Takes()
    {
        var cached = _takes;
        if (cached is not null && DateTime.UtcNow - _takesAt < TimeSpan.FromSeconds(_rec is null ? 10 : 2)) return cached;
        _takes = ScanTakes();
        _takesAt = DateTime.UtcNow;
        return _takes;
    }

    private void Invalidate() => _takes = null;

    private IReadOnlyList<TakeInfo> ScanTakes()
    {
        var list = new List<TakeInfo>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var dir in Directory.EnumerateDirectories(Folder))
            {
                var t = Load<Take>(Path.Combine(dir, "take.json"));
                if (t is null) continue;
                long bytes = 0;
                foreach (var tr in t.Tracks)
                {
                    var f = TrackPath(dir, tr);
                    if (File.Exists(f)) bytes += new FileInfo(f).Length;
                }
                var frames = t.Frames;
                if (t.Open && t.Tracks.FirstOrDefault() is { } first && File.Exists(TrackPath(dir, first)))
                    frames = Math.Max(0, (new FileInfo(TrackPath(dir, first)).Length - 44) / ((t.Format == RecordFormat.Pcm24 ? 3 : 4) * first.Channels));
                list.Add(new TakeInfo(t.Id, t.Name, t.StartedUtc, t.RecordedBy, t.SampleRate, t.Tracks.Count(x => x.Input > 0),
                    t.SampleRate > 0 ? frames / (double)t.SampleRate : 0, bytes, t.Tap, t.Imported, t.Open && _rec?.Take.Id != t.Id,
                    t.Tap == RecordTap.Input && t.Tracks.Any(x => x.Input > 0)));
            }
        }
        catch (Exception ex) { _log("Recordings: " + ex.Message); }
        return list.OrderByDescending(t => t.StartedUtc).ToList();
    }

    public void Rename(string takeId, string name)
    {
        var (dir, take) = FindTake(takeId) ?? throw new InvalidOperationException("That recording could not be found.");
        take.Name = string.IsNullOrWhiteSpace(name) ? take.Name : name.Trim()[..Math.Min(60, name.Trim().Length)];
        WriteTake(dir, take);
        OnChanged();
    }

    public void Delete(string takeId)
    {
        if (_rec?.Take.Id == takeId) throw new InvalidOperationException("That recording is still going.");
        if (_play?.Take.Id == takeId)
        {
            var p = _play;
            _play = null;
            p?.Dispose();
            _lastTracks = [];
        }
        var (dir, take) = FindTake(takeId) ?? throw new InvalidOperationException("That recording could not be found.");
        // Only ever delete inside the recordings folder, and for an imported take only the take's own note.
        if (take.Imported) File.Delete(Path.Combine(dir, "take.json"));
        foreach (var tr in take.Tracks.Where(t => !Path.IsPathRooted(t.File)))
        {
            var f = Path.Combine(dir, tr.File);
            if (File.Exists(f)) File.Delete(f);
        }
        var j = Path.Combine(dir, "take.json");
        if (File.Exists(j)) File.Delete(j);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        OnChanged();
    }

    /// <summary>
    /// Brings in a folder of WAV files from another recording program (one mono file per channel, in channel
    /// order by file name), so it can be played into the desk.
    /// </summary>
    public Take Import(string folder, string? name = null)
    {
        if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder could not be found.");
        var files = Directory.EnumerateFiles(folder, "*.wav").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new InvalidOperationException("There are no WAV files in that folder.");
        var take = new Take
        {
            Id = "imp-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder.TrimEnd('\\', '/')) : name.Trim(),
            StartedUtc = DateTime.UtcNow, Imported = true, Tap = RecordTap.Input,
        };
        var n = 1;
        foreach (var f in files)
        {
            using var r = new WavReader(f);
            if (take.SampleRate == 0) take.SampleRate = r.SampleRate;
            else if (r.SampleRate != take.SampleRate) throw new InvalidOperationException($"{Path.GetFileName(f)} is at a different sample rate from the other files.");
            take.Frames = Math.Max(take.Frames, r.Frames);
            take.Format = RecordFormat.Pcm24;
            take.Tracks.Add(new TakeTrack { Input = n++, Name = Path.GetFileNameWithoutExtension(f), File = Path.GetFullPath(f), Channels = r.Channels });
            if (n > 64) break;
        }
        var dir = Path.Combine(Folder, take.Id + " " + SafeName(take.Name));
        Directory.CreateDirectory(dir);
        WriteTake(dir, take);
        OnChanged();
        return take;
    }

    private (string Dir, Take Take)? FindTake(string id)
    {
        if (!Directory.Exists(Folder)) return null;
        foreach (var dir in Directory.EnumerateDirectories(Folder))
        {
            var t = Load<Take>(Path.Combine(dir, "take.json"));
            if (t?.Id == id) return (dir, t);
        }
        return null;
    }

    internal static string TrackPath(string dir, TakeTrack t) => Path.IsPathRooted(t.File) ? t.File : Path.Combine(dir, t.File);

    // ---- Status ----------------------------------------------------------------------------------

    public TapeStatus Status
    {
        get
        {
            var rec = _rec;
            var play = _play;
            var s = Settings;
            var state = rec is not null ? TapeState.Recording : play is null ? TapeState.Stopped : _paused ? (play.Position == 0 ? TapeState.Stopped : TapeState.Paused) : TapeState.Playing;
            var rate = _rate > 0 ? _rate : 48000;
            double pos = 0, len = 0;
            string? id = null, name = null;
            long drops = 0;
            if (rec is not null)
            {
                pos = len = rec.FramesWritten / rate;
                id = rec.Take.Id;
                name = rec.Take.Name;
                drops = rec.Overflows;
            }
            else if (play is not null)
            {
                pos = play.Position / rate;
                len = play.Length / rate;
                id = play.Take.Id;
                name = play.Take.Name;
                drops = play.Underruns;
            }
            var free = FreeBytes(Folder);
            var tracks = (s.Armed?.Count ?? Math.Min(_inputs, 32)) + (s.RecordMain ? 2 : 0);
            var perSecond = Math.Max(1, tracks) * (s.Format == RecordFormat.Pcm24 ? 3 : 4) * rate;
            var live = _live;
            var playIdx = (play?.Inputs ?? _lastTracks).Where(i => !(i < live.Length && live[i])).Select(i => i + 1).ToList();
            return new TapeStatus(state, _playback, id, name, pos, len,
                live.Select((l, i) => (l, i)).Where(x => x.l).Select(x => x.i + 1).ToList(), _playback ? playIdx : [],
                drops, free < 0 ? 0 : free / 1e9, free < 0 ? 0 : free / perSecond / 60, _note, _ready, _rate, _inputs, Folder, s, Takes());
        }
    }

    private static long FreeBytes(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // ---- Files -----------------------------------------------------------------------------------

    internal static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(s.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Untitled" : clean.Length > 40 ? clean[..40] : clean;
    }

    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    private static void WriteTake(string dir, Take t)
    {
        var tmp = Path.Combine(dir, "take.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, Json));
        File.Move(tmp, Path.Combine(dir, "take.json"), true);
    }

    private static T? Load<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : default; }
        catch (Exception) { return default; }
    }

    private void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex) { _log("Recorder settings: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_rec is not null) StopRecording();
        _play?.Dispose();
        _play = null;
    }

    // ---- Sessions ----------------------------------------------------------------------------------

    /// <summary>One recording in progress: a ring per track filled by the sound thread, emptied to disk by its own thread.</summary>
    private sealed class RecSession
    {
        private readonly SampleRing[] _rings;
        private readonly int[] _inputs;
        private readonly WavWriter[] _writers;
        private readonly WavWriter? _mainWriter;
        private readonly float[] _interleave;
        private readonly Thread _thread;
        private readonly Action<string> _log;
        private volatile bool _stop;
        private bool _dropThisBlock;
        private long _queued;

        public RecSession(string dir, Take take, int rate, int maxFrames, RecordFormat format, bool processed, Action<string> log)
        {
            Dir = dir;
            Take = take;
            Processed = processed;
            _log = log;
            var tracks = take.Tracks.Where(t => t.Input > 0).ToList();
            _inputs = tracks.Select(t => t.Input - 1).ToArray();
            var cap = Math.Max(rate * 4, maxFrames * 8);
            _rings = tracks.Select(_ => new SampleRing(cap)).ToArray();
            _writers = tracks.Select(t => new WavWriter(Path.Combine(dir, t.File), rate, 1, format)).ToArray();
            if (take.Tracks.FirstOrDefault(t => t.Input == 0) is { } main)
            {
                Main = new SampleRing(cap * 2);
                _mainWriter = new WavWriter(Path.Combine(dir, main.File), rate, 2, format);
            }
            _interleave = new float[maxFrames * 2];
            _thread = new Thread(Drain) { IsBackground = true, Name = "NEX-Audio recorder", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public string Dir { get; }
        public Take Take { get; }
        public bool Processed { get; }
        public SampleRing? Main { get; }
        public long Overflows;
        public long FramesWritten => Interlocked.Read(ref _framesWritten);
        private long _framesWritten;

        /// <summary>Sound thread. Every track takes this block, or (if the disk has fallen behind) none do, so they stay in line.</summary>
        public void Capture(Func<int, float[]?> source, int frames)
        {
            _dropThisBlock = false;
            foreach (var r in _rings)
                if (r.Free < frames) _dropThisBlock = true;
            if (Main is not null && Main.Free < frames * 2) _dropThisBlock = true;
            if (_dropThisBlock)
            {
                Interlocked.Increment(ref Overflows);
                return;
            }
            for (var t = 0; t < _rings.Length; t++)
            {
                var src = source(_inputs[t]);
                if (src is null || src.Length < frames) Array.Clear(_interleave, 0, frames);
                else Array.Copy(src, _interleave, frames);
                _rings[t].Write(_interleave, 0, frames);
            }
        }

        public void CaptureMain(float[] l, float[] r, int frames)
        {
            if (_dropThisBlock || Main is null) return;
            for (var n = 0; n < frames; n++)
            {
                _interleave[n * 2] = l[n];
                _interleave[n * 2 + 1] = r[n];
            }
            Main.Write(_interleave, 0, frames * 2);
        }

        public void EndBlock(int frames)
        {
            if (!_dropThisBlock) Interlocked.Add(ref _queued, frames);
        }

        private void Drain()
        {
            var buf = new float[BlockFrames * 2];
            var failed = false;
            while (true)
            {
                var stopping = _stop;
                var moved = false;
                try
                {
                    // Take the same number of frames from every track each time.
                    var ready = _rings.Length > 0 ? _rings.Min(r => r.Count) : Main is not null ? Main.Count / 2 : 0;
                    if (Main is not null) ready = Math.Min(ready, Main.Count / 2);
                    while (ready > 0)
                    {
                        var n = Math.Min(ready, BlockFrames);
                        for (var t = 0; t < _rings.Length; t++)
                        {
                            _rings[t].Read(buf, 0, n);
                            if (!failed) _writers[t].Write(buf.AsSpan(0, n));
                        }
                        if (Main is not null)
                        {
                            Main.Read(buf, 0, n * 2);
                            if (!failed) _mainWriter!.Write(buf.AsSpan(0, n * 2));
                        }
                        Interlocked.Add(ref _framesWritten, n);
                        ready -= n;
                        moved = true;
                    }
                }
                catch (Exception ex)
                {
                    if (!failed) _log("Recording could not write to the disk: " + ex.Message);
                    failed = true;
                }
                if (stopping) break;
                if (!moved) Thread.Sleep(10);
            }
            foreach (var w in _writers)
                try { w.Dispose(); } catch (Exception) { }
            try { _mainWriter?.Dispose(); } catch (Exception) { }
        }

        public void Finish()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>One recording loaded for playback: a ring per track kept full by its own thread.</summary>
    private sealed class PlaySession : IDisposable
    {
        private readonly WavReader[] _readers;
        private readonly Thread _thread;
        private readonly bool _loop;
        private volatile bool _stop;
        private long _position;

        public PlaySession(string dir, Take take, List<TakeTrack> tracks, int maxFrames, bool loop, long start)
        {
            Take = take;
            _loop = loop;
            Inputs = tracks.Select(t => t.Input - 1).ToArray();
            _readers = new WavReader[tracks.Count];
            try
            {
                for (var i = 0; i < tracks.Count; i++)
                {
                    var f = TrackPath(dir, tracks[i]);
                    if (!File.Exists(f)) throw new InvalidOperationException($"The file for {tracks[i].Name} is missing ({tracks[i].File}).");
                    _readers[i] = new WavReader(f);
                }
            }
            catch
            {
                foreach (var r in _readers) r?.Dispose();
                throw;
            }
            Length = _readers.Max(r => r.Frames);
            if (Length == 0) throw new InvalidOperationException("This recording is empty.");
            start = Math.Clamp(start, 0, Math.Max(0, Length - 1));
            foreach (var r in _readers) r.Seek(start);
            _position = start;
            var cap = Math.Max(BlockFrames * 8, maxFrames * 8);
            Rings = tracks.Select(_ => new SampleRing(cap)).ToArray();
            _thread = new Thread(Fill) { IsBackground = true, Name = "NEX-Audio player", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public Take Take { get; }
        public int[] Inputs { get; }
        public SampleRing[] Rings { get; }
        public long Length { get; }
        public long Position => Interlocked.Read(ref _position);
        public volatile bool Ended;
        /// <summary>The disk thread has filled the rings once, so a shortfall after this is a real gap.</summary>
        public volatile bool Primed;
        public long Underruns;

        public bool HasFrames(int frames)
        {
            foreach (var r in Rings)
                if (r.Count < frames) return false;
            return true;
        }

        public void Advance(int frames)
        {
            var p = Interlocked.Add(ref _position, frames);
            if (p >= Length && _loop) Interlocked.Add(ref _position, -Length);
        }

        private void Fill()
        {
            var bufs = _readers.Select(r => new float[BlockFrames * r.Channels]).ToArray();
            var mono = new float[BlockFrames];
            while (!_stop)
            {
                if (Rings.Any(r => r.Free < BlockFrames))
                {
                    Primed = true;
                    Thread.Sleep(5);
                    continue;
                }
                var most = 0;
                var got = new int[_readers.Length];
                try
                {
                    for (var i = 0; i < _readers.Length; i++)
                    {
                        got[i] = _readers[i].Read(bufs[i]);
                        most = Math.Max(most, got[i]);
                    }
                }
                catch (Exception)
                {
                    most = 0;
                }
                if (most == 0)
                {
                    if (_loop)
                    {
                        foreach (var r in _readers) r.Seek(0);
                        continue;
                    }
                    Ended = true;
                    Primed = true;
                    Thread.Sleep(20);
                    continue;
                }
                for (var i = 0; i < _readers.Length; i++)
                {
                    var ch = _readers[i].Channels;
                    // Take the left (first) channel of a stereo file; pad a shorter file with silence.
                    for (var n = 0; n < most; n++) mono[n] = n < got[i] ? bufs[i][n * ch] : 0;
                    Rings[i].Write(mono, 0, most);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(2));
            foreach (var r in _readers) r.Dispose();
        }
    }
}
