using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Nexora.Audio;

/// <summary>
/// Where the stream mix leaves the audio thread: a ring of stereo samples written by the audio thread
/// and read by the network sender. If the sender falls behind, the oldest sound is overwritten.
/// </summary>
public sealed class StreamTap
{
    private readonly float[] _ring;
    private readonly int _mask;
    private long _write; // frames written (each frame is two floats)

    public StreamTap(double sampleRate)
    {
        var size = 1;
        while (size < sampleRate * 2) size <<= 1; // about a second of stereo
        _ring = new float[size * 2];
        _mask = size - 1;
    }

    public long Written => Volatile.Read(ref _write);

    public void Write(float[] l, float[] r, int frames)
    {
        var w = _write;
        for (var n = 0; n < frames; n++)
        {
            var i = (int)((w + n) & _mask) * 2;
            _ring[i] = l[n];
            _ring[i + 1] = r[n];
        }
        Volatile.Write(ref _write, w + frames);
    }

    /// <summary>Copies frames from <paramref name="from"/> (a frame count) into dst (interleaved). Returns frames copied.</summary>
    public int Read(long from, Span<float> dst)
    {
        var w = Written;
        var frames = (int)Math.Min(dst.Length / 2, w - from);
        if (frames <= 0) return 0;
        for (var n = 0; n < frames; n++)
        {
            var i = (int)((from + n) & _mask) * 2;
            dst[n * 2] = _ring[i];
            dst[n * 2 + 1] = _ring[i + 1];
        }
        return frames;
    }

    public int Capacity => _mask + 1;
}

/// <summary>The packet format for the stream mix on the church network (UDP, 16-bit stereo, 48 kHz).</summary>
public static class StreamPacket
{
    public const int Port = 7420;
    public const int Frames = 240; // 5 ms
    public const int HeaderSize = 16;
    public static ReadOnlySpan<byte> Magic => "NXS1"u8;

    public static int Write(Span<byte> dst, uint seq, long clock, ReadOnlySpan<float> interleaved)
    {
        Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[8..], clock);
        var p = HeaderSize;
        foreach (var v in interleaved)
        {
            var s = (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue + 1, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dst[p..], s);
            p += 2;
        }
        return p;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out uint seq, out long clock, out ReadOnlySpan<byte> pcm)
    {
        seq = 0;
        clock = 0;
        pcm = default;
        if (src.Length < HeaderSize + 4 || !src[..4].SequenceEqual(Magic)) return false;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        clock = BinaryPrimitives.ReadInt64LittleEndian(src[8..]);
        pcm = src[HeaderSize..];
        return pcm.Length % 4 == 0;
    }
}

/// <summary>Sends the stream mix from the NEX-Audio PC to the Live Output PC, 5 ms at a time.</summary>
public sealed class StreamSender : IDisposable
{
    private readonly Func<StreamTap?> _tap;
    private readonly Thread _thread;
    private volatile bool _run = true;
    private volatile string? _host;
    private IPEndPoint? _target;
    private string? _resolved;

