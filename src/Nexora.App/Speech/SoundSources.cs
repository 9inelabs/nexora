using System.Diagnostics;
using Nexora.App.Controls;
using Nexora.App.Output;
using Nexora.App.Services;
using Nexora.Audio;
using Nexora.Audio.Dsp;

namespace Nexora.App.Speech;

/// <summary>Where the pastor's voice comes from. Hands 16 kHz mono sound to the listener as it arrives.</summary>
public interface ISoundSource : IDisposable
{
    /// <summary>What it is, for the screen ("NEX-Audio: SOUND-PC · Pastor").</summary>
    string Name { get; }
    /// <summary>How it is doing right now, in words.</summary>
    string Status { get; }
    /// <summary>Sound is arriving.</summary>
    bool Ok { get; }
    void Start(Action<float[]> sink);
}

/// <summary>Turns sound at any rate into 16 kHz mono (a low-pass at 7 kHz, then one sample in every rate/16000).</summary>
public sealed class Downsampler(double inputRate)
{
    private readonly double _step = inputRate / 16000.0;
    private readonly Biquad _lp1 = Biquad.Make(FilterShape.LowPass, 7000, 0.54, 0, inputRate);
    private readonly Biquad _lp2 = Biquad.Make(FilterShape.LowPass, 7000, 1.31, 0, inputRate);
    private BiquadState _s1, _s2;
    private double _pos;
    private float _last;

    public double InputRate { get; } = inputRate;

    public float[] Process(ReadOnlySpan<float> x)
    {
        var outp = new List<float>((int)(x.Length / _step) + 2);
        var pos = _pos;
        var prev = _last;
        foreach (var raw in x)
        {
            var v = _s2.Tick(_lp2, _s1.Tick(_lp1, raw));
            while (pos <= 1.0)
            {
                outp.Add(prev + (v - prev) * (float)pos);
                pos += _step;
            }
            pos -= 1.0;
            prev = v;
        }
        _pos = pos;
        _last = prev;
        return [.. outp];
    }
}

/// <summary>The speech feed from NEX-Audio over the church network (the pastor's channel, chosen on its Stream page).</summary>
public sealed class NexAudioSource : ISoundSource
{
    private SpeechReceiver? _rx;
    private string? _problem;

    public NexAudioSource(string? onlyFrom) => OnlyFrom = string.IsNullOrWhiteSpace(onlyFrom) ? null : onlyFrom;

    public string? OnlyFrom { get; }
    public string Name => "NEX-Audio" + (_rx?.Following is { } f ? ": " + f : OnlyFrom is { } o ? ": " + o : "");
    public bool Ok => _rx?.Active == true;
    public IReadOnlyList<string> Senders => _rx?.Senders ?? [];

    public string Status => _problem
        ?? (Ok ? $"Hearing {_rx!.Following}" + (_rx.PacketsLost > 0 ? $" · {_rx.PacketsLost} packets lost" : "")
            : "Waiting for the speech feed. On the sound PC: NEX-Audio > Stream > Speech feed for Scripture, pick the pastor's channel.");

    public void Start(Action<float[]> sink)
    {
        try
        {
            _rx = new SpeechReceiver { Only = OnlyFrom };
            _rx.Samples += sink;
        }
        catch (Exception ex)
        {
            _problem = "Cannot listen on the network (port 7421 is in use?): " + ex.Message;
            AppLog.Write("Speech feed: " + ex.Message);
        }
    }

    public void Dispose() => _rx?.Dispose();
}

/// <summary>
/// A sound input on this PC (a USB sound card fed from the desk), read through FFmpeg, with an optional
/// noise reducer and a voice-band filter.
/// </summary>
public sealed class DeviceSource(string device, bool reduceNoise) : ISoundSource
{
    private FfmpegReader? _reader;

    public string Name => "Sound input: " + device;
    public bool Ok => _reader?.Flowing == true;
    public string Status => _reader?.Problem ?? (Ok ? "Hearing " + device : "Starting " + device + "…");

    public void Start(Action<float[]> sink)
    {
        if (Recorder.FfmpegPath is not { } ff)
        {
            _reader = FfmpegReader.Failed("FFmpeg is missing. Reinstall NEXORA.");
            return;
        }
        var filter = "highpass=f=90,lowpass=f=7500" + (reduceNoise ? ",afftdn=nf=-28" : "");
        _reader = new FfmpegReader(ff, ["-f", "dshow", "-audio_buffer_size", "40", "-i", "audio=" + device, "-af", filter], sink, realtime: true);
    }

    public void Dispose() => _reader?.Dispose();
}

/// <summary>A recorded sermon played into the listener: at normal speed (a rehearsal), or as fast as the PC can (a scan).</summary>
public sealed class FileSource(string path, bool asFastAsPossible, bool reduceNoise) : ISoundSource
{
    private FfmpegReader? _reader;

    public string Path { get; } = path;
    public string Name => "Recording: " + System.IO.Path.GetFileName(Path);
    public bool Ok => _reader?.Flowing == true;
    public bool Finished => _reader?.Ended == true;
    public string Status => _reader?.Problem ?? (Finished ? "The recording has ended." : Ok ? "Playing " + System.IO.Path.GetFileName(Path) : "Opening the recording…");

