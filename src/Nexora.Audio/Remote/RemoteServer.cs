using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Remote;

/// <summary>What the remote needs from the desk.</summary>
public interface IRemoteDesk
{
    MixState MixNow();
    /// <summary>Goes up on every change to the mix.</summary>
    long Rev { get; }
    RemoteSettings Remote { get; }
    /// <summary>Each strip's level after its fader, 0 to 1.</summary>
    IReadOnlyDictionary<string, float> Peaks { get; }
    bool Set(string path, JsonElement value);
    /// <summary>The name of the next cue, or null.</summary>
    string? NextCueName { get; }
    bool GoCue(string? id = null);
}

/// <summary>A tablet or phone on the remote just now.</summary>
public sealed record RemoteClient(string Address, RemoteRole Role, string? Mix, DateTime SinceUtc);

/// <summary>
/// The tablet and phone remote: a small web page served by the sound engine on the church network
/// (http://this-pc:7410). Musicians open it on their phones and turn up what they need in their own
/// wedge or in-ear mix; the engineer, with the PIN, gets every fader, mute and send while walking the room.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private readonly IRemoteDesk _desk;
    private readonly Action<string> _log;
    private readonly RemoteAccess.PinGuard _pins = new();
    private readonly Dictionary<int, Session> _sessions = [];
    private WebApplication? _web;
    private CancellationTokenSource? _cts;
    private int _next;

    public RemoteServer(IRemoteDesk desk, Action<string>? log = null)
    {
        _desk = desk;
        _log = log ?? (_ => { });
    }

    public int Port { get; private set; }
    public bool Running => _web is not null;
    public string? Error { get; private set; }

    public IReadOnlyList<RemoteClient> Clients
    {
        get { lock (_sessions) return _sessions.Values.Select(s => new RemoteClient(s.Address, s.Role, s.Mix, s.Since)).ToList(); }
    }

    /// <summary>Starts listening on every network card. False (and <see cref="Error"/>) if the port is taken.</summary>
    public async Task<bool> StartAsync(int port)
    {
        await StopAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Any, port);
            k.Limits.MaxRequestBodySize = 64 * 1024;
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        app.Run(HandleAsync);
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            Error = $"Network port {port} is already in use on this PC, so the remote cannot start. Pick another port. ({ex.Message})";
            _log("Remote: " + Error);
            return false;
        }
        _web = app;
        Port = port;
        Error = null;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PushLoop(_cts.Token));
        _log($"Remote: listening on port {port}.");
        return true;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        Session[] all;
        lock (_sessions) all = [.. _sessions.Values];
        foreach (var s in all) s.Abort();
        if (_web is { } w)
        {
            _web = null;
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await w.StopAsync(stop.Token); } catch { }
            await w.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/ws" && ctx.WebSockets.IsWebSocketRequest)
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var s = new Session(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
            lock (_sessions) _sessions[s.Id = ++_next] = s;
            try
            {
                await s.SendAsync(Welcome(s));
                await ReceiveLoop(s);
            }
            catch (Exception) { }
            finally
            {
                lock (_sessions) _sessions.Remove(s.Id);
            }
            return;
        }
        if (path is "/" or "/index.html")
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsync(RemotePage.Html);
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    private async Task ReceiveLoop(Session s)
    {
        var buffer = new byte[16 * 1024];
        while (s.Ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await s.Ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 64 * 1024) return;
            } while (!r.EndOfMessage);
            JsonNode? msg;
            try { msg = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); }
            catch (JsonException) { continue; }
            if (msg is JsonObject o) await Handle(s, o);
        }
    }

    /// <summary>One message from a tablet or phone.</summary>
    private async Task Handle(Session s, JsonObject m)
    {
        var settings = _desk.Remote;
        switch ((string?)m["t"])
        {
            case "pin":
            {
                var typed = (string?)m["pin"] ?? "";
                if (_pins.Check(s.Address, typed, settings.Pin, DateTime.UtcNow))
                {
                    s.Role = RemoteRole.Engineer;
                    s.Mix = null;
                    _log($"Remote: the engineer's remote signed in from {s.Address}.");
                }
                else
                {
                    await s.SendAsync(new JsonObject
                    {
                        ["t"] = "error",
                        ["text"] = _pins.Blocked(s.Address, DateTime.UtcNow) ? "Too many wrong PINs. Wait a minute and try again." : "That PIN is not right.",
                    });
                    return;
                }
                break;
            }
            case "pick":
            {
                var id = (string?)m["mix"];
                if (id is null || !settings.Mixes.Contains(id)) return;
                s.Role = RemoteRole.Musician;
                s.Mix = id;
                break;
            }
            case "leave":
                s.Role = RemoteRole.None;
                s.Mix = null;
                break;
            case "set":
            {
                var path = (string?)m["path"];
                if (path is null || m["value"] is not { } v) return;
                var mix = _desk.MixNow();
                if (!RemoteAccess.MayChange(s.Role, s.Mix, path, mix, settings))
                {
                    await s.SendAsync(new JsonObject { ["t"] = "error", ["text"] = "The remote cannot change that." });
                    return;
                }
                _desk.Set(path, JsonSerializer.SerializeToElement(v));
                return;
            }
            case "go":
                if (s.Role == RemoteRole.Engineer) _desk.GoCue();
                return;
            default:
                return;
        }
        s.SentRev = -1;
        await s.SendAsync(Welcome(s));
    }

    private JsonObject Welcome(Session s)
    {
        var mix = _desk.MixNow();
        var settings = _desk.Remote;
        var choices = new JsonArray();
        foreach (var m in RemoteAccess.MusicianMixes(mix, settings)) choices.Add(new JsonObject { ["id"] = m.Id, ["name"] = MixState.Label(m) });
        return new JsonObject
        {
            ["t"] = "hello",
            ["role"] = s.Role.ToString().ToLowerInvariant(),
            ["mixes"] = choices,
            ["engineer"] = settings.Pin.Length >= 4,
        };
    }

    /// <summary>The whole picture for one remote: its strips, their levels and (for a musician) their sends.</summary>
    public static JsonObject State(RemoteRole role, string? mixId, MixState mix, string? nextCue)
    {
        var strips = new JsonArray();
        var bus = mixId is null ? null : mix.Find(mixId);
        foreach (var st in RemoteAccess.Strips(role, mixId, mix))
        {
            var o = new JsonObject
            {
                ["id"] = st.Id,
                ["name"] = MixState.Label(st),
                ["kind"] = st.Kind.ToString().ToLowerInvariant(),
                ["colour"] = st.Colour,
            };
            if (role == RemoteRole.Musician && bus is not null)
            {
                var send = st.SendTo(bus.Id);
                o["level"] = send?.Level ?? Dsp.Db.Floor;
                o["on"] = send?.On ?? false;
            }
            else
            {
                o["level"] = st.Fader;
                o["on"] = !st.Mute;
                o["solo"] = st.Solo;
                o["pan"] = st.Pan;
                o["hard"] = st.HardMute;
            }
            strips.Add(o);
        }
        var state = new JsonObject { ["t"] = "state", ["role"] = role.ToString().ToLowerInvariant(), ["strips"] = strips };
        if (bus is not null)
        {
            state["mix"] = bus.Id;
            state["mixName"] = MixState.Label(bus);
            state["master"] = bus.Fader;
        }
        if (role == RemoteRole.Engineer) state["next"] = nextCue;
        return state;
    }

    private async Task PushLoop(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return; }
            Session[] all;
            lock (_sessions) all = [.. _sessions.Values];
            if (all.Length == 0) continue;
            tick++;
            var rev = _desk.Rev;
            MixState? mix = null;
            var peaks = _desk.Peaks;
            var settings = _desk.Remote;
            foreach (var s in all.Where(x => x.Role != RemoteRole.None))
            {
                try
                {
                    // The engineer took this mix off the remote, or switched the PIN off: back to the start.
                    if (s.Role == RemoteRole.Musician && (s.Mix is null || !settings.Mixes.Contains(s.Mix)) || s.Role == RemoteRole.Engineer && settings.Pin.Length < 4)
                    {
                        s.Role = RemoteRole.None;
                        s.Mix = null;
                        await s.SendAsync(Welcome(s));
                        continue;
                    }
                    // The state when it changed (and every few seconds anyway), the meters ten times a second.
                    if (s.SentRev != rev || tick % 30 == 0)
                    {
                        mix ??= _desk.MixNow();
                        s.Order = RemoteAccess.Strips(s.Role, s.Mix, mix).Select(x => x.Id).ToArray();
                        s.SentRev = rev;
                        await s.SendAsync(State(s.Role, s.Mix, mix, _desk.NextCueName));
                    }
                    var meters = new JsonArray();
                    foreach (var id in s.Order) meters.Add(Math.Round(peaks.GetValueOrDefault(id), 3));
                    if (s.Mix is { } mixId) meters.Add(Math.Round(peaks.GetValueOrDefault(mixId), 3));
                    await s.SendAsync(new JsonObject { ["t"] = "meters", ["v"] = meters });
                }
                catch (Exception) { s.Abort(); }
            }
        }
    }

    private sealed class Session(WebSocket ws, string address)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        public int Id;
        public WebSocket Ws { get; } = ws;
        public string Address { get; } = address;
        public DateTime Since { get; } = DateTime.UtcNow;
        public RemoteRole Role = RemoteRole.None;
        public string? Mix;
        public long SentRev = -1;
        public string[] Order = [];

        public async Task SendAsync(JsonObject msg)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            if (!await _write.WaitAsync(1000)) return;
            try
            {
                if (Ws.State == WebSocketState.Open) await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { _write.Release(); }
        }

        public void Abort()
        {
            try { Ws.Abort(); } catch { }
        }
    }
}
