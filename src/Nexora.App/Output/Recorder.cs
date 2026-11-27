using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Nexora.App.Services;
using Nexora.Core.Model;

namespace Nexora.App.Output;

/// <summary>A rendered frame shared by several encoders; it goes back to the pool when all are done with it.</summary>
public sealed class Frame
{
    private static readonly ConcurrentBag<Frame> Pool = new();
    private int _refs;

    public byte[] Data { get; private set; } = [];

    public static Frame Rent(int size)
    {
        if (!Pool.TryTake(out var f) || f.Data.Length != size) f = new Frame { Data = new byte[size] };
        f._refs = 1;
        return f;
    }

    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0 && Pool.Count < 12) Pool.Add(this);
    }
}

/// <summary>
/// One FFmpeg process that takes raw frames on its input and writes a file or a stream.
/// Frames are queued and written on a background thread so the screens never wait for the encoder.
/// </summary>
public sealed class FfmpegPipe : IDisposable
{
    private readonly Process _p;
    private readonly BlockingCollection<Frame> _queue = new(boundedCapacity: 4);
    private readonly Thread _writer;
    private volatile bool _closing;

    public string Name { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public double Kbps { get; private set; }
    public double Fps { get; private set; }
    public int Dropped { get; private set; }
    public int Skipped;
    public string? LastError { get; private set; }
    private readonly List<string> _errors = [];

    /// <summary>Every error line FFmpeg printed, oldest first (at most 20).</summary>
    public IReadOnlyList<string> Errors { get { lock (_errors) return _errors.ToList(); } }
    public DateTime LastProgressUtc { get; private set; } = DateTime.UtcNow;
    public bool Exited => _p.HasExited;
    public int? ExitCode => _p.HasExited ? _p.ExitCode : null;

    public FfmpegPipe(string name, string ffmpeg, IEnumerable<string> args)
    {
        Name = name;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        try { _p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _p.ErrorDataReceived += (_, e) => Parse(e.Data);
        _p.BeginErrorReadLine();
        _p.OutputDataReceived += (_, e) => ParseProgress(e.Data);
        _p.BeginOutputReadLine();
        _writer = new Thread(Write) { IsBackground = true, Name = "FFmpeg " + name };
        _writer.Start();
    }

    /// <summary>Queues one frame. When the encoder is behind, the frame is dropped rather than waiting.</summary>
    public void Push(Frame frame)
    {
        if (_closing || _p.HasExited) return;
        frame.AddRef();
        try
        {
            if (_queue.TryAdd(frame)) return;
        }
        catch (InvalidOperationException) { }
        frame.Release();
        Interlocked.Increment(ref Skipped);
    }

    private void Write()
    {
        try
        {
            var stdin = _p.StandardInput.BaseStream;
            foreach (var f in _queue.GetConsumingEnumerable())
            {
                try { stdin.Write(f.Data, 0, f.Data.Length); }
                finally { f.Release(); }
            }
            stdin.Flush();
            stdin.Close();
        }
        catch (Exception ex)
        {
            if (!_closing) LastError ??= "The encoder stopped: " + ex.Message;
        }
    }

    /// <summary>FFmpeg's "-progress" lines: key=value, a block every half second.</summary>
    private void ParseProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line[..eq].Trim();
        var val = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "fps" when double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f): Fps = f; break;
            case "bitrate" when double.TryParse(val.Replace("kbits/s", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var k): Kbps = k; break;
            case "drop_frames" when int.TryParse(val, out var d): Dropped = d; break;
            case "progress": LastProgressUtc = DateTime.UtcNow; break;
        }
    }

    private void Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("refused", StringComparison.OrdinalIgnoreCase) || line.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            LastError = line.Trim();
            lock (_errors) { if (_errors.Count < 20) _errors.Add(line.Trim()); }
            AppLog.Write($"FFmpeg {Name}: {line.Trim()}");
        }
    }

    /// <summary>Finishes the file properly: closes the input and gives FFmpeg time to write the end.</summary>
    public void Stop(int waitMs = 8000)
    {
        if (_closing) return;
        _closing = true;
        _queue.CompleteAdding();
        try
        {
            if (!_writer.Join(waitMs / 2)) { }
            if (!_p.WaitForExit(waitMs)) _p.Kill(true);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        Stop(3000);
        _p.Dispose();
        _queue.Dispose();
    }
}

