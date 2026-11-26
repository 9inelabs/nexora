using System.Net;
using System.Net.Sockets;

namespace Nexora.Audio.X32;

/// <summary>What the X32 says about itself.</summary>
public sealed record X32Info(string Address, string Name, string Model, string Firmware);

/// <summary>One X32 input channel as the console names it.</summary>
public sealed record X32Channel(int Number, string Name, int Colour)
{
    /// <summary>The name to show: the X32's own name, or "Ch 5" when the channel has none.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"Ch {Number}" : Name;

    /// <summary>The X32's 16 scribble-strip colours (0 to 7 normal, 8 to 15 the same colours inverted), as hex.</summary>
    public string ColourHex => (Colour & 7) switch
    {
        1 => "#E0574F",
        2 => "#45B36B",
        3 => "#E2C24E",
        4 => "#4F86E0",
        5 => "#C45BD6",
        6 => "#4FC8D8",
        7 => "#E8E8E8",
        _ => "#4A4D54",
    };
}

/// <summary>
/// Talks to the X32 over the network (OSC on UDP port 10023): who it is, and the names and colours of
/// its 32 input channels, kept up to date as engineers rename channels on the desk.
/// Phase 1 only reads from the console. Nothing here changes the X32.
/// </summary>
public sealed class X32Client : IDisposable
{
    public const int DefaultPort = 10023;
    public const int ChannelCount = 32;

    private readonly int _port;
    private readonly object _gate = new();
    private UdpClient? _udp;
    private IPEndPoint? _target;
    private CancellationTokenSource? _run;
    private DateTime _lastHeard = DateTime.MinValue;
    private readonly X32Channel[] _channels = Enumerable.Range(1, ChannelCount).Select(i => new X32Channel(i, "", 0)).ToArray();

    public X32Client(int port = DefaultPort) => _port = port;

    public X32Info? Info { get; private set; }
    public bool Connected => Info is not null && DateTime.UtcNow - _lastHeard < TimeSpan.FromSeconds(10);
    public string? Address => _target?.Address.ToString();

    public IReadOnlyList<X32Channel> Channels
    {
        get { lock (_gate) return [.. _channels]; }
    }

    /// <summary>Raised (on a background thread) when the link or a channel name changes.</summary>
    public event Action? Changed;

    /// <summary>Connects to the X32 at this address. Returns its details, or null when nothing answered.</summary>
    public async Task<X32Info?> ConnectAsync(string host, CancellationToken ct = default)
    {
        Disconnect();
        var ip = IPAddress.TryParse(host.Trim(), out var a) ? a : (await Dns.GetHostAddressesAsync(host.Trim(), ct)).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        if (ip is null) return null;
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _udp = udp;
        _target = new IPEndPoint(ip, _port);
        var run = new CancellationTokenSource();
        _run = run;
        _ = Task.Run(() => Receive(udp, run.Token));

        for (var attempt = 0; attempt < 3 && Info is null && !ct.IsCancellationRequested; attempt++)
        {
            await SendAsync(new OscMessage("/xinfo"));
            for (var i = 0; i < 10 && Info is null; i++) await Task.Delay(100, ct);
        }
        if (Info is null)
        {
            Disconnect();
            return null;
        }
        await RequestChannelsAsync();
        _ = Task.Run(() => KeepAlive(run.Token));
        return Info;
    }

    public void Disconnect()
    {
        _run?.Cancel();
        _run = null;
        try { _udp?.Dispose(); }
        catch (Exception) { }
        _udp = null;
        var had = Info is not null;
        Info = null;
        if (had) Changed?.Invoke();
    }

    /// <summary>Asks the X32 for every channel's name and colour again.</summary>
    public async Task RequestChannelsAsync()
    {
        for (var ch = 1; ch <= ChannelCount; ch++)
        {
            await SendAsync(new OscMessage($"/ch/{ch:00}/config/name"));
            await SendAsync(new OscMessage($"/ch/{ch:00}/config/color"));
        }
    }

    private async Task SendAsync(OscMessage msg)
    {
        var udp = _udp;
        var target = _target;
        if (udp is null || target is null) return;
        try
        {
            var bytes = msg.Encode();
            await udp.SendAsync(bytes, bytes.Length, target);
        }
        catch (Exception) { }
    }

    private async Task KeepAlive(CancellationToken ct)
    {
        var wasConnected = true;
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            // /xremote asks the X32 to tell us about changes for the next 10 seconds.
            await SendAsync(new OscMessage("/xremote"));
            await SendAsync(new OscMessage("/xinfo"));
            if (++n % 15 == 0) await RequestChannelsAsync(); // a full refresh about once a minute
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); }
            catch (OperationCanceledException) { return; }
            var now = Connected;
            if (now != wasConnected)
            {
                wasConnected = now;
                Changed?.Invoke();
                if (now) await RequestChannelsAsync();
            }
        }
    }

    private async Task Receive(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (Exception) { return; }
            if (OscMessage.Decode(r.Buffer) is { } msg) Handle(msg);
        }
    }

    internal void Handle(OscMessage msg)
    {
        _lastHeard = DateTime.UtcNow;
        if (msg.Address == "/xinfo" && msg.Args.Count >= 4)
        {
            var first = Info is null;
            Info = new X32Info(msg.String(0) ?? "", msg.String(1) ?? "", msg.String(2) ?? "", msg.String(3) ?? "");
            if (first) Changed?.Invoke();
            return;
        }
        // /ch/05/config/name "Kick"  and  /ch/05/config/color 1
        var parts = msg.Address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && parts[0] == "ch" && parts[2] == "config" && int.TryParse(parts[1], out var ch) && ch is >= 1 and <= ChannelCount)
        {
            lock (_gate)
            {
                var c = _channels[ch - 1];
                if (parts[3] == "name" && msg.String(0) is { } name) _channels[ch - 1] = c with { Name = name.Trim() };
                else if (parts[3] == "color" && msg.Int(0) is { } colour) _channels[ch - 1] = c with { Colour = colour };
                else return;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Looks for X32s on the church network (they answer a broadcast). Takes about 1.5 seconds.</summary>
    public static async Task<IReadOnlyList<X32Info>> DiscoverAsync(int port = DefaultPort, CancellationToken ct = default)
    {
        var found = new Dictionary<string, X32Info>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var probe = new OscMessage("/xinfo").Encode();
        try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, port)); }
        catch (Exception) { return []; }
        var until = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(until - DateTime.UtcNow);
            try
            {
                var r = await udp.ReceiveAsync(wait.Token);
                if (OscMessage.Decode(r.Buffer) is { Address: "/xinfo" } m && m.Args.Count >= 4)
                    found[r.RemoteEndPoint.Address.ToString()] = new X32Info(r.RemoteEndPoint.Address.ToString(), m.String(1) ?? "", m.String(2) ?? "", m.String(3) ?? "");
            }
            catch (Exception) { break; }
        }
        return [.. found.Values];
    }

    public void Dispose() => Disconnect();
}