    public void Start(Action<float[]> sink)
    {
        if (Recorder.FfmpegPath is not { } ff)
        {
            _reader = FfmpegReader.Failed("FFmpeg is missing. Reinstall NEXORA.");
            return;
        }
        var args = new List<string>();
        if (!asFastAsPossible) args.Add("-re");
        args.AddRange(["-i", Path, "-vn"]);
        if (reduceNoise) args.AddRange(["-af", "highpass=f=90,afftdn=nf=-28"]);
        _reader = new FfmpegReader(ff, args, sink, realtime: !asFastAsPossible);
    }

    public void Dispose() => _reader?.Dispose();
}

/// <summary>The sound of an NDI source (for example a vMix or OBS output that carries the pastor's mic).</summary>
public sealed class NdiSoundSource(string source) : ISoundSource
{
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _flowing;
    private long _lastTicks;
    private string? _problem;

    public string Name => "NDI: " + source;
    public bool Ok => _flowing && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks)).TotalSeconds < 1;
    public string Status => _problem ?? (Ok ? "Hearing " + source : "Waiting for sound from " + source + "…");

    public void Start(Action<float[]> sink)
    {
        if (!Ndi.Available)
        {
            _problem = Ndi.Problem;
            return;
        }
        new Thread(() => Run(sink)) { IsBackground = true, Name = "NDI sound " + source }.Start();
    }

    private unsafe void Run(Action<float[]> sink)
    {
        var recv = IntPtr.Zero;
        Downsampler? down = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (recv == IntPtr.Zero)
                {
                    recv = Ndi.AudioReceiver(source);
                    if (recv == IntPtr.Zero)
                    {
                        _problem = "Cannot connect to " + source;
                        Thread.Sleep(1000);
                        continue;
                    }
                    _problem = null;
                }
                var kind = Ndi.CaptureAudio(recv, out var f, 200);
                if (kind == -1)
                {
                    Ndi.FreeReceiver(recv);
                    recv = IntPtr.Zero;
                    continue;
                }
                if (kind != 2) continue;
                try
                {
                    if (f.Samples <= 0 || f.Channels <= 0 || f.Data == IntPtr.Zero) continue;
                    if (down is null || Math.Abs(down.InputRate - f.SampleRate) > 1) down = new Downsampler(f.SampleRate);
                    // Mix every channel to one.
                    var mono = new float[f.Samples];
                    for (var c = 0; c < f.Channels; c++)
                    {
                        var p = (float*)(f.Data + c * f.ChannelStride);
                        for (var i = 0; i < f.Samples; i++) mono[i] += p[i] / f.Channels;
                    }
                    sink(down.Process(mono));
                    _flowing = true;
                    Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());
                }
                finally { Ndi.FreeAudio(recv, ref f); }
            }
        }
        catch (Exception ex)
        {
            _problem = "NDI sound stopped: " + ex.Message;
            AppLog.Write("NDI sound: " + ex.Message);
        }
        finally { Ndi.FreeReceiver(recv); }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Runs FFmpeg to turn any input into 16 kHz mono floats on standard output, and hands them on in 20 ms pieces.</summary>
public sealed class FfmpegReader : IDisposable
{
    private readonly Process? _p;
    private long _lastTicks;
    private readonly bool _realtime;
    private readonly List<string> _errors = [];

    private FfmpegReader(string problem)
    {
        Problem = problem;
    }

    public static FfmpegReader Failed(string problem) => new(problem);

    public FfmpegReader(string ffmpeg, IReadOnlyList<string> input, Action<float[]> sink, bool realtime)
    {
        _realtime = realtime;
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in input) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(a);
        try
        {
            _p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Problem = "FFmpeg could not start: " + ex.Message;
            return;
        }
        if (_p is null) return;
        _p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } d) return;
            AppLog.Write("Listening (FFmpeg): " + d);
            lock (_errors) if (_errors.Count < 20) _errors.Add(d);
        };
        _p.BeginErrorReadLine();
        new Thread(() => Pump(sink)) { IsBackground = true, Name = "Listening sound" }.Start();
    }

    public string? Problem { get; private set; }
    public bool Ended { get; private set; }
    public bool Flowing => !Ended && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks)).TotalSeconds < (_realtime ? 1 : 5) && _lastTicks != 0;

    private void Pump(Action<float[]> sink)
    {
        var s = _p!.StandardOutput.BaseStream;
        var buf = new byte[320 * 4];
        var fill = 0;
        try
        {
            int n;
            while ((n = s.Read(buf, fill, buf.Length - fill)) > 0)
            {
                fill += n;
                if (fill < buf.Length) continue;
                var f = new float[320];
                Buffer.BlockCopy(buf, 0, f, 0, buf.Length);
                fill = 0;
                Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());
                sink(f);
            }
            if (fill >= 4)
            {
                var f = new float[fill / 4];
                Buffer.BlockCopy(buf, 0, f, 0, f.Length * 4);
                sink(f);
            }
        }
        catch (Exception) { }
        Ended = true;
        try { _p.WaitForExit(2000); } catch (Exception) { }
        if (_p.HasExited && _p.ExitCode != 0)
        {
            string first;
            lock (_errors) first = _errors.FirstOrDefault() ?? "";
            Problem = first.Contains("dshow", StringComparison.OrdinalIgnoreCase) || first.Contains("audio=", StringComparison.OrdinalIgnoreCase)
                ? "That sound input could not be opened. Check it is plugged in and not used by another program."
                : first.Length > 0 ? "FFmpeg stopped: " + first : "FFmpeg stopped.";
        }
    }

    public void Dispose()
    {
        try { if (_p is { HasExited: false }) _p.Kill(true); } catch (Exception) { }
        _p?.Dispose();
    }
}
