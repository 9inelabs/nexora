using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace Nexora.Core.Bible;

/// <summary>
/// Reads the Bible file formats found online and turns them into a <see cref="BibleText"/>:
/// OSIS XML, USFM (single file or a .zip of books), Zefania XML, OpenSong XML, the
/// "Holy Bible XML" layout, JSON layouts, SQLite files (NEXORA, MyBible and simple verse tables)
/// and spreadsheets (CSV or tab separated: book, chapter, verse, text).
/// </summary>
public static class BibleImport
{
    public sealed record Result(BibleText Bible, string Format, BibleCheckReport Report, string Hash);

    public static Result Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = Hash(bytes);
        var (bible, format) = Read(path, bytes);
        if (string.IsNullOrWhiteSpace(bible.Abbrev)) bible.Abbrev = GuessAbbrev(Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(bible.Name)) bible.Name = bible.Abbrev;
        bible.Abbrev = CleanAbbrev(bible.Abbrev);
        if (bible.Count == 0) throw new InvalidDataException($"No verses were found in this {format} file.");
        return new Result(bible, format, BibleCheck.Run(bible), hash);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    private static (BibleText, string) Read(string path, byte[] bytes)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".gz")
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return Read(path[..^3], ms.ToArray());
        }
        if (IsSqlite(bytes)) return (ReadSqlite(path, bytes), "SQLite");
        if (ext == ".zip" || (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K'))
        {
            // A zip with one Bible file inside (how most downloads come), or a zip of USFM books.
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var single = zip.Entries.Where(e => e.Length > 0 && Path.GetExtension(e.Name).ToLowerInvariant() is
                ".xml" or ".json" or ".osis" or ".zef" or ".csv" or ".tsv" or ".sqlite" or ".sqlite3" or ".db" or ".nxbible" or ".gz").ToList();
            if (single.Count == 1)
            {
                using var es = single[0].Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var (inner, fmt) = Read(single[0].Name, ms.ToArray());
                return (inner, fmt + " (zip)");
            }
            return (ReadZip(bytes), "USFM (zip)");
        }

        var text = Decode(bytes);
        var head = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (head.StartsWith('{') || head.StartsWith('[')) return (ReadJson(text), "JSON");
        if (head.StartsWith('<')) return ReadXml(text);
        if (Regex.IsMatch(head, @"^\\(id|ide|usfm|h|toc\d|mt\d?|c|v)\b")) return (ReadUsfm([text]), "USFM");
        return (ReadDelimited(text), "spreadsheet");
    }

    // ---------- JSON ----------

    private static BibleText ReadJson(string json)
    {
        var bible = new BibleText();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            bible.Abbrev = Str(root, "abbrev") ?? Str(root, "abbreviation") ?? "";
            bible.Name = Str(root, "name") ?? Str(root, "translation") ?? "";
            bible.Language = Str(root, "language") ?? bible.Language;
            if (bible.Name.Contains(':') && bible.Abbrev.Length == 0)
            {
                // scrollmapper: "KJV: King James Version (1769) ..."
                var i = bible.Name.IndexOf(':');
                bible.Abbrev = bible.Name[..i].Trim();
                bible.Name = bible.Name[(i + 1)..].Trim();
            }
            if (root.TryGetProperty("books", out var books)) ReadJsonBooks(bible, books);
            else if (root.TryGetProperty("verses", out var verses)) ReadJsonRows(bible, verses);
            return bible;
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            var first = root.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && (first.TryGetProperty("chapters", out _) || first.TryGetProperty("name", out _)) && !first.TryGetProperty("verse", out _))
                ReadJsonBooks(bible, root);
            else ReadJsonRows(bible, root);
        }
        return bible;
    }

    private static void ReadJsonBooks(BibleText bible, JsonElement books)
    {
        var index = 0;
        foreach (var b in books.EnumerateArray())
        {
            index++;
            int book;
            JsonElement chapters;
            if (b.ValueKind == JsonValueKind.Array)
            {
                // NEXORA bundled format: books[book][chapter][verse] = text
                book = index;
                chapters = b;
            }
            else
            {
                book = BookFrom(b, index);
                if (!b.TryGetProperty("chapters", out chapters)) continue;
            }
            if (book == 0) continue;

            var ci = 0;
            foreach (var ch in chapters.EnumerateArray())
            {
                ci++;
                if (ch.ValueKind == JsonValueKind.Array)
                {
                    var vi = 0;
                    foreach (var v in ch.EnumerateArray())
                    {
                        vi++;
                        if (v.ValueKind == JsonValueKind.String)
                        {
                            var t = v.GetString()!;
                            // NEXORA bundled format: "" marks a verse printed together with the one before.
                            if (t.Length == 0 && b.ValueKind == JsonValueKind.Array) bible.Join(book, ci, vi);
                            else bible.Add(book, ci, vi, t);
                        }
                        else if (v.ValueKind == JsonValueKind.Object) bible.Add(book, ci, Int(v, "verse") ?? vi, Str(v, "text") ?? "");
                    }
                }
                else if (ch.ValueKind == JsonValueKind.Object)
                {
                    var cn = Int(ch, "chapter") ?? ci;
                    if (!ch.TryGetProperty("verses", out var vs)) continue;
                    var vi = 0;
                    foreach (var v in vs.EnumerateArray())
                    {
                        vi++;
                        if (v.ValueKind == JsonValueKind.String) bible.Add(book, cn, vi, v.GetString()!);
                        else bible.Add(book, cn, Int(v, "verse") ?? vi, Str(v, "text") ?? "");
                    }
                }
            }
        }
    }

    private static void ReadJsonRows(BibleText bible, JsonElement rows)
    {
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var book = BookFrom(r, 0);
            var c = Int(r, "chapter");
            var v = Int(r, "verse");
            var t = Str(r, "text");
            if (book > 0 && c is not null && v is not null && t is not null) bible.Add(book, c.Value, v.Value, t);
        }
    }

    private static int BookFrom(JsonElement e, int fallback)
    {
        foreach (var key in new[] { "book", "book_id", "bookId", "book_number", "bookNumber", "bnumber" })
        {
            if (!e.TryGetProperty(key, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n is >= 1 and <= 66 ? n : 0;
            if (p.ValueKind == JsonValueKind.String) return BookFromText(p.GetString()!);
        }
        foreach (var key in new[] { "name", "book_name", "bookName", "abbrev" })
            if (e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String && BookFromText(p.GetString()!) is > 0 and var b)
                return b;
        return fallback is >= 1 and <= 66 ? fallback : 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out n)) return n;
        return null;
    }

    // ---------- XML ----------

    private static (BibleText, string) ReadXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using (var probe = XmlReader.Create(new StringReader(xml), settings))
        {
            probe.MoveToContent();
            var root = probe.LocalName.ToLowerInvariant();
            if (root is "osis" or "osistext") return (ReadOsis(xml, settings), "OSIS XML");
            if (root == "xmlbible")
            {
                try { return (ReadZefania(xml, settings), "Zefania XML"); }
                catch (XmlException) { return (ReadZefaniaLoose(xml), "Zefania XML (repaired)"); }
            }
        }
        return (ReadGenericXml(xml, settings), "XML");
    }

    private static BibleText ReadOsis(string xml, XmlReaderSettings settings)
    {
        var bible = new BibleText();
        using var r = XmlReader.Create(new StringReader(xml), settings);
        (int B, int C, int V)? current = null;
        var containerDepth = -1;
        var sb = new StringBuilder();
        string? workTitle = null, workId = null, lang = null;

        void Flush()
        {
            if (current is { } k && sb.Length > 0) bible.Add(k.B, k.C, k.V, sb.ToString());
            sb.Clear();
        }

        var skipped = false;
        while (skipped ? !r.EOF : r.Read())
        {
            skipped = false;
            if (r.NodeType == XmlNodeType.Element)
            {
                var name = r.LocalName;
                if (name == "osisText")
                {
                    workId ??= r.GetAttribute("osisIDWork");
                    lang ??= r.GetAttribute("xml:lang");
                }
                else if (name == "title" && workTitle is null && current is null && r.Depth <= 4 && !r.IsEmptyElement)
                {
                    workTitle = r.ReadElementContentAsString().Trim();
                    skipped = true;
                    continue;
                }
                if (name == "note" || (name == "title" && current is not null))
                {
                    if (!r.IsEmptyElement)
                    {
                        r.Skip();
                        skipped = true;
                    }
                    continue;
                }
                if (name == "verse")
                {
                    var eid = r.GetAttribute("eID");
                    if (eid is not null)
                    {
                        Flush();
                        current = null;
                        continue;
                    }
                    var id = r.GetAttribute("osisID") ?? r.GetAttribute("sID");
                    if (id is not null && ParseOsisId(id) is { } key)
                    {
                        Flush();
                        current = key;
                        containerDepth = r.GetAttribute("sID") is null && !r.IsEmptyElement ? r.Depth : -1;
                    }
                }
                else if (name is "lb" or "l" && current is not null) sb.Append(' ');
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (current is not null) sb.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "verse" && containerDepth >= 0 && r.Depth == containerDepth)
                {
                    Flush();
                    current = null;
                    containerDepth = -1;
                }
                else if (r.LocalName is "chapter" && containerDepth < 0 && current is not null)
                {
                    // A milestone verse never closed: end it at the chapter.
                    Flush();
                    current = null;
                }
            }
        }
        Flush();
        bible.Abbrev = workId ?? "";
        bible.Name = workTitle ?? "";
        if (lang is not null) bible.Language = LanguageName(lang);
        return bible;
    }

    private static (int, int, int)? ParseOsisId(string id)
    {
        var first = id.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        var bang = first.IndexOf('!');
        if (bang >= 0) first = first[..bang];
        var colon = first.IndexOf(':');
        if (colon >= 0) first = first[(colon + 1)..];
        var parts = first.Split('.');
        if (parts.Length < 3) return null;
        var book = BibleBooks.FromCode(parts[0]);
        if (book is null || !int.TryParse(parts[1], out var c) || !int.TryParse(parts[2], out var v)) return null;
        return (book.Number, c, v);
    }

    private static BibleText ReadZefania(string xml, XmlReaderSettings settings)
    {
        var bible = new BibleText();
        using var r = XmlReader.Create(new StringReader(xml), settings);
        int book = 0, chapter = 0;
        var skipped = false;
        while (skipped ? !r.EOF : r.Read())
        {
            skipped = false;
            if (r.NodeType != XmlNodeType.Element) continue;
            switch (r.LocalName.ToUpperInvariant())
            {
                case "TITLE" when bible.Name.Length == 0:
                    bible.Name = r.ReadElementContentAsString().Trim();
                    skipped = true;
                    break;
                case "IDENTIFIER" when bible.Abbrev.Length == 0:
                    bible.Abbrev = r.ReadElementContentAsString().Trim();
                    skipped = true;
                    break;
                case "LANGUAGE":
                    bible.Language = LanguageName(r.ReadElementContentAsString().Trim());
                    skipped = true;
                    break;
                case "BIBLEBOOK":
                    book = int.TryParse(r.GetAttribute("bnumber"), out var b) ? b : BookFromText(r.GetAttribute("bname") ?? r.GetAttribute("bsname") ?? "");
                    break;
                case "CHAPTER":
                    chapter = int.TryParse(r.GetAttribute("cnumber"), out var c) ? c : chapter + 1;
                    break;
                case "VERS":
                    if (int.TryParse(r.GetAttribute("vnumber"), out var v) && !r.IsEmptyElement)
                        bible.Add(book, chapter, v, InnerText(r));
                    break;
            }
        }
        return bible;
    }

    /// <summary>
    /// Reads a damaged Zefania file (tags out of place, broken attributes) by scanning for the
    /// book, chapter and verse tags in order. Book numbers must run 1 to 66: a number that is out
    /// of order and a name that does not match are replaced by the next book.
    /// </summary>
    private static BibleText ReadZefaniaLoose(string xml)
    {
        var bible = new BibleText();
        var m = Regex.Match(xml, @"<XMLBIBLE[^>]*biblename=""([^""]*)""", RegexOptions.IgnoreCase);
        if (m.Success) bible.Name = m.Groups[1].Value;
        int book = 0, chapter = 0;
        var tokens = new Regex(@"<BIBLEBOOK\b(?<ba>[^>]*)>|<CHAPTER\b(?<ca>[^>]*)>|<VERS\b(?<va>[^>]*)>(?<vt>.*?)</VERS>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match t in tokens.Matches(xml))
        {
            if (t.Groups["ba"].Success)
            {
                var attrs = t.Groups["ba"].Value;
                var num = Regex.Match(attrs, @"bnumber=""(\d+)""") is { Success: true } nm ? int.Parse(nm.Groups[1].Value) : 0;
                var byName = Regex.Match(attrs, @"bname=""([^""<>]+)""") is { Success: true } bm ? BookFromText(bm.Groups[1].Value) : 0;
                book = num == book + 1 || (num > book && num == byName) ? num : byName > book ? byName : book + 1;
                chapter = 0;
            }
            else if (t.Groups["ca"].Success)
            {
                chapter = Regex.Match(t.Groups["ca"].Value, @"cnumber=""(\d+)""") is { Success: true } cm ? int.Parse(cm.Groups[1].Value) : chapter + 1;
            }
            else if (Regex.Match(t.Groups["va"].Value, @"vnumber=""(\d+)""") is { Success: true } vm)
            {
                bible.Add(book, chapter, int.Parse(vm.Groups[1].Value), LooseText(t.Groups["vt"].Value));
            }
        }
        return bible;
    }

    private static string LooseText(string inner)
    {
        // Keep bracketed amplifications (the Amplified Bible's [ ] notes are part of the reading); drop other notes.
        var s = Regex.Replace(inner, @"<NOTE\b[^>]*>(.*?)</NOTE>", n => n.Groups[1].Value.TrimStart().StartsWith('[') ? n.Groups[1].Value : "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(s);
    }

    /// <summary>OpenSong (bible/b/c/v) and "Holy Bible XML" (bible/testament/book/chapter/verse).</summary>
    private static BibleText ReadGenericXml(string xml, XmlReaderSettings settings)
    {
        var bible = new BibleText();
        using var r = XmlReader.Create(new StringReader(xml), settings);
        int book = 0, chapter = 0, bookIndex = 0;
        r.MoveToContent();
        bible.Name = r.GetAttribute("translation") ?? r.GetAttribute("name") ?? "";
        bible.Abbrev = r.GetAttribute("abbreviation") ?? r.GetAttribute("abbrev") ?? "";
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            var name = r.LocalName.ToLowerInvariant();
            switch (name)
            {
                case "b" or "book":
                    bookIndex++;
                    var n = r.GetAttribute("n") ?? r.GetAttribute("name") ?? r.GetAttribute("number") ?? r.GetAttribute("bnumber");
                    book = int.TryParse(n, out var bn) ? bn : n is not null ? BookFromText(n) : bookIndex;
                    if (book is < 1 or > 66) book = bookIndex <= 66 ? bookIndex : 0;
                    chapter = 0;
                    break;
                case "c" or "chapter":
                    chapter = int.TryParse(r.GetAttribute("n") ?? r.GetAttribute("number") ?? r.GetAttribute("cnumber"), out var c) ? c : chapter + 1;
                    break;
                case "v" or "verse":
                    var vn = r.GetAttribute("n") ?? r.GetAttribute("number") ?? r.GetAttribute("vnumber");
                    if (int.TryParse(vn, out var v) && !r.IsEmptyElement) bible.Add(book, chapter, v, InnerText(r));
                    break;
            }
        }
        return bible;
    }

    private static string InnerText(XmlReader r)
    {
        var depth = r.Depth;
        var sb = new StringBuilder();
        var skipped = false;
        while (skipped ? !r.EOF : r.Read())
        {
            skipped = false;
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;
            if (r.NodeType == XmlNodeType.Element && !r.IsEmptyElement && r.LocalName.ToLowerInvariant() is "note" or "footnote" or "gr" or "xref" or "sup")
            {
                var note = r.ReadInnerXml();
                skipped = true;
                // The Amplified Bible's bracketed amplifications are part of the reading.
                if (note.TrimStart().StartsWith('[')) sb.Append(Regex.Replace(note, "<[^>]+>", ""));
                continue;
            }
            if (r.NodeType == XmlNodeType.Element && r.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase)) sb.Append(' ');
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) sb.Append(r.Value);
        }
        return sb.ToString();
    }

    // ---------- USFM ----------

    private static BibleText ReadZip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var texts = new List<string>();
        foreach (var e in zip.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (ext is not (".usfm" or ".sfm" or ".txt" or ".ptx")) continue;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            texts.Add(Decode(ms.ToArray()));
        }
        if (texts.Count == 0) throw new InvalidDataException("The zip file has no USFM books (.usfm or .sfm) inside.");
        return ReadUsfm(texts);
    }

    private static readonly Regex UsfmNotes = new(@"\\(f|fe|x|ef|ex)\s.*?\\\1\*", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex UsfmWord = new(@"\\\+?w\s+([^|\\]*?)(\|[^\\]*?)?\\\+?w\*", RegexOptions.Compiled);
    private static readonly Regex UsfmMarker = new(@"\\\+?[a-z]+\d*\*?", RegexOptions.Compiled);

    private static BibleText ReadUsfm(IEnumerable<string> files)
    {
        var bible = new BibleText();
        foreach (var file in files)
        {
            int book = 0, chapter = 0, verse = 0;
            var sb = new StringBuilder();
            void Flush()
            {
                if (book > 0 && chapter > 0 && verse > 0 && sb.Length > 0) bible.Add(book, chapter, verse, CleanUsfm(sb.ToString()));
                sb.Clear();
            }
            var text = UsfmNotes.Replace(file, "");
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = Regex.Match(line, @"^\s*\\(\w+\d*)\s*(.*)$");
                if (!m.Success)
                {
                    if (verse > 0) sb.Append(' ').Append(line);
                    continue;
                }
                var marker = m.Groups[1].Value;
                var rest = m.Groups[2].Value;
                switch (marker)
                {
                    case "id":
                        Flush();
                        book = BibleBooks.FromCode(rest.Split(' ', 2)[0])?.Number ?? 0;
                        chapter = verse = 0;
                        break;
                    case "c":
                        Flush();
                        chapter = int.TryParse(rest.Split(' ', 2)[0], out var c) ? c : chapter + 1;
                        verse = 0;
                        break;
                    case "s" or "s1" or "s2" or "s3" or "ms" or "ms1" or "mt" or "mt1" or "mt2" or "h" or "toc1" or "toc2" or "toc3" or "ide" or "rem" or "r" or "d" or "sp" or "cl" or "cp" or "mr" or "sr":
                        break;
                    default:
                        // A line may hold several verses: "\v 1 text \v 2 text".
                        var segs = Regex.Split(rest.Insert(0, marker == "v" ? "\\v " : ""), @"(?=\\v\s+\d)");
                        if (marker != "v") sb.Append(' ').Append(rest);
                        else
                        {
                            foreach (var seg in segs.Where(s => s.Length > 0))
                            {
                                var vm = Regex.Match(seg, @"^\\v\s+(\d+)[\w-]*\s*(.*)$", RegexOptions.Singleline);
                                if (vm.Success)
                                {
                                    Flush();
                                    verse = int.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture);
                                    sb.Append(vm.Groups[2].Value);
                                }
                                else sb.Append(' ').Append(seg);
                            }
                        }
                        break;
                }
            }
            Flush();
        }
        return bible;
    }

    private static string CleanUsfm(string s)
    {
        s = UsfmWord.Replace(s, "$1");
        s = UsfmMarker.Replace(s, " ");
        return s;
    }

    // ---------- SQLite ----------

    private static bool IsSqlite(byte[] b) => b.Length > 16 && Encoding.ASCII.GetString(b, 0, 15) == "SQLite format 3";

    private static readonly int[] MyBibleNumbers =
    [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 190, 220, 230, 240, 250, 260, 290, 300, 310, 330,
        340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570,
        580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730,
    ];

    private static BibleText ReadSqlite(string path, byte[] bytes)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "nexora-import-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            var bible = new BibleText();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                db.Open();
                var tables = Query(db, "SELECT name FROM sqlite_master WHERE type='table'").Select(r => r[0]!.ToString()!).ToList();

                if (tables.Contains("meta") && tables.Contains("verses") && Columns(db, "verses").Contains("id"))
                {
                    // NEXORA's own file.
                    foreach (var r in Query(db, "SELECT key, value FROM meta"))
                    {
                        if ((string)r[0]! == "abbrev") bible.Abbrev = (string)r[1]!;
                        if ((string)r[0]! == "name") bible.Name = (string)r[1]!;
                        if ((string)r[0]! == "language") bible.Language = (string)r[1]!;
                    }
                    foreach (var r in Query(db, "SELECT book, chapter, verse, text FROM verses"))
                        bible.Add(ToInt(r[0]), ToInt(r[1]), ToInt(r[2]), (string)r[3]!);
                    return bible;
                }

                if (tables.Contains("verses") && Columns(db, "verses").Contains("book_number"))
                {
                    // MyBible module.
                    if (tables.Contains("info"))
                        foreach (var r in Query(db, "SELECT name, value FROM info"))
                        {
                            var k = r[0]?.ToString();
                            if (k == "description") bible.Name = r[1]?.ToString() ?? "";
                            if (k == "language") bible.Language = LanguageName(r[1]?.ToString() ?? "");
                        }
                    foreach (var r in Query(db, "SELECT book_number, chapter, verse, text FROM verses"))
                    {
                        var idx = Array.IndexOf(MyBibleNumbers, ToInt(r[0]));
                        if (idx >= 0) bible.Add(idx + 1, ToInt(r[1]), ToInt(r[2]), StripTags(r[3]?.ToString() ?? ""));
                    }
                    return bible;
                }

                // Any table with book/chapter/verse/text columns (for example scrollmapper's "KJV_verses").
                foreach (var t in tables)
                {
                    var cols = Columns(db, t);
                    var bookCol = cols.FirstOrDefault(c => c is "book" or "book_id" or "b" or "booknumber" or "book_num");
                    var chCol = cols.FirstOrDefault(c => c is "chapter" or "c" or "chapternumber");
                    var vCol = cols.FirstOrDefault(c => c is "verse" or "v" or "versenumber");
                    var tCol = cols.FirstOrDefault(c => c is "text" or "t" or "scripture" or "content");
                    if (bookCol is null || chCol is null || vCol is null || tCol is null) continue;
                    foreach (var r in Query(db, $"SELECT \"{bookCol}\", \"{chCol}\", \"{vCol}\", \"{tCol}\" FROM \"{t.Replace("\"", "")}\""))
                    {
                        var book = r[0] is string s ? BookFromText(s) : ToInt(r[0]);
                        bible.Add(book, ToInt(r[1]), ToInt(r[2]), StripTags(r[3]?.ToString() ?? ""));
                    }
                    if (bible.Count > 0)
                    {
                        var m = Regex.Match(t, @"^([A-Za-z0-9]+)_verses$", RegexOptions.IgnoreCase);
                        if (m.Success) bible.Abbrev = m.Groups[1].Value.ToUpperInvariant();
                        return bible;
                    }
                }
            }
            throw new InvalidDataException("This database has no table of book, chapter, verse and text.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(tmp); } catch { }
        }
    }

    private static List<object?[]> Query(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    private static List<string> Columns(SqliteConnection db, string table) =>
        Query(db, $"PRAGMA table_info(\"{table.Replace("\"", "")}\")").Select(r => r[1]!.ToString()!.ToLowerInvariant()).ToList();

    private static int ToInt(object? o) => o switch
    {
        long l => (int)l,
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => 0,
    };

    private static readonly Regex Tags = new(@"<(S|f|n|e|h)>.*?</\1>|<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string StripTags(string s) => Tags.Replace(s, m => m.Value.StartsWith("<pb") || m.Value.StartsWith("<br") ? " " : "");

    // ---------- Spreadsheet ----------

    private static BibleText ReadDelimited(string text)
    {
        var bible = new BibleText();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return bible;
        var sample = string.Join("\n", lines.Take(20));
        var delim = sample.Count(c => c == '\t') >= sample.Count(c => c == ',') ? '\t' : sample.Count(c => c == ',') >= sample.Count(c => c == ';') ? ',' : ';';
        if (!sample.Contains(delim) && sample.Contains('|')) delim = '|';

        foreach (var line in lines)
        {
            var cells = SplitCsv(line, delim);
            if (cells.Count < 4)
            {
                // "Gen 1:1 In the beginning..." or "John 3:16<tab>text"
                var m = Regex.Match(line, @"^\s*(\S+(?:\s\S+)?)\s+(\d+):(\d+)\s+(.+)$");
                if (m.Success && BookFromText(m.Groups[1].Value) is > 0 and var bk)
                    bible.Add(bk, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value);
                continue;
            }
            var book = int.TryParse(cells[0], out var bn) ? (bn is >= 1 and <= 66 ? bn : 0) : BookFromText(cells[0]);
            if (book == 0 || !int.TryParse(cells[1], out var c) || !int.TryParse(cells[2], out var v)) continue; // header row
            bible.Add(book, c, v, string.Join(delim.ToString(), cells.Skip(3)));
        }
        return bible;
    }

    private static List<string> SplitCsv(string line, char delim)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) quoted = true;
            else if (ch == delim) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    // ---------- Helpers ----------

    /// <summary>A book from a name or code in a file ("Genesis", "Gen", "GEN", "1 Cor", "I Samuel").</summary>
    public static int BookFromText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (BibleBooks.FromCode(s) is { } byCode) return byCode.Number;
        var t = s.Trim();
        if (t.Equals("Revelation of John", StringComparison.OrdinalIgnoreCase)) return 66;
        var p = ReferenceParser.Parse(t);
        return p.Reference?.Book.Number ?? 0;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string GuessAbbrev(string fileName)
    {
        var m = Regex.Match(fileName, @"\b([A-Z]{2,6})\b");
        if (m.Success) return m.Groups[1].Value;
        var letters = new string(fileName.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length == 0 ? "BIBLE" : letters[..Math.Min(6, letters.Length)].ToUpperInvariant();
    }

    public static string CleanAbbrev(string s)
    {
        var t = new string(s.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (t.Length == 0) t = "BIBLE";
        return t.Length > 10 ? t[..10] : t;
    }

    private static string LanguageName(string code)
    {
        if (code.Length is 2 or 3)
        {
            try { return CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (CultureNotFoundException) { }
        }
        return code;
    }
}