    public StreamSender(Func<StreamTap?> tap)
    {
        _tap = tap;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NEX-Audio stream sender", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The Live Output PC's address, or null to stop sending.</summary>
    public string? Host
    {
        get => _host;
        set => _host = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public int Port { get; set; } = StreamPacket.Port;
    public long PacketsSent { get; private set; }

    private void Loop()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var buf = new float[StreamPacket.Frames * 2];
        var packet = new byte[StreamPacket.HeaderSize + buf.Length * 2];
        StreamTap? lastTap = null;
        long pos = 0;
        uint seq = 0;
        while (_run)
        {
            Thread.Sleep(3);
            var tap = _tap();
            var host = _host;
            if (tap is null || host is null)
            {
                lastTap = null;
                continue;
            }
            if (!ReferenceEquals(tap, lastTap))
            {
                lastTap = tap;
                pos = tap.Written;
            }
            if (tap.Written - pos > tap.Capacity / 2) pos = tap.Written - StreamPacket.Frames; // fell far behind: catch up
            if (_resolved != host) Resolve(host);
            if (_target is null) continue;
            while (_run && tap.Written - pos >= StreamPacket.Frames)
            {
                tap.Read(pos, buf);
                var len = StreamPacket.Write(packet, seq++, pos, buf);
                pos += StreamPacket.Frames;
                try
                {
                    udp.Send(packet, len, _target);
                    PacketsSent++;
                }
                catch (Exception) { }
            }
        }
    }

    private void Resolve(string host)
    {
        _resolved = host;
        try
        {
            var ip = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            _target = ip is null ? null : new IPEndPoint(ip, Port);
        }
        catch (Exception)
        {
            _target = null;
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
    }
}

/// <summary>A sound source the relay can fall back to (for example the X32's stream feed into this PC).</summary>
public interface IPcmSource : IDisposable
{
    /// <summary>Reads 16-bit stereo frames that have arrived; returns how many frames were copied.</summary>
    int Read(Span<short> interleaved);
}

/// <summary>
/// On the Live Output PC: takes the stream mix from NEX-Audio off the network, evens out network timing,
/// and plays it to the recorder and the stream as a steady 48 kHz, 16-bit stereo feed over a local TCP port
/// (FFmpeg reads it as raw audio). If NEX-Audio goes quiet, it switches to the backup sound source in a
/// few hundred milliseconds, and back again when NEX-Audio returns.
/// </summary>
public sealed class StreamRelay : IDisposable
{
    public const int Rate = 48000;
    private const int Chunk = 480; // 10 ms

    private readonly UdpClient _udp;
    private readonly TcpListener _tcp;
    private readonly List<TcpClient> _clients = [];
    private readonly short[] _jitter;
    private readonly int _jmask;
    private long _jw, _jr;
    private uint _lastSeq;
    private bool _haveSeq;
    private readonly object _gate = new();
    private readonly Thread _player;
    private readonly CancellationTokenSource _stop = new();
    private long _lastPacketTicks;
    private IPcmSource? _backup;
    private bool _primed;

    public StreamRelay(int udpPort = StreamPacket.Port, int targetMs = 80)
    {
        TargetFrames = Rate * targetMs / 1000;
        var size = 1;
        while (size < Rate) size <<= 1;
        _jitter = new short[size * 2];
        _jmask = size - 1;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.ReceiveBufferSize = 1 << 20;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _tcp = new TcpListener(IPAddress.Loopback, 0);
        _tcp.Start();
        TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(AcceptLoop);
        _player = new Thread(PlayLoop) { IsBackground = true, Name = "NEX-Audio stream relay", Priority = ThreadPriority.AboveNormal };
        _player.Start();
    }

    public int UdpPort { get; }
    /// <summary>FFmpeg reads the sound from tcp://127.0.0.1:TcpPort.</summary>
    public int TcpPort { get; }
    public int TargetFrames { get; }
    public long PacketsReceived { get; private set; }
    public long PacketsLost { get; private set; }
    public long Gaps { get; private set; }

    /// <summary>What is playing now: "NEX-Audio", "Backup" or "Silence".</summary>
    public string Source { get; private set; } = "Silence";

    public bool NexAudioActive => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacketTicks)).TotalMilliseconds < 300 && _lastPacketTicks != 0;
    public double BufferMs { get { lock (_gate) return (_jw - _jr) * 1000.0 / Rate; } }

    /// <summary>The source to use when NEX-Audio is not sending (owned by the relay from now on).</summary>
    public IPcmSource? Backup
    {
        get => _backup;
        set
        {
            var old = Interlocked.Exchange(ref _backup, value);
            old?.Dispose();
        }
    }

    public static string FfmpegInput(int port) => $"tcp://127.0.0.1:{port}";

