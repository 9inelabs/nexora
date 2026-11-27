using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nexora.Audio.X32;

namespace Nexora.Audio;

/// <summary>
/// What the NEX-Audio PC says about itself many times a second, so the Live Output PC can tell at once
/// when it stops (power cut, crash, cable pulled) and hand the room back to the X32.
/// </summary>
public sealed record Heartbeat(
    string Id,
    string Pc,
    long Seq,
    /// <summary>"running", "problem" or "stopped".</summary>
    string State,
    /// <summary>Sound is really passing through the X32's USB card.</summary>
    bool Flowing,
    /// <summary>The engineer turned the failsafe on for this service.</summary>
    bool Armed,
    /// <summary>The room is already on the X32's own mix (the failsafe fired or was pressed).</summary>
    bool HandedOver,
    /// <summary>Goes up each time the engineer returns the room to NEX-Audio, so each handover happens once.</summary>
    int Epoch,
    string? X32,
    int DeskScene,
    int ConsoleScene,
    int TimeoutMs)
{
    public const int Port = 7421;
    private static readonly byte[] Magic = "NXH1"u8.ToArray();

    public byte[] Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        var b = new byte[Magic.Length + json.Length];
        Magic.CopyTo(b, 0);
        json.CopyTo(b, Magic.Length);
        return b;
    }

    public static Heartbeat? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..4].SequenceEqual(Magic)) return null;
        try { return JsonSerializer.Deserialize<Heartbeat>(data[4..], Json); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Sends the heartbeat 20 times a second: to the whole network, and straight to the Live Output PC when known.</summary>
public sealed class HeartbeatSender : IDisposable
{
    private readonly Func<Heartbeat> _make;
    private readonly Timer _timer;
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork) { EnableBroadcast = true };
    private volatile string? _host;
    private IPEndPoint? _direct;
    private string? _resolved;
    private readonly int _port;

    public HeartbeatSender(Func<Heartbeat> make, int port = Heartbeat.Port, int intervalMs = 50)
    {
        _make = make;
        _port = port;
        _timer = new Timer(_ => Send(), null, 0, intervalMs);
    }

    public string? Host
    {
        get => _host;
        set => _host = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Also broadcast on the network (off in tests).</summary>
    public bool Broadcast { get; set; } = true;

    private void Send()
    {
        try
        {
            var bytes = _make().Encode();
            if (Broadcast)
            {
                try { _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, _port)); }
                catch (Exception) { }
            }
            var host = _host;
            if (host != _resolved)
            {
                _resolved = host;
                _direct = null;
                if (host is not null)
                {
                    var ip = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                    if (ip is not null) _direct = new IPEndPoint(ip, _port);
                }
            }
            if (_direct is { } d)
            {
                try { _udp.Send(bytes, bytes.Length, d); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _udp.Dispose();
    }
}

/// <summary>What the Live Output PC knows about one NEX-Audio PC.</summary>
public sealed record WatchedEngine(Heartbeat Last, DateTime LastSeenUtc, bool Fired, string? FiredReason, DateTime? FiredUtc)
{
    public double SecondsSinceHeard => (DateTime.UtcNow - LastSeenUtc).TotalSeconds;
}

/// <summary>
/// On the Live Output PC: listens for NEX-Audio heartbeats. When an armed NEX-Audio PC goes silent, or says
/// its sound stopped, for longer than its time-out (half a second by default), it tells the X32 to recall
/// the "Console only" scene, so the X32 runs the room by itself. It does this once; the engineer returns the
/// room to NEX-Audio from the NEX-Audio screen at a quiet moment.
/// </summary>
public sealed class FailsafeWatcher : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Dictionary<string, Entry> _engines = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _check;
    private readonly Func<string, int, Task<bool>> _recall;

    private sealed class Entry
    {
        public Heartbeat Last = null!;
        public long LastTicks;
        public long NotFlowingSince;
        public int FiredEpoch = -1;
        public string? FiredReason;
        public DateTime? FiredUtc;
    }

    /// <param name="recall">Recalls a scene on the X32 at an address. The default sends the X32's own command.</param>
    public FailsafeWatcher(int port = Heartbeat.Port, Func<string, int, Task<bool>>? recall = null)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _recall = recall ?? X32Client.RecallSceneAsync;
        _ = Task.Run(Receive);
        _check = new Timer(_ => Check(), null, 50, 50);
    }

    public int Port { get; }

    /// <summary>Raised with a plain-language note when the watcher hands the room to the X32.</summary>
    public event Action<string>? Fired;
    public event Action? Changed;

    public IReadOnlyList<WatchedEngine> Engines
    {
        get
        {
            lock (_gate)
                return _engines.Values.Select(e => new WatchedEngine(e.Last, DateTime.UtcNow - Stopwatch.GetElapsedTime(e.LastTicks),
                    e.FiredEpoch == e.Last.Epoch, e.FiredReason, e.FiredUtc)).ToList();
        }
    }

    private async Task Receive()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception) { if (_stop.IsCancellationRequested) return; continue; }
            if (Heartbeat.Decode(r.Buffer) is not { } hb) continue;
            var changed = false;
            lock (_gate)
            {
                if (!_engines.TryGetValue(hb.Id, out var e))
                {
                    e = new Entry();
                    _engines[hb.Id] = e;
                    changed = true;
                }
                else if (e.Last.Armed != hb.Armed || e.Last.HandedOver != hb.HandedOver || e.Last.State != hb.State || e.Last.Epoch != hb.Epoch) changed = true;
                if (e.Last is not null && hb.Seq < e.Last.Seq && hb.Seq > 0 && e.Last.Seq - hb.Seq < 1000) continue; // an old packet
                e.Last = hb;
                e.LastTicks = Stopwatch.GetTimestamp();
                if (hb.Flowing) e.NotFlowingSince = 0;
                else if (e.NotFlowingSince == 0) e.NotFlowingSince = Stopwatch.GetTimestamp();
            }
            if (changed) Changed?.Invoke();
        }
    }

    private void Check()
    {
        List<(Entry e, string reason)> fire = [];
        lock (_gate)
        {
            foreach (var e in _engines.Values)
            {
                var hb = e.Last;
                if (!hb.Armed || hb.HandedOver || e.FiredEpoch == hb.Epoch || string.IsNullOrEmpty(hb.X32)) continue;
                var timeout = Math.Clamp(hb.TimeoutMs, 200, 5000);
                var silent = Stopwatch.GetElapsedTime(e.LastTicks).TotalMilliseconds;
                if (silent > timeout)
                    fire.Add((e, $"{hb.Pc} stopped answering for {silent / 1000:0.0} s"));
                else if (e.NotFlowingSince != 0 && Stopwatch.GetElapsedTime(e.NotFlowingSince).TotalMilliseconds > timeout)
                    fire.Add((e, $"{hb.Pc} reports no sound through the X32's USB card"));
                foreach (var (fe, _) in fire.Where(f => f.e == e)) fe.FiredEpoch = hb.Epoch;
            }
            // Forget PCs not heard from for an hour.
            foreach (var k in _engines.Where(kv => Stopwatch.GetElapsedTime(kv.Value.LastTicks) > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
                _engines.Remove(k);
        }
        foreach (var (e, reason) in fire) _ = HandOver(e, reason);
    }

    private async Task HandOver(Entry e, string reason)
    {
        var hb = e.Last;
        var ok = false;
        // Say it a few times: UDP can lose a packet, and recalling the same scene twice does no harm.
        for (var i = 0; i < 3; i++)
        {
            try { ok |= await _recall(hb.X32!, hb.ConsoleScene); }
            catch (Exception) { }
            await Task.Delay(60);
        }
        var note = $"Failsafe: {reason}. The X32 was told to recall scene {hb.ConsoleScene + 1} (\"Console only\") and now runs the room.";
        lock (_gate)
        {
            e.FiredReason = ok ? note : note + " The X32 did not answer: press the X32's failsafe button.";
            e.FiredUtc = DateTime.UtcNow;
        }
        Fired?.Invoke(e.FiredReason!);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _check.Dispose();
        _udp.Dispose();
    }
}
