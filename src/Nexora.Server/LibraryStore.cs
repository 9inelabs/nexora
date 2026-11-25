using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nexora.Core.Model;
using Nexora.Core.Protocol;
using Nexora.Core.Security;

namespace Nexora.Server;

/// <summary>
/// The master copy of users and settings on the Live Output PC: one SQLite file,
/// easy to back up by copying.
/// </summary>
public sealed class LibraryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public LibraryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                initials TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                roles INTEGER NOT NULL,
                pin_hash TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO meta(key, value) VALUES ('schema', '1');
            CREATE TABLE IF NOT EXISTS songs (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS song_use (
                date TEXT NOT NULL,
                song_id TEXT NOT NULL,
                title TEXT NOT NULL,
                author TEXT NOT NULL,
                ccli TEXT NOT NULL,
                PRIMARY KEY (date, song_id)
            );
            """);
    }

    // ---------- Users ----------

    public IReadOnlyList<UserInfo> Users()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, initials, roles, title FROM users ORDER BY created_utc";
            using var r = cmd.ExecuteReader();
            var list = new List<UserInfo>();
            while (r.Read())
                list.Add(new UserInfo(r.GetString(0), r.GetString(1), r.GetString(2), (Role)r.GetInt64(3), r.GetString(4)));
            return list;
        }
    }

    public UserInfo? User(string id) => Users().FirstOrDefault(u => u.Id == id);

    public bool HasAdmin() => Users().Any(u => u.IsAdmin);

    public void SaveUser(UserInfo user, string? pin)
    {
        lock (_gate)
        {
            var exists = User(user.Id) is not null;
            if (!exists && pin is null) throw new InvalidOperationException("A new user needs a PIN.");
            using var cmd = _db.CreateCommand();
            if (exists)
            {
                cmd.CommandText = pin is null
                    ? "UPDATE users SET name=$n, initials=$i, roles=$r, title=$t WHERE id=$id"
                    : "UPDATE users SET name=$n, initials=$i, roles=$r, title=$t, pin_hash=$p WHERE id=$id";
            }
            else
            {
                cmd.CommandText = "INSERT INTO users(id, name, initials, roles, title, pin_hash, created_utc) VALUES ($id, $n, $i, $r, $t, $p, $c)";
                cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            }
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$n", user.Name.Trim());
            cmd.Parameters.AddWithValue("$i", user.Initials.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("$r", (long)user.Roles);
            cmd.Parameters.AddWithValue("$t", user.Title.Trim());
            if (pin is not null) cmd.Parameters.AddWithValue("$p", PinHasher.Hash(pin));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteUser(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public bool CheckPin(string userId, string pin)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT pin_hash FROM users WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", userId);
            return cmd.ExecuteScalar() is string hash && PinHasher.Verify(pin, hash);
        }
    }

    // ---------- Settings ----------

    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            if (cmd.ExecuteScalar() is not string json) return default;
            try { return JsonSerializer.Deserialize<T>(json, Wire.Options); }
            catch (JsonException) { return default; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value, Wire.Options));
            cmd.ExecuteNonQuery();
        }
    }

    public Branding Branding
    {
        get => Get<Branding>("branding") ?? Branding.Default;
        set => Set("branding", value);
    }

    public IReadOnlyList<ScreenInfo> Screens
    {
        get => Get<List<ScreenInfo>>("screens") is { Count: > 0 } s ? s : DefaultScreens;
        set => Set("screens", value);
    }

    public IReadOnlyList<ScriptureTheme> ScriptureThemes
    {
        get => Get<List<ScriptureTheme>>("scriptureThemes") is { Count: > 0 } t ? t : ScriptureTheme.BuiltIn;
        set => Set("scriptureThemes", value);
    }

    public ScriptureSettings ScriptureSettings
    {
        get => Get<ScriptureSettings>("scriptureSettings") ?? ScriptureSettings.Default;
        set => Set("scriptureSettings", value);
    }

    // ---------- Songs ----------

    public IReadOnlyList<Song> Songs()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM songs ORDER BY title COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            var list = new List<Song>();
            while (r.Read())
            {
                try { if (JsonSerializer.Deserialize<Song>(r.GetString(0), Wire.Options) is { } s) list.Add(s); }
                catch (JsonException) { }
            }
            return list;
        }
    }

    public Song? Song(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM songs WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Song>(json, Wire.Options) : null;
        }
    }

    public int SongCount()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM songs";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void SaveSongs(IEnumerable<Song> songs)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var s in songs)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO songs(id, title, json, updated_utc) VALUES ($id, $t, $j, $u) ON CONFLICT(id) DO UPDATE SET title=excluded.title, json=excluded.json, updated_utc=excluded.updated_utc";
                cmd.Parameters.AddWithValue("$id", s.Id);
                cmd.Parameters.AddWithValue("$t", s.Title);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s, Wire.Options));
                cmd.Parameters.AddWithValue("$u", s.UpdatedUtc.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            SongsVersion++;
        }
    }

    public void DeleteSongs(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM songs WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            SongsVersion++;
        }
    }

    /// <summary>Goes up with every song change, so PCs can tell when their copy is out of date.</summary>
    public long SongsVersion
    {
        get => Get<long>("songsVersion");
        private set => Set("songsVersion", value);
    }

    /// <summary>Logs a song for the CCLI report, once per song per day. True when it was new today.</summary>
    public bool LogSongUse(Song song, string date)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO song_use(date, song_id, title, author, ccli) VALUES ($d, $id, $t, $a, $c)";
            cmd.Parameters.AddWithValue("$d", date);
            cmd.Parameters.AddWithValue("$id", song.Id);
            cmd.Parameters.AddWithValue("$t", song.Title);
            cmd.Parameters.AddWithValue("$a", song.Author);
            cmd.Parameters.AddWithValue("$c", song.Ccli);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<SongUse> SongUses()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT date, song_id, title, author, ccli FROM song_use ORDER BY date DESC, title";
            using var r = cmd.ExecuteReader();
            var list = new List<SongUse>();
            while (r.Read()) list.Add(new SongUse(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return list;
        }
    }

    // ---------- Other sections ----------

    public IReadOnlyList<ScriptureTheme> Themes(ThemeKind kind) => kind switch
    {
        ThemeKind.Scripture => ScriptureThemes,
        ThemeKind.Lyrics => Get<List<ScriptureTheme>>("lyricThemes") is { Count: > 0 } l ? l : SectionThemes.Lyrics,
        _ => Get<List<ScriptureTheme>>("announcementThemes") is { Count: > 0 } a ? a : SectionThemes.Announcements,
    };

    public void SetThemes(ThemeKind kind, IReadOnlyList<ScriptureTheme> list)
    {
        switch (kind)
        {
            case ThemeKind.Scripture: ScriptureThemes = list; break;
            case ThemeKind.Lyrics: Set("lyricThemes", list); break;
            default: Set("announcementThemes", list); break;
        }
    }

    public LyricsSettings LyricsSettings
    {
        get => Get<LyricsSettings>("lyricsSettings") ?? LyricsSettings.Default;
        set => Set("lyricsSettings", value);
    }

    public SongList? SongList
    {
        get => Get<SongList>("songList");
        set => Set("songList", value);
    }

    public IReadOnlyList<AnnouncementSlide> Announcements
    {
        get => Get<List<AnnouncementSlide>>("announcements") ?? [];
        set => Set("announcements", value);
    }

    public IReadOnlyList<Person> People
    {
        get => Get<List<Person>>("people") ?? [];
        set => Set("people", value);
    }

    public IReadOnlyList<string> TodayPeople
    {
        get => Get<List<string>>("todayPeople") ?? [];
        set => Set("todayPeople", value);
    }

    public LowerThirdDefaults LowerThirdDefaults
    {
        get => Get<LowerThirdDefaults>("lowerThirds") ?? LowerThirdDefaults.Default;
        set => Set("lowerThirds", value);
    }

    public IReadOnlyList<CountdownPreset> CountdownPresets
    {
        get => Get<List<CountdownPreset>>("countdownPresets") is { } l ? l : DefaultCountdowns;
        set => Set("countdownPresets", value);
    }

    public static readonly IReadOnlyList<CountdownPreset> DefaultCountdowns =
    [
        new("service", "Service starts", CountdownMode.ToTime, 0, "09:00", 300, false, CountdownLook.FullScreen, ZeroAction.StopAnnouncements),
        new("sermon", "Sermon", CountdownMode.Duration, 40 * 60, null, 300, true, CountdownLook.StageOnly, ZeroAction.None),
        new("offering", "Offering", CountdownMode.Duration, 3 * 60, null, 30, false, CountdownLook.StageOnly, ZeroAction.None),
        new("worship", "Worship", CountdownMode.Duration, 25 * 60, null, 120, true, CountdownLook.StageOnly, ZeroAction.None),
    ];

    public StageLayout StageLayout
    {
        get => Get<StageLayout>("stageLayout") ?? StageLayout.Default;
        set => Set("stageLayout", value);
    }

    public IReadOnlyList<string> SermonPoints
    {
        get => Get<List<string>>("sermonPoints") ?? [];
        set => Set("sermonPoints", value);
    }

    public LiveModes Modes
    {
        get => Get<LiveModes>("modes") ?? new LiveModes();
        set => Set("modes", value);
    }

    /// <summary>Recording and streaming settings. The stream key is stored encrypted.</summary>
    public BroadcastSettings Broadcast
    {
        get
        {
            var b = Get<BroadcastSettings>("broadcast") ?? new BroadcastSettings();
            return b with { StreamKey = Secrets.Unprotect(b.StreamKey) };
        }
        set => Set("broadcast", value with { StreamKey = Secrets.Protect(value.StreamKey) });
    }

    /// <summary>Copies the whole library to a file while it is in use (SQLite online backup).</summary>
    public void BackupTo(string path)
    {
        lock (_gate)
        {
            if (File.Exists(path)) File.Delete(path);
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            dest.Open();
            _db.BackupDatabase(dest);
        }
    }

    public static readonly IReadOnlyList<ScreenInfo> DefaultScreens =
    [
        new("main", "Main Audience Screen", ScreenKind.Audience, 0, true, 0),
        new("stage", "Stage Monitor", ScreenKind.Stage, 0, false, 1),
        new("broadcast", "Broadcast", ScreenKind.Broadcast, 0, true, 2),
    ];

    public string ServerId
    {
        get
        {
            var id = Get<string>("serverId");
            if (id is null)
            {
                id = Guid.NewGuid().ToString("N")[..12];
                Set("serverId", id);
            }
            return id;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
