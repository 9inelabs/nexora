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

    public static readonly IReadOnlyList<ScreenInfo> DefaultScreens =
    [
        new("main", "Main Audience Screen", ScreenKind.Audience, 0, true, 0),
        new("stage", "Stage Monitor", ScreenKind.Stage, 0, false, 1),
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
