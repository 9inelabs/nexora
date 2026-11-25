using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nexora.Core.Live;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.Server;

/// <summary>Songs, the other sections, media, cameras, the output engine, backups and updates.</summary>
public sealed partial class LiveServer
{
    private Timer? _clock;
    private EngineStatus? _engine;
    private DateTime _lastBackupCheck = DateTime.MinValue;

    /// <summary>Raised when an admin uploaded a backup to restore: the app restarts its server to apply it.</summary>
    public event Action<string>? RestoreRequested;

    private LiveSnapshot Snap() => _live.Snapshot(Clients()) with { Engine = _engine };

    // ---------------- Clock ----------------

    private void StartClock()
    {
        _live.SetModes(_store.Modes with { Rehearsal = false });
        _clock = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    private void Tick()
    {
        try
        {
            IReadOnlyList<LiveState.ClockAction> actions;
            bool changed;
            lock (_gate)
            {
                var before = _live.Version;
                actions = _live.Tick(DateTime.UtcNow);
                foreach (var a in actions)
                {
                    switch (a.Action)
                    {
                        case ZeroAction.ShowLogo:
                            foreach (var sc in _live.Screens.Where(x => x.Kind == ScreenKind.Audience)) _live.SetMaster(sc.Id, MasterMode.Logo);
                            break;
                        case ZeroAction.StopAnnouncements:
                            _live.ClearSourceEverywhere(SectionKind.Announcements);
                            break;
                    }
                    _log.Write($"Countdown reached zero: {a.Action}");
                }
                changed = _live.Version != before;
                if (changed) Persist();
            }
            if (changed) BroadcastSnapshot();
            NightlyBackup();
        }
        catch (Exception ex)
        {
            _log.Write("Clock: " + ex.Message);
        }
    }

    // ---------------- Messages ----------------

    private void HandleMore(Session s, Message msg)
    {
        switch (msg)
        {
            case SaveSongs m: OnSaveSongs(s, m); break;
            case DeleteSongs m: OnDeleteSongs(s, m); break;
            case SaveSongList m: OnSaveSongList(s, m); break;
            case LogSongUse m:
                if (_store.Song(m.SongId) is { } song) _store.LogSongUse(song, DateTime.Now.ToString("yyyy-MM-dd"));
                break;
            case SaveTheme m: OnSaveSectionTheme(s, m); break;
            case DeleteTheme m: OnDeleteSectionTheme(s, m); break;
            case SaveLyricsSettings m:
                if (!s.User!.IsAdmin && !s.User.Roles.HasFlag(Role.Lyrics)) return;
                _store.LyricsSettings = m.Settings;
                LibraryChanged();
                break;
            case SaveAnnouncements m:
                if (!MayOperate(s.User!, Role.Announcements)) { Deny(s); return; }
                _store.Announcements = m.Slides.Take(300).ToList();
                LibraryChanged();
                break;
            case SavePeople m:
                if (!MayOperate(s.User!, Role.LowerThirds)) { Deny(s); return; }
                _store.People = m.People.Take(500).ToList();
                _store.TodayPeople = m.Today.Take(40).ToList();
                _store.LowerThirdDefaults = m.Defaults;
                LibraryChanged();
                break;
            case SaveCountdownPresets m:
                if (!MayOperate(s.User!, Role.Countdown)) { Deny(s); return; }
                _store.CountdownPresets = m.Presets.Take(40).ToList();
                LibraryChanged();
                break;
            case SaveStage m:
                if (!MayOperate(s.User!, Role.Countdown) && !s.User!.MayRunLiveOutput) { Deny(s); return; }
                _store.StageLayout = m.Layout;
                _store.SermonPoints = m.SermonPoints.Take(60).ToList();
                LibraryChanged();
                SendToEngines(new EngineSettings(_store.Broadcast, _store.StageLayout));
                break;
            case SetModes m: OnSetModes(s, m); break;
            case MediaCommand m: OnMedia(s, m); break;
            case CameraCommand m: OnCamera(s, m); break;
            case EngineCommand m: OnEngineCommand(s, m); break;
            case SaveBroadcastSettings m:
                if (!s.User!.IsAdmin) { Deny(s); return; }
                if (Blocked(s)) return;
                var keep = _store.Broadcast;
                // An empty key from a form means "keep the key you have".
                var b = m.Settings with { StreamKey = string.IsNullOrEmpty(m.Settings.StreamKey) ? keep.StreamKey : m.Settings.StreamKey };
                _store.Broadcast = b;
                _log.Write($"{s.User.Name} changed recording and streaming settings");
                SendToEngines(new EngineSettings(b, _store.StageLayout));
                LibraryChanged();
                break;
        }
    }

    private static bool MayOperate(UserInfo u, Role role) => !u.IsTrainee && (u.IsAdmin || u.Roles.HasFlag(role));

    private static void Deny(Session s) => s.Send(new Notice("Your role cannot change that.", true));

    /// <summary>Service mode blocks settings changes, imports and updates while a service runs.</summary>
    private bool Blocked(Session s)
    {
        if (!_live.Modes.ServiceMode) return false;
        s.Send(new Notice("Service mode is on. Settings, imports and updates are locked until the Live Output operator turns it off.", true));
        return true;
    }

    /// <summary>Sends the library again to every PC (for example after a new version is offered).</summary>
    public void RefreshLibrary() => LibraryChanged();

    private void LibraryChanged()
    {
        _libraryCache = null;
        BroadcastLibrary();
    }

    // ---------------- Songs ----------------

    private void OnSaveSongs(Session s, SaveSongs m)
    {
        if (!MayOperate(s.User!, Role.Lyrics)) { Deny(s); return; }
        if (m.Songs.Count > 1 && Blocked(s)) return;
        var songs = m.Songs
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Title))
            .Take(1000)
            .Select(x => x with
            {
                Title = x.Title.Trim(),
                Parts = x.Parts.Where(p => p.Text.Trim().Length > 0).Take(60).ToList(),
                UpdatedUtc = DateTime.UtcNow,
            })
            .ToList();
        if (songs.Count == 0) return;
        _store.SaveSongs(songs);
        _log.Write(songs.Count == 1 ? $"{s.User!.Name} saved the song {songs[0].Title}" : $"{s.User!.Name} imported {songs.Count} songs");
        Broadcast(new SongsChanged(songs, [], _store.SongsVersion));
        LibraryChanged();
    }

    private void OnDeleteSongs(Session s, DeleteSongs m)
    {
        if (!MayOperate(s.User!, Role.Lyrics)) { Deny(s); return; }
        if (m.Ids.Count > 1 && Blocked(s)) return;
        _store.DeleteSongs(m.Ids);
        _log.Write($"{s.User!.Name} removed {m.Ids.Count} song(s)");
        Broadcast(new SongsChanged([], m.Ids, _store.SongsVersion));
        LibraryChanged();
    }

    private void OnSaveSongList(Session s, SaveSongList m)
    {
        if (!MayOperate(s.User!, Role.Lyrics)) { Deny(s); return; }
        _store.SongList = m.List with { Entries = m.List.Entries.Take(100).ToList() };
        LibraryChanged();
    }

    private void OnSaveSectionTheme(Session s, SaveTheme m)
    {
        var role = m.Kind switch { ThemeKind.Lyrics => Role.Lyrics, ThemeKind.Announcements => Role.Announcements, _ => Role.Scripture };
        if (!MayOperate(s.User!, role)) { Deny(s); return; }
        var t = m.Theme;
        if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name)) { s.Send(new Notice("A theme needs a name.", true)); return; }
        var list = _store.Themes(m.Kind).ToList();
        var i = list.FindIndex(x => x.Id == t.Id);
        if (i >= 0) list[i] = t; else list.Add(t);
        if (list.Count > 60) { s.Send(new Notice("Up to 60 themes are supported.", true)); return; }
        _store.SetThemes(m.Kind, list);
        _log.Write($"{s.User!.Name} saved the {m.Kind} theme {t.Name}");
        LibraryChanged();
    }

    private void OnDeleteSectionTheme(Session s, DeleteTheme m)
    {
        var role = m.Kind switch { ThemeKind.Lyrics => Role.Lyrics, ThemeKind.Announcements => Role.Announcements, _ => Role.Scripture };
        if (!MayOperate(s.User!, role)) { Deny(s); return; }
        var list = _store.Themes(m.Kind).Where(x => x.Id != m.ThemeId).ToList();
        if (list.Count == 0) { s.Send(new Notice("Keep at least one theme.", true)); return; }
        _store.SetThemes(m.Kind, list);
        if (m.Kind == ThemeKind.Lyrics && _store.LyricsSettings.DefaultThemeId == m.ThemeId)
            _store.LyricsSettings = _store.LyricsSettings with { DefaultThemeId = list[0].Id };
        LibraryChanged();
    }

    // ---------------- Live Output operator ----------------

    private void OnSetModes(Session s, SetModes m)
    {
        if (!s.User!.MayRunLiveOutput) { Deny(s); return; }
        var modes = m.Modes with { Booth = m.Modes.Booth is { } b ? (b.Length > 200 ? b[..200] : b) : null };
        lock (_gate)
        {
            var before = _live.Modes;
            _live.SetModes(modes);
            _store.Modes = modes;
            Persist();
            if (before.ServiceMode != modes.ServiceMode) _log.Write($"{s.User.Name} turned Service mode {(modes.ServiceMode ? "on" : "off")}");
            if (before.Rehearsal != modes.Rehearsal) _log.Write($"{s.User.Name} turned Rehearsal mode {(modes.Rehearsal ? "on" : "off")}");
        }
        if (modes.Rehearsal) SendToEngines(new EngineCommand(EngineAction.StopRecording));
        BroadcastSnapshot();
    }

    /// <summary>The media layer: one source owned by the Live Output PC, shown on every audience screen and Broadcast.</summary>
    private void OnMedia(Session s, MediaCommand m)
    {
        if (!s.User!.MayRunLiveOutput) { Deny(s); return; }
        lock (_gate)
        {
            var src = EnsureLiveSource(SectionKind.Media, s.User.Name);
            MediaPlay? play = m.Play;
            if (play is { Playing: true }) play = play with { StartUtc = DateTime.UtcNow.AddSeconds(-Math.Max(0, play.PausedAt)) };
            _live.SetContent(src.Key, play is null ? null : new SlideContent(play.Media.Title, Media: play));
            Persist();
            if (play is not null) _log.Write($"{s.User.Name}: {(play.Playing ? "play" : "cue")} {play.Media.Title}");
        }
        BroadcastSnapshot();
    }

    private void OnCamera(Session s, CameraCommand m)
    {
        if (!s.User!.MayRunLiveOutput) { Deny(s); return; }
        lock (_gate)
        {
            var src = EnsureLiveSource(SectionKind.Camera, s.User.Name);
            _live.SetContent(src.Key, m.Feed is null ? null : new SlideContent(m.Feed.Label, Caption: m.Feed.Source, Camera: m.Feed));
            Persist();
            _log.Write($"{s.User.Name}: direct camera {m.Feed?.Label ?? "off"}");
        }
        BroadcastSnapshot();
    }

    /// <summary>A source owned by the Live Output PC itself, pinned live on every audience screen and Broadcast the first time.</summary>
    private LiveState.SourceState EnsureLiveSource(SectionKind section, string user)
    {
        var key = Sections.SourceKey(LiveState.LiveOutputPc, section);
        var existed = _live.Source(key) is not null;
        var src = _live.RegisterSource(LiveState.LiveOutputPc, "Live Output", section, user);
        if (!existed)
            foreach (var sc in _live.Screens.Where(x => x.Kind is ScreenKind.Audience or ScreenKind.Broadcast))
                _live.Pin(key, sc.Id);
        return src;
    }

    private void OnEngineCommand(Session s, EngineCommand m)
    {
        if (!s.User!.MayRunLiveOutput) { Deny(s); return; }
        if (m.Action is EngineAction.StartRecording or EngineAction.StartStream && _live.Modes.Rehearsal)
        {
            s.Send(new Notice("Rehearsal mode is on. Turn it off to record or stream.", true));
            return;
        }
        if (!_sessions.Values.Any(x => x.Hello?.Kind == ClientKind.OutputEngine))
        {
            s.Send(new Notice("The output engine is not running. Press Restart output windows.", true));
            return;
        }
        SendToEngines(m);
        _log.Write($"{s.User.Name}: {m.Action}{(m.ServiceName is { } n ? " (" + n + ")" : "")}");
    }

    private void SendToEngines(Message m)
    {
        var json = Wire.Serialize(m);
        foreach (var x in _sessions.Values.Where(x => x.Hello?.Kind == ClientKind.OutputEngine)) x.SendRaw(json);
    }

    private void Broadcast(Message m)
    {
        var json = Wire.Serialize(m);
        foreach (var x in _sessions.Values.Where(x => x.Hello is not null)) x.SendRaw(json);
    }

    // ---------------- Versions and updates ----------------

    /// <summary>The newest version in the updates folder, or this Live Output PC's own version.</summary>
    private string OfferedVersion()
    {
        var best = _options.Version;
        if (_options.UpdatesDir is { } dir && File.Exists(Path.Combine(dir, "offered.txt")))
        {
            var v = File.ReadAllText(Path.Combine(dir, "offered.txt")).Trim();
            if (v.Length > 0) best = v;
        }
        return best;
    }

    /// <summary>True when a terminal runs an older NEXORA than the Live Output PC offers.</summary>
    private bool Outdated(Session owner) =>
        Version.TryParse(owner.Hello?.AppVersion, out var mine) && Version.TryParse(OfferedVersion(), out var offered) && mine < offered;

    // ---------------- HTTP: songs, usage, backups, updates ----------------

    private async Task<bool> HandleMoreHttpAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        bool CodeOk() => ctx.Request.Headers[Wire.CodeHeader].ToString().Trim() == _options.PairingCode().Trim();
        UserInfo? TokenUser() => _tokens.TryGetValue(ctx.Request.Headers[Wire.TokenHeader].ToString(), out var uid) ? _store.User(uid) : null;

        if (path == Wire.LibraryPath + "/songs" && HttpMethods.IsGet(ctx.Request.Method))
        {
            if (!CodeOk()) { ctx.Response.StatusCode = 403; return true; }
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["X-Nexora-Songs-Version"] = _store.SongsVersion.ToString();
            await JsonSerializer.SerializeAsync(ctx.Response.Body, _store.Songs(), Wire.Options);
            return true;
        }
        if (path == Wire.LibraryPath + "/usage" && HttpMethods.IsGet(ctx.Request.Method))
        {
            if (!CodeOk()) { ctx.Response.StatusCode = 403; return true; }
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, _store.SongUses(), Wire.Options);
            return true;
        }
        // A full copy for promoting a terminal, or the admin's one-click backup.
        if (path is Wire.LibraryPath + "/promote" or "/backup" && HttpMethods.IsGet(ctx.Request.Method))
        {
            if (path == "/backup" ? TokenUser() is not { IsAdmin: true } : !CodeOk()) { ctx.Response.StatusCode = 403; return true; }
            var tmp = Path.Combine(Path.GetTempPath(), $"nexora-backup-{Guid.NewGuid():N}.zip");
            try
            {
                await Task.Run(() => WriteBackup(tmp));
                ctx.Response.ContentType = "application/zip";
                await ctx.Response.SendFileAsync(tmp);
            }
            finally { TryDelete(tmp); }
            return true;
        }
        if (path == "/restore" && HttpMethods.IsPost(ctx.Request.Method))
        {
            if (TokenUser() is not { IsAdmin: true } admin) { ctx.Response.StatusCode = 403; return true; }
            if (_live.Modes.ServiceMode)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsync("Service mode is on. Turn it off before restoring a backup.");
                return true;
            }
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
            var pending = Path.Combine(Path.GetTempPath(), $"nexora-restore-{Guid.NewGuid():N}.zip");
            await using (var fs = File.Create(pending)) await ctx.Request.Body.CopyToAsync(fs);
            if (!IsBackup(pending))
            {
                TryDelete(pending);
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("That file is not a NEXORA backup.");
                return true;
            }
            _log.Write($"{admin.Name} is restoring a backup");
            await ctx.Response.WriteAsync("ok");
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                RestoreRequested?.Invoke(pending);
            });
            return true;
        }
        if (path.StartsWith("/updates/") && HttpMethods.IsGet(ctx.Request.Method) && _options.UpdatesDir is { } updates)
        {
            var name = path["/updates/".Length..];
            if (!SafeName(name) || !File.Exists(Path.Combine(updates, name))) { ctx.Response.StatusCode = 404; return true; }
            ctx.Response.ContentType = name.EndsWith(".json") ? "application/json" : "application/octet-stream";
            await ctx.Response.SendFileAsync(Path.Combine(updates, name));
            return true;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public const string BackupMarker = "nexora-backup.json";

    /// <summary>
    /// One zip with everything a Live Output PC needs: the library (users, settings, songs, themes),
    /// imported Bibles, uploaded media and this PC's settings. Videos and recordings are not included.
    /// </summary>
    public void WriteBackup(string zipPath)
    {
        var dbCopy = Path.Combine(Path.GetTempPath(), $"nexora-db-{Guid.NewGuid():N}.db");
        try
        {
            _store.BackupTo(dbCopy);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(dbCopy, "library/nexora-library.db", CompressionLevel.Optimal);
            void AddDir(string? dir, string prefix)
            {
                if (dir is null || !Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')))
                    zip.CreateEntryFromFile(f, prefix + Path.GetFileName(f), CompressionLevel.Fastest);
            }
            AddDir(_options.BiblesDir, "library/bibles/");
            AddDir(_options.MediaDir, "library/media/");
            if (_options.ConfigFile is { } cfg && File.Exists(cfg)) zip.CreateEntryFromFile(cfg, "live-output-pc.json");
            var marker = zip.CreateEntry(BackupMarker);
            using (var w = new StreamWriter(marker.Open(), Encoding.UTF8))
                w.Write(JsonSerializer.Serialize(new { app = "NEXORA", version = _options.Version, created = DateTime.UtcNow, server = _options.ServerName(), serverId = ServerId }));
        }
        finally { TryDelete(dbCopy); }
    }

    public static bool IsBackup(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.GetEntry(BackupMarker) is not null && zip.GetEntry("library/nexora-library.db") is not null;
        }
        catch { return false; }
    }

    /// <summary>One backup a night (after 2 AM, or at first start after), keeping the last 30.</summary>
    private void NightlyBackup()
    {
        if (_options.BackupsDir is not { } dir) return;
        var now = DateTime.Now;
        if (now - _lastBackupCheck < TimeSpan.FromMinutes(5)) return;
        _lastBackupCheck = now;
        if (now.Hour < 2) return;
        try
        {
            Directory.CreateDirectory(dir);
            var name = $"NEXORA backup {now:yyyy-MM-dd}.zip";
            if (File.Exists(Path.Combine(dir, name))) return;
            if (_live.Modes.ServiceMode) return; // never during a service
            var tmp = Path.Combine(dir, "." + name + ".part");
            WriteBackup(tmp);
            File.Move(tmp, Path.Combine(dir, name), overwrite: true);
            foreach (var old in Directory.GetFiles(dir, "NEXORA backup *.zip").OrderByDescending(f => f).Skip(30)) TryDelete(old);
            _log.Write("Nightly backup saved: " + name);
        }
        catch (Exception ex)
        {
            _log.Write("Nightly backup failed: " + ex.Message);
        }
    }
}
