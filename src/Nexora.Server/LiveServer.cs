using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nexora.Core.Bible;
using Nexora.Core.Live;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;
using Nexora.Core.Security;

namespace Nexora.Server;

public sealed class LiveServerOptions
{
    public int Port { get; set; } = Wire.DefaultPort;
    public int DiscoveryPort { get; set; } = Wire.DiscoveryPort;
    public bool Announce { get; set; } = true;
    public required Func<string> PairingCode { get; init; }
    public required Func<string> ServerName { get; init; }
    public string Version { get; init; } = "0.0.0";
    public string? LogsDir { get; init; }
    /// <summary>Master copies of imported Bibles (.nxbible), served to every PC. Null turns file sharing off.</summary>
    public string? BiblesDir { get; init; }
    /// <summary>Master copies of uploaded media (backgrounds), served to every PC.</summary>
    public string? MediaDir { get; init; }
    /// <summary>Where the nightly backups go (the last 30 are kept). Null turns them off.</summary>
    public string? BackupsDir { get; init; }
    /// <summary>NEXORA update packages the Live Output PC offers to every PC on the network.</summary>
    public string? UpdatesDir { get; init; }
    /// <summary>Speech models (Whisper) the Live Output PC shares with the Scripture PCs, so only one PC downloads them.</summary>
    public string? SpeechDir { get; init; }
    /// <summary>The Live Output PC's own settings file, included in backups so a terminal can take over.</summary>
    public string? ConfigFile { get; init; }
}

/// <summary>
/// The server built into the Live Output PC. Terminals connect over the church LAN;
/// commands are applied one at a time, in the order they arrive.
/// </summary>
public sealed partial class LiveServer : IAsyncDisposable
{
    private readonly LibraryStore _store;
    private readonly LiveServerOptions _options;
    private readonly LiveState _live = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _tokens = new(); // token -> userId
    private readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> _lockouts = new();
    private readonly Dictionary<string, string> _sourceOwner = new(); // source key -> session id
    private readonly ActivityLog _log;
    private WebApplication? _web;
    private DiscoveryAnnouncer? _announcer;

    public LiveServer(LibraryStore store, LiveServerOptions options)
    {
        _store = store;
        _options = options;
        _log = new ActivityLog(options.LogsDir);
        _live.SetScreens(store.Screens);
        if (store.Get<LiveState.Saved>("liveState") is { } saved) _live.Import(saved);
        // Sign-ins survive a Live Output restart, so terminals carry on without asking for PINs again.
        foreach (var (token, s) in store.Get<Dictionary<string, SavedSession>>("sessions") ?? [])
            if (DateTime.UtcNow - s.CreatedUtc < SessionLifetime) _tokens[token] = s.UserId;
    }

    private sealed record SavedSession(string UserId, DateTime CreatedUtc);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(2);
    private readonly ConcurrentDictionary<string, DateTime> _tokenCreated = new();

    private void SaveSessions()
    {
        try
        {
            _store.Set("sessions", _tokens.ToDictionary(kv => kv.Key,
                kv => new SavedSession(kv.Value, _tokenCreated.GetValueOrDefault(kv.Key, DateTime.UtcNow))));
        }
        catch (Exception ex) { _log.Write("Could not save sign-ins: " + ex.Message); }
    }

    public string ServerId => _store.ServerId;
    public int Port { get; private set; }

    /// <summary>Raised (on a background thread) whenever the live picture or routing changes.</summary>
    public event Action<LiveSnapshot>? Changed;

