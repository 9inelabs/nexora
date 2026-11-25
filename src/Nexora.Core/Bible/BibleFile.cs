using System.Text;
using Microsoft.Data.Sqlite;

namespace Nexora.Core.Bible;

/// <summary>Short facts about one installed translation.</summary>
public sealed record BibleInfo(string Abbrev, string Name, string Language, int Verses, bool Complete, string Source, string Hash);

/// <summary>
/// NEXORA's own Bible format (.nxbible): one SQLite file per translation with every verse
/// and a full-text index for word search.
/// </summary>
public static class BibleFile
{
    public const string Extension = ".nxbible";
    public const int FormatVersion = 1;

    public static void Write(BibleText bible, string path, string source, string hash)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        var report = BibleCheck.Run(bible);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
        {
            db.Open();
            Exec(db, """
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE verses (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, chapter INTEGER NOT NULL, verse INTEGER NOT NULL, text TEXT NOT NULL);
                CREATE VIRTUAL TABLE verses_fts USING fts5(text, content='verses', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
                """);
            using var tx = db.BeginTransaction();
            using (var meta = db.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
                var k = meta.Parameters.Add("$k", SqliteType.Text);
                var v = meta.Parameters.Add("$v", SqliteType.Text);
                foreach (var (key, value) in new[]
                {
                    ("format", FormatVersion.ToString()),
                    ("abbrev", bible.Abbrev),
                    ("name", bible.Name),
                    ("language", bible.Language),
                    ("source", source),
                    ("hash", hash),
                    ("verses", bible.Count.ToString()),
                    ("complete", report.Complete ? "1" : "0"),
                    ("created", DateTime.UtcNow.ToString("O")),
                })
                {
                    k.Value = key;
                    v.Value = value;
                    meta.ExecuteNonQuery();
                }
            }
            using (var ins = db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO verses(id, book, chapter, verse, text) VALUES ($id, $b, $c, $v, $t)";
                var id = ins.Parameters.Add("$id", SqliteType.Integer);
                var b = ins.Parameters.Add("$b", SqliteType.Integer);
                var c = ins.Parameters.Add("$c", SqliteType.Integer);
                var vv = ins.Parameters.Add("$v", SqliteType.Integer);
                var t = ins.Parameters.Add("$t", SqliteType.Text);
                foreach (var x in bible.All())
                {
                    id.Value = Id(x.Book, x.Chapter, x.Verse);
                    b.Value = x.Book;
                    c.Value = x.Chapter;
                    vv.Value = x.Verse;
                    t.Value = x.Text;
                    ins.ExecuteNonQuery();
                }
            }
            Exec(db, "INSERT INTO verses_fts(verses_fts) VALUES('rebuild');", tx);
            tx.Commit();
            Exec(db, "VACUUM;");
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, path, overwrite: true);
    }

    public static BibleInfo? ReadInfo(string path)
    {
        try
        {
            using var db = OpenRead(path);
            var meta = Meta(db);
            if (!meta.TryGetValue("abbrev", out var abbrev) || string.IsNullOrWhiteSpace(abbrev)) return null;
            return new BibleInfo(abbrev, meta.GetValueOrDefault("name", abbrev), meta.GetValueOrDefault("language", ""),
                int.TryParse(meta.GetValueOrDefault("verses"), out var n) ? n : 0,
                meta.GetValueOrDefault("complete") == "1", meta.GetValueOrDefault("source", ""), meta.GetValueOrDefault("hash", ""));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static SqliteConnection OpenRead(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        return db;
    }

    internal static Dictionary<string, string> Meta(SqliteConnection db)
    {
        var d = new Dictionary<string, string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public static long Id(int book, int chapter, int verse) => book * 1_000_000L + chapter * 1000L + verse;

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Turns what someone typed into a safe full-text query: every word must appear; "quoted words" are a phrase.</summary>
    internal static string? FtsQuery(string input)
    {
        var parts = new List<string>();
        var s = input.Trim();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                var phrase = end < 0 ? s[(i + 1)..] : s[(i + 1)..end];
                var words = Words(phrase);
                if (words.Count > 0) parts.Add("\"" + string.Join(" ", words) + "\"");
                i = end < 0 ? s.Length : end + 1;
                continue;
            }
            var next = s.IndexOf('"', i);
            var chunk = next < 0 ? s[i..] : s[i..next];
            foreach (var w in Words(chunk)) parts.Add("\"" + w + "\"" + (w.Length >= 3 ? "*" : ""));
            i = next < 0 ? s.Length : next;
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static List<string> Words(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'') sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0) { list.Add(sb.ToString().Trim('\'')); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(sb.ToString().Trim('\''));
        return list.Where(w => w.Length > 0).ToList();
    }
}

/// <summary>One translation opened for reading. Every verse is held in memory so lookups are instant.</summary>
public sealed class BibleTranslation : IDisposable
{
    private readonly Dictionary<long, string> _verses;
    private readonly Dictionary<(int Book, int Chapter), int> _lastVerse = new();
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    private BibleTranslation(string path, BibleInfo info, SqliteConnection db, Dictionary<long, string> verses)
    {
        Path = path;
        Info = info;
        _db = db;
        _verses = verses;
        foreach (var id in verses.Keys)
        {
            var key = ((int)(id / 1_000_000), (int)(id / 1000 % 1000));
            var v = (int)(id % 1000);
            if (_lastVerse.GetValueOrDefault(key) < v) _lastVerse[key] = v;
        }
    }

    public string Path { get; }
    public BibleInfo Info { get; }
    public string Abbrev => Info.Abbrev;
    public string Name => Info.Name;

    public static BibleTranslation Open(string path)
    {
        var db = BibleFile.OpenRead(path);
        try
        {
            var meta = BibleFile.Meta(db);
            var verses = new Dictionary<long, string>(32_000);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, text FROM verses";
                using var r = cmd.ExecuteReader();
                while (r.Read()) verses[r.GetInt64(0)] = r.GetString(1);
            }
            var abbrev = meta.GetValueOrDefault("abbrev", System.IO.Path.GetFileNameWithoutExtension(path));
            var info = new BibleInfo(abbrev, meta.GetValueOrDefault("name", abbrev), meta.GetValueOrDefault("language", ""),
                verses.Count, meta.GetValueOrDefault("complete") == "1", meta.GetValueOrDefault("source", ""), meta.GetValueOrDefault("hash", ""));
            return new BibleTranslation(path, info, db, verses);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public string? Verse(int book, int chapter, int verse) => _verses.GetValueOrDefault(BibleFile.Id(book, chapter, verse));

    /// <summary>Verses in a chapter in this translation (may differ slightly from the KJV count).</summary>
    public int VersesIn(int book, int chapter) => _lastVerse.GetValueOrDefault((book, chapter));

    public bool Has(int book, int chapter, int verse) => _verses.ContainsKey(BibleFile.Id(book, chapter, verse));

    public BibleCheckReport Check() => BibleCheck.Run(Has, _verses.Keys.Select(id => ((int)(id / 1_000_000), (int)(id / 1000 % 1000), (int)(id % 1000))),
        (b, c, v) => Verse(b, c, v) == "");

    /// <summary>Word search across the whole Bible. Results are best matches first.</summary>
    public IReadOnlyList<(int Book, int Chapter, int Verse, string Text)> Search(string query, int limit = 100)
    {
        var fts = BibleFile.FtsQuery(query);
        if (fts is null) return [];
        var list = new List<(int, int, int, string)>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM verses_fts WHERE verses_fts MATCH $q ORDER BY rank LIMIT $n";
            cmd.Parameters.AddWithValue("$q", fts);
            cmd.Parameters.AddWithValue("$n", limit);
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetInt64(0);
                    if (_verses.TryGetValue(id, out var text))
                        list.Add(((int)(id / 1_000_000), (int)(id / 1000 % 1000), (int)(id % 1000), text));
                }
            }
            catch (SqliteException) { }
        }
        return list;
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