    private async Task ReceiveLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception) { if (_stop.IsCancellationRequested) return; continue; }
            if (!StreamPacket.TryRead(r.Buffer, out var seq, out _, out var pcm)) continue;
            lock (_gate)
            {
                if (_haveSeq)
                {
                    var gap = (int)(seq - _lastSeq);
                    if (gap <= 0 && gap > -1000) continue; // late or repeated packet
                    if (gap > 1) PacketsLost += gap - 1;
                }
                _haveSeq = true;
                _lastSeq = seq;
                var frames = pcm.Length / 4;
                // Never let the buffer run far ahead (for example after the player stalled).
                if (_jw - _jr + frames > _jmask) _jr = _jw + frames - TargetFrames;
                for (var i = 0; i < frames * 2; i++)
                    _jitter[(int)((_jw * 2 + i) & (_jmask * 2 + 1))] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
                _jw += frames;
                PacketsReceived++;
            }
            Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var c = await _tcp.AcceptTcpClientAsync(_stop.Token);
                c.NoDelay = true;
                c.SendBufferSize = Rate * 4 / 2;
                lock (_clients) _clients.Add(c);
            }
            catch (Exception)
            {
                if (_stop.IsCancellationRequested) return;
            }
        }
    }

    private void PlayLoop()
    {
        var chunk = new short[Chunk * 2];
        var bytes = new byte[Chunk * 4];
        var backup = new short[Chunk * 2];
        var start = Stopwatch.GetTimestamp();
        long sent = 0;
        while (!_stop.IsCancellationRequested)
        {
            // Real time: one 10 ms chunk every 10 ms, by the clock.
            var due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * Rate);
            if (due - sent < Chunk)
            {
                Thread.Sleep(2);
                continue;
            }
            if (due - sent > Rate) sent = due - Chunk; // the PC was asleep: do not try to catch up a whole second
            Fill(chunk, backup);
            for (var i = 0; i < chunk.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), chunk[i]);
            sent += Chunk;
            TcpClient[] clients;
            lock (_clients) clients = [.. _clients];
            foreach (var c in clients)
            {
                try
                {
                    // A reader that has stopped reading is dropped rather than holding the sound up.
                    if (!c.Connected || c.Client.Poll(0, SelectMode.SelectError)) throw new IOException();
                    c.Client.Send(bytes, SocketFlags.None);
                }
                catch (Exception)
                {
                    lock (_clients) _clients.Remove(c);
                    try { c.Dispose(); } catch (Exception) { }
                }
            }
        }
    }

    private void Fill(short[] chunk, short[] backup)
    {
        var active = NexAudioActive;
        lock (_gate)
        {
            var level = _jw - _jr;
            if (active)
            {
                // Wait until there is a cushion before playing, so network wobble does not make gaps.
                if (!_primed && level >= TargetFrames) _primed = true;
                if (_primed && level >= Chunk)
                {
                    // Keep the cushion near its target: drop a little when it grows too big.
                    if (level > TargetFrames * 3) _jr = _jw - TargetFrames;
                    for (var i = 0; i < chunk.Length; i++) chunk[i] = _jitter[(int)((_jr * 2 + i) & (_jmask * 2 + 1))];
                    _jr += Chunk;
                    Source = "NEX-Audio";
                    DrainBackup(backup);
                    return;
                }
                if (_primed)
                {
                    Gaps++;
                    _primed = false;
                }
            }
            else
            {
                _primed = false;
                _jr = _jw;
            }
        }
        var b = _backup;
        if (b is not null)
        {
            var got = b.Read(chunk);
            if (got < Chunk) Array.Clear(chunk, got * 2, chunk.Length - got * 2);
            Source = got > 0 ? "Backup" : "Silence";
            return;
        }
        Array.Clear(chunk);
        Source = "Silence";
    }

    /// <summary>Keeps the backup source current while it is not used, so switching over plays fresh sound.</summary>
    private void DrainBackup(short[] scratch)
    {
        var b = _backup;
        if (b is null) return;
        while (b.Read(scratch) == Chunk) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _udp.Dispose(); } catch (Exception) { }
        try { _tcp.Stop(); } catch (Exception) { }
        _player.Join(500);
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        Backup = null;
    }
}