    public LiveSnapshot Snapshot()
    {
        lock (_gate) return Snap();
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Any, _options.Port);
            k.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        app.Run(HandleHttpAsync);

        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            throw new InvalidOperationException(
                $"Network port {_options.Port} is already in use on this PC. Is another copy of NEXORA running as Live Output?", ex);
        }

        _web = app;
        Port = _options.Port;

        if (_options.Announce)
        {
            _announcer = new DiscoveryAnnouncer(() => new Announcement(
                Announcement.AppTag, ServerId, _options.ServerName(), _store.Branding.ChurchName, Port, _options.Version),
                _options.DiscoveryPort);
        }
        _log.Write("Live Output server started on port " + Port);
        StartClock();
    }

    // ---------- HTTP / WebSocket ----------

    private async Task HandleHttpAsync(HttpContext ctx)
    {
        if (ctx.Request.Path == Wire.Path && ctx.WebSockets.IsWebSocketRequest)
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new Session(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
            _sessions[session.Id] = session;
            try
            {
                await RunSessionAsync(session, ctx.RequestAborted);
            }
            finally
            {
                _sessions.TryRemove(session.Id, out _);
                OnSessionClosed(session);
            }
            return;
        }

        if (await HandleMoreHttpAsync(ctx)) return;

        if (ctx.Request.Path.StartsWithSegments(Wire.LibraryPath, out var rest))
        {
            await HandleLibraryAsync(ctx, rest.Value ?? "");
            return;
        }

        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync($"NEXORA Live Output · {_options.ServerName()} · {_options.Version}");
    }

    // ---------- Library files (HTTP) ----------

    private string? LibraryDir(string kind) => kind switch
    {
        "bibles" => _options.BiblesDir,
        "media" => _options.MediaDir,
        _ => null,
    };

    private static bool SafeName(string name) =>
        name.Length is > 0 and <= 120 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_') && !name.StartsWith('.');

    private async Task HandleLibraryAsync(HttpContext ctx, string rest)
    {
        var parts = rest.Trim('/').Split('/');
        if (parts.Length != 2 || LibraryDir(parts[0]) is not { } dir || !SafeName(parts[1]))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var kind = parts[0];
        var name = parts[1];

        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            if (ctx.Request.Headers[Wire.CodeHeader].ToString().Trim() != _options.PairingCode().Trim())
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Response.ContentType = "application/octet-stream";
            await ctx.Response.SendFileAsync(path);
            return;
        }

        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        // Uploads need a signed-in user with the right role.
        var token = ctx.Request.Headers[Wire.TokenHeader].ToString();
        var user = _tokens.TryGetValue(token, out var uid) ? _store.User(uid) : null;
        var allowed = user is not null && !user.IsTrainee && (kind == "bibles"
            ? user.IsAdmin
            : user.IsAdmin || user.Roles.HasFlag(Role.Scripture) || user.Roles.HasFlag(Role.Lyrics) || user.Roles.HasFlag(Role.Announcements));
        if (!allowed)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Your role cannot add files to the library.");
            return;
        }
        if (kind == "bibles" && _live.Modes.ServiceMode)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync("Service mode is on. Add translations after the service.");
            return;
        }
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 200L * 1024 * 1024;

        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            await using (var fs = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(fs);
            string final;
            if (kind == "bibles")
            {
                var info = BibleFile.ReadInfo(tmp);
                if (info is null)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("That is not a NEXORA Bible file.");
                    return;
                }
                final = BibleImport.CleanAbbrev(info.Abbrev) + BibleFile.Extension;
                _log.Write($"{user!.Name} added the {info.Abbrev} Bible ({info.Name})");
            }
            else
            {
                // Media files are named by their content, so the same picture is stored once.
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ttf" or ".otf" or ".mp4" or ".mov"))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("That kind of file is not supported.");
                    return;
                }
                final = BibleImport.Hash(await File.ReadAllBytesAsync(tmp)) + ext;
                _log.Write($"{user!.Name} uploaded {name} as {final}");
            }
            File.Move(tmp, Path.Combine(dir, final), overwrite: true);
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync(final);
            _libraryCache = null;
            BroadcastLibrary();
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private LibraryInfo? _libraryCache;
    private readonly Dictionary<string, (DateTime Modified, long Size, string Hash)> _hashes = new();

    private LibraryInfo Library()
    {
        if (_libraryCache is { } cached) return cached;
        var bibles = new List<LibraryFile>();
        var media = new List<LibraryFile>();
        if (_options.BiblesDir is { } bd && Directory.Exists(bd))
            foreach (var f in Directory.GetFiles(bd, "*" + BibleFile.Extension))
                if (BibleFile.ReadInfo(f) is { } info)
                    bibles.Add(new LibraryFile(Path.GetFileName(f), HashOf(f), new FileInfo(f).Length, $"{info.Abbrev}|{info.Name}"));
        if (_options.MediaDir is { } md && Directory.Exists(md))
            foreach (var f in Directory.GetFiles(md).Where(f => !Path.GetFileName(f).StartsWith('.')))
                media.Add(new LibraryFile(Path.GetFileName(f), Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length, Path.GetFileName(f)));
        var lib = new LibraryInfo(_store.ScriptureThemes, _store.ScriptureSettings, bibles, media,
            _store.Themes(ThemeKind.Lyrics), _store.LyricsSettings, _store.SongsVersion, _store.SongList,
            _store.Themes(ThemeKind.Announcements), _store.Announcements, _store.People, _store.TodayPeople,
            _store.LowerThirdDefaults, _store.CountdownPresets, _store.StageLayout, _store.SermonPoints,
            OfferedVersion(), !string.IsNullOrWhiteSpace(_store.Broadcast.StreamUrl), _store.Ministers);
        _libraryCache = lib;
        return lib;
    }

    private string HashOf(string path)
    {
        var fi = new FileInfo(path);
        lock (_hashes)
        {
            if (_hashes.TryGetValue(path, out var h) && h.Modified == fi.LastWriteTimeUtc && h.Size == fi.Length) return h.Hash;
            var hash = BibleImport.Hash(File.ReadAllBytes(path));
            _hashes[path] = (fi.LastWriteTimeUtc, fi.Length, hash);
            return hash;
        }
    }

    private void BroadcastLibrary()
    {
        var json = Wire.Serialize(Library());
        foreach (var s in _sessions.Values.Where(x => x.Hello is not null)) s.SendRaw(json);
    }

    private async Task RunSessionAsync(Session s, CancellationToken aborted)
    {
        var sender = Task.Run(() => s.SendLoopAsync());
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (s.Socket.State == WebSocketState.Open && !aborted.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                using var silence = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                // Terminals ping every second. Ten seconds of silence means the link is gone.
                silence.CancelAfter(TimeSpan.FromSeconds(10));
                do
                {
                    r = await s.Socket.ReceiveAsync(buffer, silence.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 4 * 1024 * 1024) return;
                } while (!r.EndOfMessage);

                var msg = Wire.Deserialize(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                if (msg is not null) Handle(s, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            s.Complete();
            try { await sender; } catch { }
        }
    }

    // ---------- Message handling ----------

    private void Handle(Session s, Message msg)
    {
        if (msg is Ping p)
        {
            s.Send(new Pong(p.SentTicks));
            return;
        }

        if (s.Hello is null)
        {
            if (msg is Hello h) OnHello(s, h);
            else s.Send(new Rejected("Say hello first."));
            return;
        }

        switch (msg)
        {
            case Login l: OnLogin(s, l); return;
            case Resume r: OnResume(s, r); return;
            case Logout: OnLogout(s); return;
        }

        if (msg is EngineStatusMessage es)
        {
            if (s.Hello?.Kind != ClientKind.OutputEngine) return;
            _engine = es.Status;
            // Sent on its own (not in a snapshot) so every PC does not redraw every second.
            var json = Wire.Serialize(es);
            foreach (var x in _sessions.Values.Where(x => x.User is not null)) x.SendRaw(json);
            return;
        }

        if (s.User is null)
        {
            s.Send(new Notice("Please sign in first.", true));
            return;
        }

        switch (msg)
        {
            case OpenSections o: OnOpenSections(s, o); break;
            case SendContent c: OnSendContent(s, c); break;
            case ClearContent c: OnClearContent(s, c); break;
            case RouteCommand r: OnRoute(s, r); break;
            case ScreenCommand sc: OnScreen(s, sc); break;
            case SaveUser su: OnSaveUser(s, su); break;
            case DeleteUser du: OnDeleteUser(s, du); break;
            case SaveBranding sb: OnSaveBranding(s, sb); break;
            case SaveScreens ss: OnSaveScreens(s, ss); break;
            case SaveScriptureTheme st: OnSaveTheme(s, st); break;
            case DeleteScriptureTheme dt: OnDeleteTheme(s, dt); break;
            case SaveScriptureSettings ssx: OnSaveScriptureSettings(s, ssx); break;
            case RemoveBible rb: OnRemoveBible(s, rb); break;
            default: HandleMore(s, msg); break;
        }
    }

    private void OnHello(Session s, Hello h)
    {
        if (!string.Equals(h.PairingCode.Trim(), _options.PairingCode().Trim(), StringComparison.Ordinal))
        {
            _log.Write($"Refused {h.PcName} ({s.Address}): wrong pairing code");
            s.Send(new Rejected("Wrong pairing code. Check it in Settings on the Live Output PC."));
            s.Complete();
            return;
        }
        s.Hello = h;
        s.Send(new Welcome(ServerId, _options.ServerName(), _options.Version, s.Id));
        s.Send(PublicInfo());
        s.Send(Library());
        if (h.Kind == ClientKind.OutputEngine)
        {
            lock (_gate) s.Send(new SnapshotMessage(Snap()));
            s.Send(new EngineSettings(_store.Broadcast, _store.StageLayout));
        }
        _log.Write($"{h.Kind} {h.PcName} connected from {s.Address}");
        BroadcastSnapshot();
    }

    private void OnLogin(Session s, Login l)
    {
        var user = _store.User(l.UserId);
        if (user is null)
        {
            s.Send(new LoginResult(false, "That name is no longer on the list.", null, null));
            return;
        }
        if (_lockouts.TryGetValue(user.Id, out var lo) && lo.Until > DateTime.UtcNow)
        {
            var secs = (int)Math.Ceiling((lo.Until - DateTime.UtcNow).TotalSeconds);
            s.Send(new LoginResult(false, $"Too many wrong PINs. Try again in {secs} seconds.", null, null));
            return;
        }
        if (!_store.CheckPin(user.Id, l.Pin))
        {
            var fails = lo.Fails + 1;
            _lockouts[user.Id] = fails >= 5 ? (0, DateTime.UtcNow.AddSeconds(30)) : (fails, DateTime.MinValue);
            s.Send(new LoginResult(false, "That PIN is not right. Try again.", null, null));
            _log.Write($"Wrong PIN for {user.Name} on {s.Hello!.PcName}");
            return;
        }
        _lockouts.TryRemove(user.Id, out _);
        var token = PinHasher.NewToken();
        _tokens[token] = user.Id;
        _tokenCreated[token] = DateTime.UtcNow;
        SaveSessions();
        SignIn(s, user, token);
        _log.Write($"{user.Name} signed in on {s.Hello!.PcName}");
    }

    private void OnResume(Session s, Resume r)
    {
        if (_tokens.TryGetValue(r.Token, out var userId) && _store.User(userId) is { } user)
        {
            SignIn(s, user, r.Token);
            return;
        }
        s.Send(new LoginResult(false, "Please sign in again.", null, null));
    }

    private void SignIn(Session s, UserInfo user, string token)
    {
        s.User = user;
        s.Token = token;
        s.Send(new LoginResult(true, null, user, token));
        BroadcastSnapshot();
    }

    private void OnLogout(Session s)
    {
        if (s.Token is not null && _tokens.TryRemove(s.Token, out _)) SaveSessions();
        if (s.User is not null) _log.Write($"{s.User.Name} signed out on {s.Hello?.PcName}");
        lock (_gate)
        {
            ReleaseSources(s);
            s.User = null;
            s.Token = null;
            s.Sections = [];
        }
        BroadcastSnapshot();
    }

    private void OnOpenSections(Session s, OpenSections o)
    {
        var allowed = o.Sections.Distinct().Where(k => Sections.UserMayOpen(s.User!.Roles, k)).ToList();
        lock (_gate)
        {
            foreach (var key in s.Sections.Except(allowed).Select(k => Sections.SourceKey(s.Hello!.PcId, k)))
                if (_sourceOwner.GetValueOrDefault(key) == s.Id)
                {
                    _live.SetOffline(key);
                    _sourceOwner.Remove(key);
                }

            s.Sections = allowed;
            // Sections such as NEX-Audio work on their own PC and never feed a screen.
            foreach (var kind in allowed.Where(k => Sections.Get(k).ToScreens))
            {
                var src = _live.RegisterSource(s.Hello!.PcId, s.Hello.PcName, kind, s.User!.Name);
                _sourceOwner[src.Key] = s.Id;
            }
            Persist();
        }
        if (allowed.Count < o.Sections.Count) s.Send(new Notice("Some sections are not allowed for your role.", true));
        BroadcastSnapshot();
    }

    private void OnSendContent(Session s, SendContent c)
    {
        if (s.User!.IsTrainee)
        {
            s.Send(new Notice("Trainee accounts can practise, but nothing reaches the screens.", false));
            return;
        }
        if (!s.Sections.Contains(c.Section)) return;
        lock (_gate)
        {
            _live.SetContent(Sections.SourceKey(s.Hello!.PcId, c.Section), Sanitize(c.Content));
            Persist();
        }
        if (c.Content.Lyrics is { SongId.Length: > 0 } ly && _store.Song(ly.SongId) is { } song && _store.LogSongUse(song, DateTime.Now.ToString("yyyy-MM-dd")))
            _log.Write($"Song used: {song.Title} (CCLI {song.Ccli})");
        BroadcastSnapshot();
    }

    private void OnClearContent(Session s, ClearContent c)
    {
        if (!s.Sections.Contains(c.Section)) return;
        lock (_gate)
        {
            _live.SetContent(Sections.SourceKey(s.Hello!.PcId, c.Section), null);
            Persist();
        }
        BroadcastSnapshot();
    }

    private void OnRoute(Session s, RouteCommand r)
    {
        if (!s.User!.MayRunLiveOutput)
        {
            s.Send(new Notice("Only the Live Output operator can change what goes on screen.", true));
            return;
        }
        lock (_gate)
        {
            var src = _live.Source(r.SourceKey);
            var screen = _live.Screen(r.ScreenId);
            if (src is null || (screen is null && r.Action != RouteAction.Forget)) return;
            if (r.Action is RouteAction.Pin or RouteAction.Take && _sourceOwner.TryGetValue(r.SourceKey, out var ownerId)
                && _sessions.TryGetValue(ownerId, out var owner) && Outdated(owner))
            {
                s.Send(new Notice($"{owner.Hello!.PcName} runs an older NEXORA ({owner.Hello.AppVersion}). Update it from Settings → This PC before it goes live.", true));
                return;
            }
            switch (r.Action)
            {
                case RouteAction.Pin: _live.Pin(r.SourceKey, r.ScreenId); break;
                case RouteAction.TakeDown: _live.TakeDown(r.SourceKey, r.ScreenId); break;
                case RouteAction.Unroute: _live.Unroute(r.SourceKey, r.ScreenId); break;
                case RouteAction.Take: _live.Take(r.SourceKey, r.ScreenId); break;
                case RouteAction.Forget:
                    if (src.Online) return;
                    _live.ForgetSource(r.SourceKey);
                    break;
            }
            Persist();
            _log.Write($"{s.User.Name}: {r.Action} {src.Label} on {screen?.Name ?? "all screens"}");
        }
        BroadcastSnapshot();
    }

    private void OnScreen(Session s, ScreenCommand c)
    {
        if (!s.User!.MayRunLiveOutput) return;
        lock (_gate)
        {
            switch (c.Action)
            {
                case ScreenAction.Logo: _live.ToggleMaster(c.ScreenId, MasterMode.Logo); break;
                case ScreenAction.Black: _live.ToggleMaster(c.ScreenId, MasterMode.Black); break;
                case ScreenAction.Normal: _live.SetMaster(c.ScreenId, MasterMode.None); break;
                case ScreenAction.ClearText: _live.ClearText(c.ScreenId); break;
                case ScreenAction.ClearAll: _live.ClearAll(c.ScreenId); break;
            }
            Persist();
            _log.Write($"{s.User.Name}: {c.Action} on {_live.Screen(c.ScreenId)?.Name}");
        }
        BroadcastSnapshot();
    }

    private void OnSaveUser(Session s, SaveUser su)
    {
        if (!s.User!.IsAdmin) { s.Send(new Notice("Only an admin can change users.", true)); return; }
        if (Blocked(s)) return;
        var u = su.User;
        if (string.IsNullOrWhiteSpace(u.Name)) { s.Send(new Notice("Please type a name.", true)); return; }
        if (su.Pin is not null && !PinHasher.IsValidPin(su.Pin)) { s.Send(new Notice("A PIN is exactly 4 digits.", true)); return; }
        var existing = _store.User(u.Id);
        if (existing is null && su.Pin is null) { s.Send(new Notice("Please choose a 4-digit PIN for the new user.", true)); return; }
        if (existing is { IsAdmin: true } && !u.Roles.HasFlag(Role.Admin) && _store.Users().Count(x => x.IsAdmin) == 1)
        {
            s.Send(new Notice("NEXORA needs at least one admin. Make someone else admin first.", true));
            return;
        }
        var initials = string.IsNullOrWhiteSpace(u.Initials) ? UserInfo.MakeInitials(u.Name) : u.Initials;
        _store.SaveUser(u with { Initials = initials[..Math.Min(2, initials.Length)] }, su.Pin);
        _log.Write($"{s.User.Name} saved user {u.Name}");
        s.Send(new Notice($"Saved {u.Name}.", false));
        BroadcastPublicInfo();
    }

    private void OnDeleteUser(Session s, DeleteUser d)
    {
        if (!s.User!.IsAdmin || Blocked(s)) return;
        var target = _store.User(d.UserId);
        if (target is null) return;
        if (target.Id == s.User.Id) { s.Send(new Notice("You cannot remove yourself while signed in.", true)); return; }
        if (target.IsAdmin && _store.Users().Count(x => x.IsAdmin) == 1) { s.Send(new Notice("NEXORA needs at least one admin.", true)); return; }
        _store.DeleteUser(d.UserId);
        foreach (var (token, uid) in _tokens) if (uid == d.UserId) _tokens.TryRemove(token, out _);
        SaveSessions();
        _log.Write($"{s.User.Name} removed user {target.Name}");
        BroadcastPublicInfo();
    }

    private void OnSaveBranding(Session s, SaveBranding b)
    {
        if (!s.User!.IsAdmin || Blocked(s)) return;
        if (b.Branding.LogoPngBase64 is { Length: > 3_000_000 })
        {
            s.Send(new Notice("That logo file is too large. Please use one under 2 MB.", true));
            return;
        }
        _store.Branding = b.Branding;
        _log.Write($"{s.User.Name} changed church branding");
        BroadcastPublicInfo();
    }

    private void OnSaveScreens(Session s, SaveScreens ss)
    {
        if (!s.User!.IsAdmin || Blocked(s)) return;
        var screens = ss.Screens.Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Id).Select(g => g.First()).ToList();
        if (screens.Count == 0) { s.Send(new Notice("Keep at least one screen.", true)); return; }
        if (screens.Count > 6) { s.Send(new Notice("Up to 6 screens are supported.", true)); return; }
        lock (_gate)
        {
            _store.Screens = screens;
            _live.SetScreens(screens);
            Persist();
        }
        _log.Write($"{s.User.Name} changed screens");
        BroadcastSnapshot();
    }

    private static bool MayEditThemes(UserInfo u) => !u.IsTrainee && (u.IsAdmin || u.Roles.HasFlag(Role.Scripture));

    private void OnSaveTheme(Session s, SaveScriptureTheme m)
    {
        if (!MayEditThemes(s.User!)) { s.Send(new Notice("Your role cannot change Scripture themes.", true)); return; }
        var t = m.Theme;
        if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name)) { s.Send(new Notice("A theme needs a name.", true)); return; }
        var list = _store.ScriptureThemes.ToList();
        var i = list.FindIndex(x => x.Id == t.Id);
        if (i >= 0) list[i] = t; else list.Add(t);
        if (list.Count > 60) { s.Send(new Notice("Up to 60 themes are supported.", true)); return; }
        _store.ScriptureThemes = list;
        _libraryCache = null;
        _log.Write($"{s.User!.Name} saved the Scripture theme {t.Name}");
        BroadcastLibrary();
    }

    private void OnDeleteTheme(Session s, DeleteScriptureTheme m)
    {
        if (!MayEditThemes(s.User!)) return;
        var list = _store.ScriptureThemes.Where(x => x.Id != m.ThemeId).ToList();
        if (list.Count == 0) { s.Send(new Notice("Keep at least one theme.", true)); return; }
        _store.ScriptureThemes = list;
        var settings = _store.ScriptureSettings;
        if (settings.DefaultThemeId == m.ThemeId) _store.ScriptureSettings = settings with { DefaultThemeId = list[0].Id };
        _libraryCache = null;
        BroadcastLibrary();
    }

    private void OnSaveScriptureSettings(Session s, SaveScriptureSettings m)
    {
        if (!s.User!.IsAdmin) return;
        _store.ScriptureSettings = m.Settings;
        _libraryCache = null;
        _log.Write($"{s.User.Name} set the default translation to {m.Settings.DefaultTranslation}");
        BroadcastLibrary();
    }

    private void OnRemoveBible(Session s, RemoveBible m)
    {
        if (!s.User!.IsAdmin || _options.BiblesDir is not { } dir || Blocked(s)) return;
        var path = Path.Combine(dir, BibleImport.CleanAbbrev(m.Abbrev) + BibleFile.Extension);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            s.Send(new Notice("Could not remove it: " + ex.Message, true));
            return;
        }
        _libraryCache = null;
        _log.Write($"{s.User.Name} removed the {m.Abbrev} Bible");
        BroadcastLibrary();
    }

    // ---------- Sessions ----------

    private void OnSessionClosed(Session s)
    {
        if (s.Hello is null) return;
        lock (_gate)
        {
            ReleaseSources(s);
            Persist();
        }
        _log.Write($"{s.Hello.PcName} disconnected");
        BroadcastSnapshot();
    }

    /// <summary>Marks this session's sections offline. Their content stays on screen.</summary>
    private void ReleaseSources(Session s)
    {
        if (s.Hello is null) return;
        foreach (var k in s.Sections)
        {
            var key = Sections.SourceKey(s.Hello.PcId, k);
            if (_sourceOwner.GetValueOrDefault(key) != s.Id) continue;
            _sourceOwner.Remove(key);
            _live.SetOffline(key);
        }
    }

    private IReadOnlyList<ClientInfo> Clients() => _sessions.Values
        .Where(x => x.Hello is not null)
        .OrderBy(x => x.Hello!.PcName)
        .Select(x => new ClientInfo(x.Id, x.Hello!.PcId, x.Hello.PcName, x.Hello.Kind, x.User?.Name, x.Address, x.Hello.AppVersion, x.Sections))
        .ToList();

    private PublicInfo PublicInfo() => new(_options.ServerName(), _store.Branding, _store.Users());

    private void BroadcastPublicInfo()
    {
        var json = Wire.Serialize(PublicInfo());
        foreach (var s in _sessions.Values.Where(x => x.Hello is not null)) s.SendRaw(json);
    }

    private void BroadcastSnapshot()
    {
        LiveSnapshot snap;
        lock (_gate) snap = Snap();
        var json = Wire.Serialize(new SnapshotMessage(snap));
        foreach (var s in _sessions.Values)
            if (s.User is not null || s.Hello?.Kind == ClientKind.OutputEngine)
                s.SendRaw(json);
        try { Changed?.Invoke(snap); } catch { }
    }

    private void Persist()
    {
        try { _store.Set("liveState", _live.Export()); }
        catch (Exception ex) { _log.Write("Could not save live state: " + ex.Message); }
    }

    private static SlideContent Sanitize(SlideContent c)
    {
        var now = DateTime.UtcNow;
        var countdown = c.Countdown;
        if (countdown is { Running: true, EndsUtc: { } given } && Math.Abs((given - now).TotalHours) < 24)
        {
            // Already running on this PC's clock (the terminal sent back the end time it was given): keep it exact.
        }
        else if (countdown is { Running: true })
        {
            // The timer runs on this PC's clock from now on.
            var ends = countdown.Mode == CountdownMode.ToTime && CountdownShow.NextLocal(countdown.ToTime, DateTime.Now) is { } local
                ? local.ToUniversalTime()
                : now.AddSeconds(countdown.RemainingSeconds);
            countdown = countdown with { EndsUtc = ends };
        }
        var minister = c.Minister;
        if (minister is { Running: true, EndsUtc: { } mEnds } && Math.Abs((mEnds - now).TotalHours) < 24)
        {
            // Already running on this PC's clock.
        }
        else if (minister is { Running: true })
            minister = minister with { EndsUtc = now.AddSeconds(minister.RemainingSeconds) };
        if (minister is not null)
            minister = minister with
            {
                Name = Trim(minister.Name, 120)!, Purpose = Trim(minister.Purpose, 120)!,
                AllowedSeconds = Math.Clamp(minister.AllowedSeconds, 0, 6 * 3600),
            };
        var lyrics = c.Lyrics is { } ly && ly.Lines.Count <= 40 ? ly with { Lines = ly.Lines.Select(l => Trim(l, 400)!).ToList() } : null;
        return new SlideContent(
            Trim(c.Text, 6000) ?? "", Trim(c.Caption, 200), Trim(c.Label, 120),
            c.Scripture is { } sc && sc.Columns.Count <= 3 && sc.Columns.All(col => col.Verses.Count <= 200) ? sc : null,
            lyrics,
            Trim(c.Next, 1000),
            c.Logo,
            c.Announcement is { } an && an.Slides.Count <= 200 ? an with { StartUtc = now } : null,
            c.LowerThird is { } lt ? lt with { StartUtc = now, Name = Trim(lt.Name, 200)!, Title = Trim(lt.Title, 300)! } : null,
            countdown,
            c.Points is { } pts && pts.Points.Count <= 100 ? pts : null,
            null, // media comes only from the Live Output operator
            c.Camera,
            minister);
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public async ValueTask DisposeAsync()
    {
        _clock?.Dispose();
        if (_announcer is not null) await _announcer.DisposeAsync();
        foreach (var s in _sessions.Values)
        {
            try { s.Socket.Abort(); } catch { }
        }
        if (_web is not null) await _web.DisposeAsync();
        _log.Write("Live Output server stopped");
    }

    private sealed class Session(WebSocket socket, string address)
    {
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
        public WebSocket Socket { get; } = socket;
        public string Address { get; } = address;
        public Hello? Hello { get; set; }
        public UserInfo? User { get; set; }
        public string? Token { get; set; }
        public IReadOnlyList<SectionKind> Sections { get; set; } = [];

        public void Send(Message m) => SendRaw(Wire.Serialize(m));
        public void SendRaw(string json) => _out.Writer.TryWrite(json);
        public void Complete() => _out.Writer.TryComplete();

        public async Task SendLoopAsync()
        {
            await foreach (var json in _out.Reader.ReadAllAsync())
            {
                if (Socket.State != WebSocketState.Open) break;
                try
                {
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    break;
                }
            }
            if (Socket.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
                }
                catch { }
            }
        }
    }
}
