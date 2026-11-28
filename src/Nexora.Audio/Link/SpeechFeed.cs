using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nexora.Audio;

/// <summary>
/// The speech feed: one channel (the pastor's mic) as 16 kHz mono, written by the audio thread and sent over the
/// church network so NEXORA's Scripture PC can listen for Bible references. 16 kHz is what speech recognition uses,
/// and it is light on the network (32 KB a second).
/// </summary>
public sealed class SpeechTap
{
    public const int Rate = 16000;
    private readonly float[] _ring;
    private readonly int _mask;
    private long _write;

    public SpeechTap()
    {
        var size = 1;
        while (size < Rate * 2) size <<= 1; // about two seconds
        _ring = new float[size];
        _mask = size - 1;
    }

    public long Written => Volatile.Read(ref _write);
    public int Capacity => _mask + 1;

    public void Write(ReadOnlySpan<float> samples)
    {
        var w = _write;
        for (var n = 0; n < samples.Length; n++) _ring[(int)((w + n) & _mask)] = samples[n];
        Volatile.Write(ref _write, w + samples.Length);
    }

    /// <summary>Copies samples starting at <paramref name="from"/> (a sample count). Returns how many were copied.</summary>
    public int Read(long from, Span<float> dst)
    {
        var count = (int)Math.Min(dst.Length, Written - from);
        if (count <= 0) return 0;
        for (var n = 0; n < count; n++) dst[n] = _ring[(int)((from + n) & _mask)];
        return count;
    }
}

/// <summary>
/// Brings one channel down to 16 kHz mono for the speech feed: a low-pass at 7 kHz (two filters), then one sample
/// in every (rate / 16000). Runs on the audio thread and never allocates there.
/// </summary>
public sealed class SpeechResampler
{
    private readonly double _step;
    private readonly Dsp.Biquad _lp1, _lp2;
    private Dsp.BiquadState _s1, _s2;
    private double _pos;
    private float _last;
    private readonly float[] _out;

    public SpeechResampler(double inputRate, int maxFrames)
    {
        InputRate = inputRate;
        _step = inputRate / SpeechTap.Rate;
        _lp1 = Dsp.Biquad.Make(Dsp.FilterShape.LowPass, 7000, 0.54, 0, inputRate);
        _lp2 = Dsp.Biquad.Make(Dsp.FilterShape.LowPass, 7000, 1.31, 0, inputRate);
        _out = new float[(int)Math.Ceiling(maxFrames / _step) + 4];
    }

    public double InputRate { get; }

    /// <summary>Takes one buffer (the right side may be null for a mono channel) and writes 16 kHz into the tap.</summary>
    public void Write(SpeechTap tap, float[] l, float[]? r, int frames)
    {
        var count = 0;
        var pos = _pos;
        var prev = _last;
        for (var n = 0; n < frames; n++)
        {
            var x = r is null ? l[n] : (l[n] + r[n]) * 0.5f;
            x = _s2.Tick(_lp2, _s1.Tick(_lp1, x));
            while (pos <= 1.0 && count < _out.Length)
            {
                _out[count++] = prev + (x - prev) * (float)pos;
                pos += _step;
            }
            pos -= 1.0;
            prev = x;
        }
        _pos = pos;
        _last = prev;
        tap.Write(_out.AsSpan(0, count));
    }
}

/// <summary>
/// The speech feed's packet on the network (UDP port 7421): "NXP1", a sequence number, the sample clock,
/// a 32-byte label (the desk and channel name), then 20 ms of 16-bit mono at 16 kHz.
/// </summary>
public static class SpeechPacket
{
    public const int Port = 7421;
    public const int Frames = 320; // 20 ms
    public const int LabelBytes = 32;
    public const int HeaderSize = 16 + LabelBytes;
    public static ReadOnlySpan<byte> Magic => "NXP1"u8;

    public static int Write(Span<byte> dst, uint seq, long clock, string label, ReadOnlySpan<float> samples)
    {
        Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[8..], clock);
        var lab = dst.Slice(16, LabelBytes);
        lab.Clear();
        var bytes = Encoding.UTF8.GetBytes(label);
        bytes.AsSpan(0, Math.Min(bytes.Length, LabelBytes)).CopyTo(lab);
        var p = HeaderSize;
        foreach (var v in samples)
        {
            var s = (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue + 1, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dst[p..], s);
            p += 2;
        }
        return p;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out uint seq, out string label, out ReadOnlySpan<byte> pcm)
    {
        seq = 0;
        label = "";
        pcm = default;
        if (src.Length < HeaderSize + 2 || !src[..4].SequenceEqual(Magic)) return false;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        var lab = src.Slice(16, LabelBytes);
        var end = lab.IndexOf((byte)0);
        label = Encoding.UTF8.GetString(end < 0 ? lab : lab[..end]);
        pcm = src[HeaderSize..];
        return pcm.Length % 2 == 0;
    }
}

