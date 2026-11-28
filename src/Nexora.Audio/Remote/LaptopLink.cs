using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexora.Audio.Remote;

/// <summary>A laptop that is running the whole desk from somewhere else on the church network.</summary>
public sealed record LaptopClient(int Id, string Address, string Name, string User, string Version, DateTime SinceUtc);

/// <summary>A sound PC that answered "who is there?" on the church network.</summary>
public sealed record FoundDesk(string Address, int Port, string Name, string Version);

/// <summary>
/// The admin remote: NEX-Audio on another laptop runs this sound PC's whole desk, exactly as if it sat at it.
/// The laptop speaks the same messages as the screen on the sound PC, over the church network instead of a
/// local pipe. Before anything is sent either way, the laptop proves it knows the laptop PIN: the sound PC sends
/// a random number, the laptop answers with that number signed with the PIN (so the PIN itself never travels).
/// </summary>
public static class LaptopLink
{
    public const int DefaultPort = 7411;
    /// <summary>What a laptop sends on the network to find sound PCs (on the same port, by UDP).</summary>
    public const string FindWord = "NEX-AUDIO-DESK?";
    public const string AnswerWord = "NEX-AUDIO-DESK";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string NewChallenge() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The answer to a challenge: HMAC-SHA256 of the challenge, keyed with the PIN.</summary>
    public static string Proof(string pin, string challenge) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("nex-audio:" + pin), Encoding.UTF8.GetBytes(challenge)));

    public static bool Check(string pin, string challenge, string? proof) =>
        pin.Length >= 4 && proof is { Length: 64 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Proof(pin, challenge)), Encoding.ASCII.GetBytes(proof.ToUpperInvariant()));

    /// <summary>Only the first two parts of the version need to match (0.1.x talks to 0.1.y), but a warning shows when they differ.</summary>
    public static bool SameFamily(string a, string b)
    {
        static string Head(string v) => string.Join('.', v.Split('+')[0].Split('.').Take(2));
        return Head(a) == Head(b);
    }

    /// <summary>A short, safe file name for something a laptop sent (no folders, no odd characters).</summary>
    public static string SafeName(string? name, string fallback)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        n = new string(n.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')').ToArray()).Trim(' ', '.');
        return n.Length == 0 ? fallback : n.Length > 80 ? n[..80] : n;
    }

    /// <summary>The sound PC's side of "who is there?": answers every laptop that asks, until stopped.</summary>
    public sealed class Answerer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _stop = new();

        public Answerer(int port, Func<FoundDesk> me)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _udp.ReceiveAsync(_stop.Token);
                        if (Encoding.UTF8.GetString(r.Buffer) != FindWord) continue;
                        var answer = JsonSerializer.SerializeToUtf8Bytes(new { w = AnswerWord, d = me() }, Web);
                        await _udp.SendAsync(answer, r.RemoteEndPoint, _stop.Token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { await Task.Delay(200); }
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }
    }

    /// <summary>The laptop's side: asks the whole network, and lists every sound PC that answers within the time.</summary>
    public static async Task<IReadOnlyList<FoundDesk>> FindAsync(int port = DefaultPort, int waitMs = 1500)
    {
        var found = new Dictionary<string, FoundDesk>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var ask = Encoding.UTF8.GetBytes(FindWord);
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var a in Engine.Desk.LanAddresses())
            if (IPAddress.TryParse(a, out var ip))
            {
                // The subnet's own broadcast address too (some routers drop 255.255.255.255).
                var b = ip.GetAddressBytes();
                b[3] = 255;
                targets.Add(new IPAddress(b));
            }
        foreach (var t in targets.Distinct())
        {
            try { await udp.SendAsync(ask, new IPEndPoint(t, port)); }
            catch (Exception) { }
        }
        using var cts = new CancellationTokenSource(waitMs);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                using var doc = JsonDocument.Parse(r.Buffer);
                if (doc.RootElement.GetProperty("w").GetString() != AnswerWord) continue;
                var d = doc.RootElement.GetProperty("d");
                var desk = new FoundDesk(r.RemoteEndPoint.Address.ToString(), d.GetProperty("port").GetInt32(), d.GetProperty("name").GetString() ?? "", d.GetProperty("version").GetString() ?? "");
                // The same PC answering on loopback and on its network address: keep the network one.
                var key = desk.Name + ":" + desk.Port;
                if (!found.ContainsKey(key) || found[key].Address == "127.0.0.1") found[key] = desk;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { }
        }
        return found.Values.OrderBy(d => d.Name).ToList();
    }
}
