using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nexora.Core.Protocol;

namespace Nexora.Core.Net;

/// <summary>What the Live Output PC shouts on the LAN every second.</summary>
public sealed record Announcement(string App, string ServerId, string ServerName, string ChurchName, int Port, string Version)
{
    public const string AppTag = "NEXORA";

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Wire.Options));

    public static Announcement? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            var a = JsonSerializer.Deserialize<Announcement>(data, Wire.Options);
            return a is { App: AppTag } && !string.IsNullOrEmpty(a.ServerId) ? a : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DiscoveredServer(Announcement Info, IPAddress Address, DateTime LastSeenUtc)
{
    public Uri Url => new UriBuilder("ws", Address.ToString(), Info.Port, Wire.Path).Uri;
}

/// <summary>Sends the announcement to every network adapter's broadcast address.</summary>
public sealed class DiscoveryAnnouncer : IAsyncDisposable
{
    private readonly Func<Announcement> _announcement;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public DiscoveryAnnouncer(Func<Announcement> announcement, int port = Wire.DiscoveryPort)
    {
        _announcement = announcement;
        _port = port;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var bytes = _announcement().ToBytes();
                foreach (var target in BroadcastTargets())
                {
                    try { await udp.SendAsync(bytes, new IPEndPoint(target, _port), _cts.Token); }
                    catch (SocketException) { /* adapter down; try the others */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* never let discovery stop the server */ }

            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    set.Add(new IPAddress(bc));
                }
            }
        }
        catch (NetworkInformationException) { }
        return set;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}

/// <summary>Listens for Live Output PCs on the LAN. No addresses to type.</summary>
public sealed class DiscoveryListener : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredServer> _servers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly int _port;

    public event Action? Changed;

    public DiscoveryListener(int port = Wire.DiscoveryPort)
    {
        _port = port;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Servers heard from in the last 5 seconds.</summary>
    public IReadOnlyList<DiscoveredServer> Servers =>
        _servers.Values.Where(s => DateTime.UtcNow - s.LastSeenUtc < TimeSpan.FromSeconds(5))
            .OrderBy(s => s.Info.ServerName).ToList();

    public string? Error { get; private set; }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                // Several NEXORA windows on one PC (the Live Output app and its output engine) can all listen.
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
                Error = null;
                while (!_cts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(_cts.Token);
                    if (Announcement.Parse(result.Buffer) is not { } a) continue;
                    var isNew = !_servers.ContainsKey(a.ServerId);
                    var address = result.RemoteEndPoint.Address;
                    // Prefer a real LAN address over loopback when both arrive.
                    if (_servers.TryGetValue(a.ServerId, out var old) && IPAddress.IsLoopback(address) && !IPAddress.IsLoopback(old.Address)
                        && DateTime.UtcNow - old.LastSeenUtc < TimeSpan.FromSeconds(3))
                        address = old.Address;
                    _servers[a.ServerId] = new DiscoveredServer(a, address, DateTime.UtcNow);
                    if (isNew) Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Error = ex.Message;
                try { await Task.Delay(2000, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public DiscoveredServer? Find(string? serverId) =>
        serverId is null ? null : Servers.FirstOrDefault(s => s.Info.ServerId == serverId);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
