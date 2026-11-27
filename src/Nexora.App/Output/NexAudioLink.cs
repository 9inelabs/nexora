using System.Diagnostics;
using Nexora.App.Services;
using Nexora.Audio;
using Nexora.Core.Model;

namespace Nexora.App.Output;

/// <summary>
/// The X32's own stream feed into this PC (the sound device in Settings), read through FFmpeg, as the
/// backup for the NEX-Audio stream mix. It keeps reading all the time, so a switch-over plays fresh sound.
/// </summary>
public sealed class DeviceSoundSource : IPcmSource
{
    private readonly Process? _p;
    private readonly short[] _ring = new short[48000 * 2 * 2]; // 2 seconds of stereo
    private long _w, _r;
    private readonly object _gate = new();

    public DeviceSoundSource(string ffmpeg, string device)
    {
        Device = device;
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "dshow", "-audio_buffer_size", "20", "-i", "audio=" + device, "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" })
            psi.ArgumentList.Add(a);
        _p = Process.Start(psi);
        if (_p is null) return;
        _p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) AppLog.Write("Backup sound: " + d); };
        _p.BeginErrorReadLine();
        new Thread(Pump) { IsBackground = true, Name = "Backup sound" }.Start();
    }

    public string Device { get; }

    private void Pump()
    {
        var buf = new byte[4096];
        var s = _p!.StandardOutput.BaseStream;
        try
        {
            int n;
            var odd = -1;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                lock (_gate)
                {
                    var i = 0;
                    if (odd >= 0)
                    {
                        _ring[_w++ % _ring.Length] = (short)(odd | buf[0] << 8);
                        i = 1;
                        odd = -1;
                    }
                    for (; i + 1 < n; i += 2) _ring[_w++ % _ring.Length] = (short)(buf[i] | buf[i + 1] << 8);
                    if (i < n) odd = buf[i];
                    if (_w - _r > _ring.Length) _r = _w - _ring.Length / 2;
                }
            }
        }
        catch { }
    }

    public int Read(Span<short> interleaved)
    {
        lock (_gate)
        {
            var avail = (int)Math.Min(_w - _r, interleaved.Length) & ~1;
            for (var i = 0; i < avail; i++) interleaved[i] = _ring[(_r + i) % _ring.Length];
            _r += avail;
            return avail / 2;
        }
    }

    public void Dispose()
    {
        try { _p?.Kill(true); } catch { }
        _p?.Dispose();
    }
}

/// <summary>
/// NEX-Audio on the Live Output PC: receives the stream mix for recording and streaming, and watches
/// the NEX-Audio PC's heartbeat so it can hand the room to the X32 if NEX-Audio stops.
/// </summary>
public sealed class NexAudioLink : IDisposable
{
    private StreamRelay? _relay;
    private FailsafeWatcher? _watcher;
    private string? _lastNote;

    public NexAudioLink()
    {
        try
        {
            _watcher = new FailsafeWatcher();
            _watcher.Fired += note =>
            {
                _lastNote = note;
                AppLog.Write(note);
            };
        }
        catch (Exception ex)
        {
            AppLog.Write("NEX-Audio failsafe watcher could not start: " + ex.Message);
        }
    }

    public StreamRelay? Relay => _relay;

    /// <summary>Follows the recording settings: the relay runs when NEX-Audio sound is chosen, with the sound device as backup.</summary>
    public void Apply(BroadcastSettings s)
    {
        try
        {
            if (s.NexAudioSound && _relay is null) _relay = new StreamRelay();
            else if (!s.NexAudioSound && _relay is not null)
            {
                _relay.Dispose();
                _relay = null;
            }
            if (_relay is null) return;
            var device = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(s.AudioDevice) ? s.AudioDevice : null;
            var current = (_relay.Backup as DeviceSoundSource)?.Device;
            if (device != current)
                _relay.Backup = device is not null && Recorder.FfmpegPath is { } ff ? new DeviceSoundSource(ff, device) : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("NEX-Audio stream link: " + ex.Message);
        }
    }

    /// <summary>A line for the Service tab.</summary>
    public (string? Sound, string? Failsafe, bool Armed, bool HandedOver) Describe()
    {
        string? sound = _relay is null ? null : _relay.Source switch
        {
            "NEX-Audio" => "Stream sound: from NEX-Audio",
            "Backup" => "Stream sound: NEX-Audio is quiet, using the sound device (backup)",
            _ => "Stream sound: waiting for NEX-Audio",
        };
        var engines = _watcher?.Engines ?? [];
        var e = engines.OrderByDescending(x => x.LastSeenUtc).FirstOrDefault();
        if (e is null) return (sound, null, false, false);
        var hb = e.Last;
        string text;
        if (e.Fired || hb.HandedOver) text = e.FiredReason ?? _lastNote ?? "The X32 is running the room. Return to NEX-Audio from the NEX-Audio screen.";
        else if (e.SecondsSinceHeard > 2) text = $"NEX-Audio on {hb.Pc} not heard for {e.SecondsSinceHeard:0} s";
        else if (hb.Armed) text = $"Failsafe armed for {hb.Pc}: the X32 takes over if NEX-Audio stops";
        else text = $"NEX-Audio on {hb.Pc} is {hb.State}; failsafe not armed";
        return (sound, text, hb.Armed, e.Fired || hb.HandedOver);
    }

    public void Dispose()
    {
        _relay?.Dispose();
        _watcher?.Dispose();
    }
}