/// <summary>
/// Records the Broadcast picture to MP4 and sends it to a streaming service, each through its own
/// FFmpeg so one can stop without the other. The file is written in fragments, so a power cut loses
/// only the last few seconds.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly object _gate = new();
    private FfmpegPipe? _record;
    private FfmpegPipe? _clean;
    private FfmpegPipe? _stream;
    private bool _wantStream;
    private DateTime _nextStreamTry;
    private int _streamTries;
    private string? _encoder;
    private bool _recordSilent;
    private bool _recordRetried;
    private string? _recordName;

    public BroadcastSettings Settings { get; set; } = new();

    /// <summary>NEX-Audio's stream mix, when the settings say to use it.</summary>
    public Nexora.Audio.StreamRelay? Relay { get; set; }

    private bool UseNexAudio => Settings.NexAudioSound && Relay is not null;
    public string? RecordingFile { get; private set; }
    public string? RecordProblem { get; private set; }
    public string? StreamProblem { get; private set; }
    public string StreamState { get; private set; } = "Off";

    public bool Recording => _record is { Exited: false };
    public bool Streaming => _wantStream;
    public bool CleanWanted => Settings.CleanRecording && Recording;
    public bool Active => _record is not null || _clean is not null || _wantStream;
    public int Width => Height * 16 / 9 / 2 * 2;
    public int Height => Math.Clamp(Settings.Height, 360, 1080) / 2 * 2;
    public int Fps => Math.Clamp(Settings.Fps, 15, 60);

    public double RecordingSeconds => _record is null ? 0 : (DateTime.UtcNow - _record.StartedUtc).TotalSeconds;
    public double StreamKbps => _stream?.Kbps ?? 0;
    public int Dropped => (_stream?.Dropped ?? 0) + (_stream?.Skipped ?? 0) + (_record?.Skipped ?? 0);
    public double EncodeFps => _record?.Fps ?? _stream?.Fps ?? 0;

    public static string? FfmpegPath
    {
        get
        {
            var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe), Path.Combine(AppContext.BaseDirectory, exe) })
                if (File.Exists(p)) return p;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }
    }

    public string RecordFolder => string.IsNullOrWhiteSpace(Settings.RecordFolder) ? Nexora.Core.Config.AppPaths.DefaultRecordingsDir : Settings.RecordFolder!;

    public double DiskFreeGb
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(RecordFolder));
                return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace / 1e9;
            }
            catch { return 0; }
        }
    }

    public string? StartRecording(string? serviceName)
    {
        lock (_gate)
        {
            if (Recording) return null;
            if (FfmpegPath is not { } ff) return RecordProblem = "FFmpeg is missing, so recording cannot start. Reinstall NEXORA.";
            if (DiskFreeGb is > 0 and < 2) return RecordProblem = $"Only {DiskFreeGb:0.0} GB free on the recording drive. Free up space first.";
            try
            {
                Directory.CreateDirectory(RecordFolder);
                var name = FileName(serviceName ?? Settings.ServiceName);
                _recordName = name;
                _recordRetried = false;
                RecordProblem = null;
                // A sound device that is not plugged in would stop FFmpeg at once: record silently instead and say so.
                _recordSilent = false;
                if (HasSoundDevice && !SoundDevicePresent())
                {
                    _recordSilent = true;
                    RecordProblem = SoundMissing();
                }
                OpenRecording(ff);
                AppLog.Write("Recording to " + RecordingFile);
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Write("Recording: " + ex.Message);
                return RecordProblem = "Recording could not start: " + ex.Message;
            }
        }
    }

    private void OpenRecording(string ff)
    {
        RecordingFile = Path.Combine(RecordFolder, _recordName + ".mp4");
        _record = new FfmpegPipe("recording", ff, Args(ff, RecordingFile, stream: false, withAudio: !_recordSilent));
        if (Settings.CleanRecording)
            _clean = new FfmpegPipe("clean recording", ff, Args(ff, Path.Combine(RecordFolder, _recordName + " (camera only).mp4"), stream: false, withAudio: !_recordSilent));
    }

    private bool HasSoundDevice => !UseNexAudio && !string.IsNullOrWhiteSpace(Settings.AudioDevice) && OperatingSystem.IsWindows();

    private bool SoundDevicePresent()
    {
        try
        {
            var list = Nexora.App.Controls.DirectShow.AudioDevices();
            // If Windows lists nothing at all, let FFmpeg try (the list itself may have failed).
            return list.Count == 0 || list.Contains(Settings.AudioDevice!);
        }
        catch { return true; }
    }

    private string SoundMissing() =>
        $"Recording without sound: the sound device \"{Settings.AudioDevice}\" could not be opened. " +
        "Check the mixer cable is plugged into this PC, then pick the sound device again in Settings › Recording & streaming.";

    /// <summary>Turns FFmpeg's last words into something the media team can act on.</summary>
    public static string Explain(IReadOnlyList<string> errors, bool usedSound, string folder)
    {
        var all = string.Join(" | ", errors);
        bool Has(string x) => all.Contains(x, StringComparison.OrdinalIgnoreCase);
        if (Has("Permission denied") || Has("Access is denied") || (Has("No such file or directory") && !Has("dshow")))
            return $"NEXORA cannot write to the recording folder ({folder}). Pick another folder in Settings › Recording & streaming, or check the drive is connected.";
        if (Has("No space left"))
            return "The recording drive is full. Free up space or pick another folder in Settings › Recording & streaming.";
        if (usedSound && (Has("dshow") || Has("audio") || Has("Error opening input")))
            return "NEXORA could not open the sound device. Check the mixer cable is plugged into this PC, then pick the sound device again in Settings › Recording & streaming.";
        if (Has("nvenc") || Has("qsv") || Has("encoder"))
            return "The graphics card could not record. NEXORA will use the PC's processor instead; press Start recording again.";
        var detail = errors.FirstOrDefault(e => !e.StartsWith("Error opening input files", StringComparison.OrdinalIgnoreCase)) ?? errors.FirstOrDefault();
        return "The recorder could not start. Check the sound device and recording folder in Settings › Recording & streaming, then try again." +
            (detail is null ? "" : $" (Details for support: {detail})");
    }

    public void StopRecording()
    {
        FfmpegPipe? r, c;
        lock (_gate)
        {
            r = _record;
            c = _clean;
            _record = null;
            _clean = null;
        }
        Task.Run(() =>
        {
            r?.Dispose();
            c?.Dispose();
            AppLog.Write("Recording stopped: " + RecordingFile);
        });
    }

    public string? StartStream()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(Settings.StreamUrl)) return StreamProblem = "No stream address is set. An admin adds it in Settings, Recording & streaming.";
            if (FfmpegPath is null) return StreamProblem = "FFmpeg is missing, so streaming cannot start. Reinstall NEXORA.";
            _wantStream = true;
            _streamTries = 0;
            _nextStreamTry = DateTime.MinValue;
            StreamProblem = null;
            StreamState = "Connecting";
            OpenStream();
            return null;
        }
    }

    public void StopStream()
    {
        FfmpegPipe? s;
        lock (_gate)
        {
            _wantStream = false;
            s = _stream;
            _stream = null;
            StreamState = "Off";
        }
        Task.Run(() => s?.Dispose());
    }

    private void OpenStream()
    {
        try
        {
            var url = Settings.StreamUrl!.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(Settings.StreamKey)) url += "/" + Settings.StreamKey!.Trim();
            var sound = !HasSoundDevice || SoundDevicePresent();
            if (!sound) StreamProblem = "Streaming without sound: the sound device \"" + Settings.AudioDevice + "\" could not be opened. Check the mixer cable and Settings › Recording & streaming.";
            _stream = new FfmpegPipe("stream", FfmpegPath!, Args(FfmpegPath!, url, stream: true, withAudio: sound));
        }
        catch (Exception ex)
        {
            StreamProblem = "Streaming could not start: " + ex.Message;
            _stream = null;
        }
    }

    /// <summary>Called about once a second: notices a dropped stream and connects again, waiting longer each time.</summary>
    public void Watch()
    {
        lock (_gate)
        {
            if (_record is { Exited: true } r)
            {
                var errors = r.Errors;
                var early = DateTime.UtcNow - r.StartedUtc < TimeSpan.FromSeconds(20);
                var usedSound = HasSoundDevice && !_recordSilent;
                AppLog.Write("Recording stopped: " + string.Join(" | ", errors));
                _record = null;
                r.Dispose();
                var c = _clean;
                _clean = null;
                c?.Dispose();
                if (early && !_recordRetried && FfmpegPath is { } ff)
                {
                    // It failed as it started. Try once more the safe way: no sound device, the PC's own encoder.
                    _recordRetried = true;
                    if (usedSound) _recordSilent = true;
                    var hardware = _encoder is "h264_nvenc" or "h264_qsv";
                    if (hardware) _encoder = "libx264";
                    if (usedSound || hardware)
                    {
                        try
                        {
                            OpenRecording(ff);
                            RecordProblem = usedSound ? SoundMissing() : null;
                            AppLog.Write("Recording restarted " + (usedSound ? "without sound" : "with the software encoder"));
                        }
                        catch (Exception ex) { AppLog.Write("Recording retry: " + ex.Message); }
                    }
                }
                if (_record is null)
                    RecordProblem = (early ? "Recording could not start. " : "Recording stopped unexpectedly. ") + Explain(errors, usedSound, RecordFolder);
            }
            if (!_wantStream) return;
            if (_stream is { Exited: false } s)
            {
                var stalled = DateTime.UtcNow - s.LastProgressUtc > TimeSpan.FromSeconds(10) && DateTime.UtcNow - s.StartedUtc > TimeSpan.FromSeconds(15);
                if (!stalled)
                {
                    if (DateTime.UtcNow - s.StartedUtc > TimeSpan.FromSeconds(4)) { StreamState = "Live"; _streamTries = 0; }
                    return;
                }
                s.Dispose();
                _stream = null;
            }
            if (_stream is { Exited: true } gone)
            {
                StreamProblem = gone.LastError;
                gone.Dispose();
                _stream = null;
            }
            if (_stream is null)
            {
                if (_nextStreamTry == DateTime.MinValue)
                {
                    _streamTries++;
                    _nextStreamTry = DateTime.UtcNow.AddSeconds(Math.Min(30, 2 * _streamTries));
                    StreamState = "Reconnecting";
                    AppLog.Write($"Stream dropped, trying again (attempt {_streamTries})");
                }
                else if (DateTime.UtcNow >= _nextStreamTry)
                {
                    _nextStreamTry = DateTime.MinValue;
                    OpenStream();
                }
            }
        }
    }

    public void Push(Frame frame)
    {
        _record?.Push(frame);
        _stream?.Push(frame);
    }

    public void PushClean(Frame frame) => _clean?.Push(frame);

    private static string FileName(string service)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((service ?? "Service").Where(c => !bad.Contains(c)).ToArray()).Trim();
        if (s.Length == 0) s = "Service";
        return $"{s} {DateTime.Now:yyyy-MM-dd HHmm}";
    }

    private List<string> Args(string ffmpeg, string target, bool stream, bool withAudio)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "1", "-y" };
        a.AddRange(["-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-r", Fps.ToString(), "-i", "pipe:0"]);
        var audio = withAudio && !string.IsNullOrWhiteSpace(Settings.AudioDevice) && OperatingSystem.IsWindows();
        if (withAudio && UseNexAudio)
        {
            // NEX-Audio's stream mix, steady 48 kHz from the relay (which falls back to the sound device by itself).
            audio = true;
            if (Settings.SoundDelayMs != 0)
                a.AddRange(["-itsoffset", (Settings.SoundDelayMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)]);
            a.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-i", Nexora.Audio.StreamRelay.FfmpegInput(Relay!.TcpPort)]);
        }
        else if (audio)
        {
            if (Settings.SoundDelayMs != 0)
                a.AddRange(["-itsoffset", (Settings.SoundDelayMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)]);
            a.AddRange(["-f", "dshow", "-audio_buffer_size", "50", "-i", "audio=" + Settings.AudioDevice]);
        }
        else
        {
            // Silence, so the file and the stream always have a sound track.
            a.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        }
        a.AddRange(["-map", "0:v", "-map", "1:a"]);
        var kbps = Math.Clamp(Settings.VideoKbps, 1000, 20000);
        _encoder ??= PickEncoder(ffmpeg);
        if (_encoder == "h264_nvenc")
            a.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "cbr", "-b:v", $"{kbps}k"]);
        else if (_encoder == "h264_qsv")
            a.AddRange(["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", $"{kbps}k"]);
        else
            a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", $"{kbps}k"]);
        a.AddRange(["-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-pix_fmt", "yuv420p", "-g", (Fps * 2).ToString(), "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]);
        if (stream) a.AddRange(["-f", "flv", target]);
        else a.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", target]);
        if (!audio) a.Insert(a.IndexOf("-map"), "-shortest");
        return a;
    }

    /// <summary>The graphics card's encoder when there is one (much lighter on the PC), otherwise x264.</summary>
    private static string PickEncoder(string ffmpeg)
    {
        foreach (var enc in new[] { "h264_nvenc", "h264_qsv" })
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var x in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=640x360:r=30", "-frames:v", "5", "-c:v", enc, "-f", "null", "-" })
                    psi.ArgumentList.Add(x);
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.StandardError.ReadToEnd();
                if (p.WaitForExit(8000) && p.ExitCode == 0)
                {
                    AppLog.Write("Encoder: " + enc);
                    return enc;
                }
            }
            catch { }
        }
        AppLog.Write("Encoder: libx264");
        return "libx264";
    }

    public void Dispose()
    {
        _wantStream = false;
        _record?.Dispose();
        _clean?.Dispose();
        _stream?.Dispose();
    }
}
