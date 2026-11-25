using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Nexora.Core.Model;

namespace Nexora.Core.Songs;

/// <summary>
/// Reads songs from other programs: the EasyWorship 6/7 song library (Songs.db with SongWords.db),
/// OpenLyrics XML, OpenSong, CCLI SongSelect text files and plain text.
/// </summary>
public static class SongImport
{
    public sealed record Result(IReadOnlyList<Song> Songs, string Format, IReadOnlyList<string> Problems);

    /// <summary>Imports a file, or every song file in a folder.</summary>
    public static Result Load(string path)
    {
        if (Directory.Exists(path)) return LoadFolder(path);
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        if (name is "songs.db" or "songwords.db" || ext == ".db" && LooksLikeSqlite(path)) return EasyWorship(path);
        if (ext == ".zip") return Zip(path);
        var text = ReadText(path);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<"))
        {
            if (trimmed.Contains("openlyrics.info", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("<song", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<lyrics", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<verse", StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Contains("<lines", StringComparison.OrdinalIgnoreCase)) return One(OpenLyrics(text, name), "OpenLyrics XML");
                return One(OpenSong(text, name), "OpenSong");
            }
            return One(OpenSong(text, name), "OpenSong");
        }
        return One(PlainText(text, Path.GetFileNameWithoutExtension(path)), text.Contains("CCLI", StringComparison.OrdinalIgnoreCase) ? "SongSelect text" : "Plain text");
    }

    private static Result One(Song? s, string format) =>
        s is null ? new Result([], format, ["No lyrics were found in this file."]) : new Result([s], format, []);

    private static Result LoadFolder(string dir)
    {
        var songs = new List<Song>();
        var problems = new List<string>();
        var ew = Directory.EnumerateFiles(dir, "Songs.db", SearchOption.AllDirectories).FirstOrDefault();
        if (ew is not null) return EasyWorship(ew);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(20000))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".txt" or ".xml" or "" or ".usr" or ".song")) continue;
            try
            {
                var r = Load(f);
                songs.AddRange(r.Songs);
                problems.AddRange(r.Problems.Select(p => $"{Path.GetFileName(f)}: {p}"));
            }
            catch (Exception ex) { problems.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return new Result(songs, "Folder of song files", problems);
    }

    private static Result Zip(string path)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "nexora-songs-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(path, tmp);
            return LoadFolder(tmp);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static bool LooksLikeSqlite(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            return fs.Read(head) == 16 && Encoding.ASCII.GetString(head).StartsWith("SQLite format 3");
        }
        catch { return false; }
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var utf8 = new UTF8Encoding(false, true);
        try { return utf8.GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static string NewId() => "s-" + Guid.NewGuid().ToString("N")[..12];

    // ---------------- EasyWorship ----------------

    /// <summary>
    /// EasyWorship 6 and 7 keep the song details in Songs.db and the words (as RTF) in SongWords.db,
    /// in the same folder (Documents\Softouch\EasyWorship\Default\v6.1\Databases\Data).
    /// </summary>
    public static Result EasyWorship(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var songsDb = Path.Combine(dir, "Songs.db");
        var wordsDb = Path.Combine(dir, "SongWords.db");
        if (!File.Exists(songsDb)) songsDb = path;
        if (!File.Exists(wordsDb))
            return new Result([], "EasyWorship", ["SongWords.db was not found next to Songs.db. Copy the whole Data folder and pick Songs.db again."]);

        var songs = new List<Song>();
        var problems = new List<string>();
        var meta = new Dictionary<long, (string Title, string Author, string Copyright, string Ccli, string Tags)>();
        using (var db = Open(songsDb))
        {
            var cols = Columns(db, "song");
            if (cols.Count == 0) return new Result([], "EasyWorship", ["This is not an EasyWorship song library (no song table)."]);
            string Col(string name) => cols.Contains(name) ? name : "''";
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT rowid, {Col("title")}, {Col("author")}, {Col("copyright")}, {Col("reference_number")}, {Col("tags")} FROM song";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                meta[r.GetInt64(0)] = (Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5));
        }
        var words = new Dictionary<long, string>();
        using (var db = Open(wordsDb))
        {
            var cols = Columns(db, "word");
            if (!cols.Contains("song_id") || !cols.Contains("words"))
                return new Result([], "EasyWorship", ["SongWords.db does not have the expected word table."]);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT song_id, words FROM word";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0)) words[r.GetInt64(0)] = Str(r, 1);
        }
        foreach (var (id, m) in meta)
        {
            var rtf = words.GetValueOrDefault(id, "");
            var text = rtf.TrimStart().StartsWith("{\\rtf") ? RtfToText(rtf) : rtf;
            var parts = SongText.Split(text);
            if (parts.Count == 0) { problems.Add($"\"{m.Title}\" has no words and was skipped."); continue; }
            songs.Add(new Song
            {
                Id = NewId(),
                Title = string.IsNullOrWhiteSpace(m.Title) ? parts[0].Lines.FirstOrDefault() ?? "Untitled" : m.Title.Trim(),
                Author = m.Author.Trim(),
                Copyright = m.Copyright.Trim(),
                Ccli = Regex.Match(m.Ccli, @"\d+").Value,
                Tags = m.Tags.Trim(),
                Parts = parts,
                Arrangements = Arrangement(SongText.PastedOrder(text), parts),
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        return new Result(songs, "EasyWorship song library", problems);
    }

    /// <summary>The pasted order becomes the "Default" arrangement when it repeats parts.</summary>
    private static IReadOnlyList<SongArrangement> Arrangement(IReadOnlyList<string> order, IReadOnlyList<SongPart> parts) =>
        order.Count > parts.Count ? [new SongArrangement("Default", order)] : [];

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static HashSet<string> Columns(SqliteConnection db, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(1));
        return set;
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i) is byte[] b ? Encoding.UTF8.GetString(b) : r.GetValue(i).ToString() ?? "";

    /// <summary>Plain text from RTF: paragraphs become lines, EasyWorship slide markers become blank lines.</summary>
    public static string RtfToText(string rtf)
    {
        var sb = new StringBuilder();
        var skipDepth = -1; // group depth whose content is skipped
        var depth = 0;
        var ucSkip = 1;
        var pendingSkip = 0;
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth == skipDepth) skipDepth = -1; depth--; i++; continue; }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                var n = rtf[i];
                if (n is '\\' or '{' or '}') { if (skipDepth < 0) sb.Append(n); i++; continue; }
                if (n == '*') { if (skipDepth < 0) skipDepth = depth; i++; continue; }
                if (n == '\'')
                {
                    if (i + 2 < rtf.Length && skipDepth < 0 && pendingSkip == 0)
                    {
                        try { sb.Append(Encoding.Latin1.GetString([Convert.ToByte(rtf.Substring(i + 1, 2), 16)])); } catch { }
                    }
                    else if (pendingSkip > 0) pendingSkip--;
                    i += 3;
                    continue;
                }
                if (n is '\n' or '\r') { if (skipDepth < 0) sb.Append('\n'); i++; continue; }
                if (n == '~') { if (skipDepth < 0) sb.Append(' '); i++; continue; }
                if (!char.IsLetter(n)) { i++; continue; }
                var start = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                var word = rtf[start..i];
                var numStart = i;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) { i++; while (i < rtf.Length && char.IsDigit(rtf[i])) i++; }
                var num = rtf[numStart..i];
                if (i < rtf.Length && rtf[i] == ' ') i++;
                switch (word)
                {
                    case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "header" or "footer" or "listtable" or "listoverridetable" or "sdlistdef":
                        if (skipDepth < 0) skipDepth = depth;
                        break;
                    case "par" or "line":
                        if (skipDepth < 0) sb.Append('\n');
                        break;
                    case "sdslidemarker" or "page":
                        if (skipDepth < 0) sb.Append("\n\n");
                        break;
                    case "tab":
                        if (skipDepth < 0) sb.Append(' ');
                        break;
                    case "uc":
                        int.TryParse(num, out ucSkip);
                        break;
                    case "u":
                        if (skipDepth < 0 && int.TryParse(num, out var code))
                        {
                            if (code < 0) code += 65536;
                            sb.Append((char)code);
                        }
                        pendingSkip = ucSkip;
                        break;
                }
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }
            if (pendingSkip > 0) { pendingSkip--; i++; continue; }
            if (skipDepth < 0) sb.Append(c);
            i++;
        }
        var text = sb.ToString().Replace(" ", " ");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    // ---------------- OpenLyrics ----------------

    public static Song? OpenLyrics(string xml, string fallbackTitle)
    {
        var doc = XDocument.Parse(xml);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var props = doc.Root?.Element(ns + "properties");
        var title = props?.Element(ns + "titles")?.Elements(ns + "title").Select(t => t.Value.Trim()).ToList() ?? [];
        var authors = props?.Element(ns + "authors")?.Elements(ns + "author").Select(a => a.Value.Trim()) ?? [];
        var parts = new List<SongPart>();
        foreach (var v in doc.Root?.Element(ns + "lyrics")?.Elements(ns + "verse") ?? [])
        {
            var name = (string?)v.Attribute("name") ?? "v1";
            var lines = new List<string>();
            foreach (var l in v.Elements(ns + "lines"))
            {
                // <br/> separates lines inside <lines>; chords and comments are dropped.
                var sb = new StringBuilder();
                foreach (var node in l.Nodes())
                {
                    if (node is XText t) sb.Append(t.Value);
                    else if (node is XElement e && e.Name.LocalName == "br") sb.Append('\n');
                    else if (node is XElement e2 && e2.Name.LocalName is not "comment" and not "chord") sb.Append(e2.Value);
                }
                lines.AddRange(sb.ToString().Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
            }
            parts.Add(new SongPart(LabelFromCode(name), string.Join("\n", lines)));
        }
        if (parts.Count == 0) return null;
        var order = ((string?)props?.Element(ns + "verseOrder"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(LabelFromCode).ToList() ?? [];
        return new Song
        {
            Id = NewId(),
            Title = title.FirstOrDefault() ?? fallbackTitle,
            AltTitles = string.Join("; ", title.Skip(1)),
            Author = string.Join(", ", authors),
            Copyright = ((string?)props?.Element(ns + "copyright"))?.Trim() ?? "",
            Ccli = ((string?)props?.Element(ns + "ccliNo"))?.Trim() ?? "",
            Key = ((string?)props?.Element(ns + "key"))?.Trim() ?? "",
            Tempo = ((string?)props?.Element(ns + "tempo"))?.Trim() ?? "",
            Parts = parts,
            Arrangements = order.Count > 0 ? [new SongArrangement("Default", order.Where(o => parts.Any(p => p.Label == o)).ToList())] : [],
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>"v1" → "Verse 1", "c" → "Chorus", "p" → "Pre-chorus", "b2" → "Bridge 2", "e" → "Ending".</summary>
    public static string LabelFromCode(string code)
    {
        var m = Regex.Match(code.Trim().ToLowerInvariant(), @"^([a-z]+)(\d*)([a-z]?)$");
        if (!m.Success) return code;
        var name = m.Groups[1].Value switch
        {
            "v" => "Verse",
            "c" => "Chorus",
            "p" => "Pre-chorus",
            "b" => "Bridge",
            "e" => "Ending",
            "t" => "Tag",
            "i" => "Intro",
            _ => char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..],
        };
        var num = m.Groups[2].Value;
        return num.Length > 0 ? $"{name} {num}" : name;
    }

    // ---------------- OpenSong ----------------

    public static Song? OpenSong(string xml, string fallbackTitle)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null) return null;
        string Get(string n) => root.Element(n)?.Value.Trim() ?? "";
        var lyrics = Get("lyrics");
        var parts = new List<SongPart>();
        string? label = null;
        var lines = new List<string>();
        void Flush()
        {
            if (label is not null && lines.Count > 0) parts.Add(new SongPart(label, string.Join("\n", lines)));
            lines.Clear();
        }
        foreach (var raw in lyrics.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var m = Regex.Match(line, @"^\s*\[(.+?)\]\s*$");
            if (m.Success) { Flush(); label = LabelFromCode(m.Groups[1].Value); continue; }
            if (line.StartsWith('.') || line.StartsWith(';')) continue; // chords and comments
            var text = line.TrimStart(' ', '1', '2', '3', '4', '5', '6', '7', '8', '9').Replace("|", " ").Replace("_", "").Trim();
            if (text.Length == 0) continue;
            label ??= "Verse 1";
            lines.Add(text);
        }
        Flush();
        if (parts.Count == 0) return null;
        var order = Get("presentation").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(LabelFromCode).Where(o => parts.Any(p => p.Label == o)).ToList();
        return new Song
        {
            Id = NewId(),
            Title = Get("title") is { Length: > 0 } t ? t : fallbackTitle,
            AltTitles = Get("aka"),
            Author = Get("author"),
            Copyright = Get("copyright"),
            Ccli = Get("ccli"),
            Key = Get("key"),
            Tempo = Get("tempo"),
            Tags = Get("theme"),
            Parts = parts,
            Arrangements = order.Count > 0 ? [new SongArrangement("Default", order)] : [],
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    // ---------------- Plain text and SongSelect ----------------

    /// <summary>
    /// A text file: the first line is the title when it is followed by a blank line. SongSelect files end with
    /// "CCLI Song # 1234", a copyright line and the licence line; those become the song details.
    /// </summary>
    public static Song? PlainText(string text, string fallbackTitle)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        if (lines.Count == 0) return null;
        string title = fallbackTitle;
        if (lines.Count > 2 && lines[1].Trim().Length == 0 && SongText.LabelOf(lines[0]) is null)
        {
            title = lines[0].Trim();
            lines.RemoveRange(0, 2);
        }
        string ccli = "", copyright = "", author = "";
        var cut = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*CCLI\s+Song\s*#", RegexOptions.IgnoreCase));
        if (cut >= 0)
        {
            ccli = Regex.Match(lines[cut], @"\d+").Value;
            // SongSelect: the author line sits just above "CCLI Song #", the copyright line just below.
            if (cut > 0 && lines[cut - 1].Trim().Length > 0 && SongText.LabelOf(lines[cut - 1]) is null) { author = lines[cut - 1].Trim(); cut--; }
            var after = lines.Skip(cut + (author.Length > 0 ? 2 : 1)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            copyright = after.FirstOrDefault(l => l.StartsWith("©") || l.Contains("Copyright", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(l, @"\b(19|20)\d\d\b")) ?? "";
            lines = lines.Take(cut).ToList();
        }
        var body = string.Join("\n", lines);
        var parts = SongText.Split(body);
        if (parts.Count == 0) return null;
        return new Song
        {
            Id = NewId(),
            Title = title,
            Author = author.Replace(" | ", ", "),
            Copyright = copyright.TrimStart('©', ' '),
            Ccli = ccli,
            Parts = parts,
            Arrangements = Arrangement(SongText.PastedOrder(body), parts),
            UpdatedUtc = DateTime.UtcNow,
        };
    }
}