/// <summary>
/// Sends the speech feed from the NEX-Audio PC to everyone on the church network (a broadcast), so any Scripture
/// PC can listen without typing an address. Nothing is sent while no channel is chosen.
/// </summary>
public sealed class SpeechSender : IDisposable
{
    private readonly Func<SpeechTap?> _tap;
    private readonly Thread _thread;
    private volatile bool _run = true;

    public SpeechSender(Func<SpeechTap?> tap)
    {
        _tap = tap;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NEX-Audio speech feed", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>What receivers see, for example "SOUND-PC · Pastor". Null stops sending.</summary>
    public volatile string? Label;
    /// <summary>Where to send: null means everyone on the network (broadcast).</summary>
    public volatile string? Host;
    public int Port { get; set; } = SpeechPacket.Port;
    public long PacketsSent { get; private set; }

    private void Loop()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var buf = new float[SpeechPacket.Frames];
        var packet = new byte[SpeechPacket.HeaderSize + buf.Length * 2];
        SpeechTap? lastTap = null;
        long pos = 0;
        uint seq = 0;
        IPEndPoint? target = null;
        string? resolvedFor = "\0";
        while (_run)
        {
            Thread.Sleep(5);
            var tap = _tap();
            var label = Label;
            if (tap is null || label is null)
            {
                lastTap = null;
                continue;
            }
            if (!ReferenceEquals(tap, lastTap))
            {
                lastTap = tap;
                pos = tap.Written;
            }
            if (tap.Written - pos > tap.Capacity / 2) pos = tap.Written - SpeechPacket.Frames;
            var host = Host;
            if (host != resolvedFor)
            {
                resolvedFor = host;
                target = Resolve(host, Port);
            }
            if (target is null) continue;
            while (_run && tap.Written - pos >= SpeechPacket.Frames)
            {
                tap.Read(pos, buf);
                var len = SpeechPacket.Write(packet, seq++, pos, label, buf);
                pos += SpeechPacket.Frames;
                try
                {
                    udp.Send(packet, len, target);
                    PacketsSent++;
                }
                catch (Exception) { }
            }
        }
    }

    private static IPEndPoint? Resolve(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return new IPEndPoint(IPAddress.Broadcast, port);
        try
        {
            var ip = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            return ip is null ? null : new IPEndPoint(ip, port);
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
    }
}

/// <summary>
/// On the Scripture PC: takes the speech feed off the network. Hands out 16 kHz mono samples in the order they
/// were sent, fills short network gaps with silence, and says who is sending.
/// </summary>
public sealed class SpeechReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _senders = [];
    private uint _lastSeq;
    private bool _haveSeq;
    private string? _following;
    private long _lastTicks;

    public SpeechReceiver(int port = SpeechPacket.Port)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.ReceiveBufferSize = 1 << 18;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(ReceiveLoop);
    }

    public int Port { get; }

    /// <summary>Called with each 20 ms of sound (on the receive thread).</summary>
    public event Action<float[]>? Samples;

    /// <summary>Only take sound from this sender (its label), or null for the first one heard.</summary>
    public string? Only { get; set; }

    public long PacketsReceived { get; private set; }
    public long PacketsLost { get; private set; }

    /// <summary>The sender being listened to now, or null when nothing has arrived for a second.</summary>
    public string? Following => Active ? _following : null;
    public bool Active => _lastTicks != 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks)).TotalMilliseconds < 1000;

    /// <summary>Every sender heard in the last 5 seconds.</summary>
    public IReadOnlyList<string> Senders
    {
        get
        {
            lock (_gate)
                return _senders.Where(kv => Stopwatch.GetElapsedTime(kv.Value).TotalSeconds < 5).Select(kv => kv.Key).OrderBy(x => x).ToList();
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception) { if (_stop.IsCancellationRequested) return; continue; }
            Take(r.Buffer);
        }
    }

    /// <summary>One packet (public so tests can feed packets without a network).</summary>
    public void Take(byte[] packet)
    {
        if (!SpeechPacket.TryRead(packet, out var seq, out var label, out var pcm)) return;
        float[] samples;
        var gap = 0;
        lock (_gate)
        {
            _senders[label] = Stopwatch.GetTimestamp();
            var only = Only;
            if (only is not null && only != label) return;
            if (_following != label)
            {
                // A different desk: start again from its packets.
                if (_following is not null && Active && only is null) return;
                _following = label;
                _haveSeq = false;
            }
            if (_haveSeq)
            {
                var d = (int)(seq - _lastSeq);
                if (d <= 0 && d > -1000) return; // late or repeated
                if (d > 1)
                {
                    PacketsLost += d - 1;
                    gap = Math.Min(d - 1, 10); // up to 200 ms of silence keeps the timing right
                }
            }
            _haveSeq = true;
            _lastSeq = seq;
            PacketsReceived++;
            samples = new float[gap * SpeechPacket.Frames + pcm.Length / 2];
            var o = gap * SpeechPacket.Frames;
            for (var i = 0; i + 1 < pcm.Length; i += 2) samples[o++] = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768f;
        }
        Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());
        Samples?.Invoke(samples);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _udp.Dispose(); } catch (Exception) { }
    }
}
