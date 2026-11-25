namespace Nexora.Core.Bible;

/// <summary>A whole translation in memory, as read by an importer before it is saved.</summary>
public sealed class BibleText
{
    private readonly Dictionary<(int Book, int Chapter, int Verse), string> _verses = new();

    public string Abbrev { get; set; } = "";
    public string Name { get; set; } = "";
    public string Language { get; set; } = "English";

    public int Count => _verses.Count;

    public void Add(int book, int chapter, int verse, string text)
    {
        if (book is < 1 or > 66 || chapter < 1 || verse < 1) return;
        text = Clean(text);
        if (text.Length == 0) return;
        var key = (book, chapter, verse);
        // Some files repeat a verse number for a second paragraph: join them.
        _verses[key] = _verses.TryGetValue(key, out var existing) ? existing + " " + text : text;
    }

    /// <summary>
    /// Marks a verse as printed together with the verse before it (for example the NLT prints
    /// Numbers 1:20-21 as one). It is stored with empty text and shown with the verse before.
    /// </summary>
    public void Join(int book, int chapter, int verse)
    {
        if (book is < 1 or > 66 || chapter < 1 || verse < 2) return;
        _verses.TryAdd((book, chapter, verse), "");
    }

    /// <summary>Replaces one verse (used for corrections).</summary>
    public void Set(int book, int chapter, int verse, string text) => _verses[(book, chapter, verse)] = Clean(text);

    public void Remove(int book, int chapter, int verse) => _verses.Remove((book, chapter, verse));

    /// <summary>The verse text; "" for a verse joined to the one before; null when missing.</summary>
    public string? Get(int book, int chapter, int verse) => _verses.GetValueOrDefault((book, chapter, verse));

    public IEnumerable<(int Book, int Chapter, int Verse, string Text)> All() =>
        _verses.OrderBy(kv => kv.Key.Book).ThenBy(kv => kv.Key.Chapter).ThenBy(kv => kv.Key.Verse)
            .Select(kv => (kv.Key.Book, kv.Key.Chapter, kv.Key.Verse, kv.Value));

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Hebrew letter headings some files put inside verses (Psalm 119, Lamentations): "&u05D0; (Alef)".
        if (s.Contains("&u", StringComparison.Ordinal))
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(&u[0-9A-Fa-f]{4};\s*)+(\([A-Z][a-z]+\))?(\s*&u[0-9A-Fa-f]{4};)*", " ");
        var t = s.Replace('¶', ' ').Replace(' ', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Trim();
    }
}

/// <summary>The result of counting a translation against Genesis 1:1 to Revelation 22:21.</summary>
public sealed record BibleCheckReport(
    int Expected,
    int Present,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<int> MissingBooks,
    IReadOnlyList<string> Omitted,
    IReadOnlyList<string> Joined)
{
    /// <summary>No verse is missing. Extra verses (a few translations number differently) are allowed.</summary>
    public bool Complete => Missing.Count == 0;

    public string Summary
    {
        get
        {
            static string List(IReadOnlyList<string> l) => string.Join(", ", l.Take(3)) + (l.Count > 3 ? "…" : "");
            var note = (Joined.Count > 0 ? $" {Joined.Count} {(Joined.Count == 1 ? "verse is" : "verses are")} printed together with the verse before ({List(Joined)})." : "")
                     + (Omitted.Count > 0 ? $" {Omitted.Count} {(Omitted.Count == 1 ? "verse is" : "verses are")} left out as in most modern translations ({List(Omitted)})." : "")
                     + (Extra.Count > 0 ? $" {Extra.Count} extra {(Extra.Count == 1 ? "verse is" : "verses are")} numbered differently ({List(Extra)})." : "");
            if (Complete) return $"Complete from Genesis 1:1 to Revelation 22:21.{note}";
            if (MissingBooks.Count == 66) return "No verses were found in this file.";
            var books = MissingBooks.Count > 0 ? $" Missing books: {string.Join(", ", MissingBooks.Take(6).Select(b => BibleBooks.Get(b).Name))}{(MissingBooks.Count > 6 ? "…" : "")}." : "";
            return $"{Missing.Count:N0} of {Expected:N0} verses missing, for example {string.Join(", ", Missing.Take(5))}.{books}";
        }
    }
}

public static class BibleCheck
{
    /// <summary>
    /// Verses that many modern translations (for example NIV, ESV, BBE) leave out or move to a footnote,
    /// following older manuscripts. Leaving them out is not counted as missing.
    /// </summary>
    public static readonly IReadOnlySet<(int Book, int Chapter, int Verse)> OftenOmitted = new HashSet<(int, int, int)>
    {
        (40, 12, 47), (40, 17, 21), (40, 18, 11), (40, 23, 14), (41, 7, 16), (41, 9, 44), (41, 9, 46), (41, 11, 26), (41, 15, 28),
        (42, 17, 36), (42, 23, 17), (43, 5, 4), (44, 8, 37), (44, 15, 34), (44, 24, 7), (44, 28, 29), (45, 16, 24),
        // Also footnoted in the RSV (1952).
        (40, 21, 44), (42, 22, 43), (42, 22, 44), (42, 24, 12), (42, 24, 40),
    };

    public static BibleCheckReport Run(Func<int, int, int, bool> has, IEnumerable<(int Book, int Chapter, int Verse)> present,
        Func<int, int, int, bool>? joined = null)
    {
        var missing = new List<string>();
        var omitted = new List<string>();
        var joinedList = new List<string>();
        var missingBooks = new List<int>();
        var count = 0;
        foreach (var b in BibleBooks.All)
        {
            var bookMissing = 0;
            var bookTotal = 0;
            for (var c = 1; c <= b.Chapters; c++)
            for (var v = 1; v <= b.VersesIn(c); v++)
            {
                bookTotal++;
                if (has(b.Number, c, v))
                {
                    count++;
                    if (joined?.Invoke(b.Number, c, v) == true) joinedList.Add($"{b.Name} {c}:{v}");
                }
                else if (OftenOmitted.Contains((b.Number, c, v))) { count++; omitted.Add($"{b.Name} {c}:{v}"); }
                else
                {
                    bookMissing++;
                    if (missing.Count < 2000) missing.Add($"{b.Name} {c}:{v}");
                }
            }
            if (bookMissing == bookTotal) missingBooks.Add(b.Number);
        }
        var extra = present
            .Where(p => BibleBooks.TryGet(p.Book) is not { } bk || p.Chapter > bk.Chapters || p.Verse > bk.VersesIn(p.Chapter))
            .Select(p => BibleBooks.TryGet(p.Book) is { } bk ? $"{bk.Name} {p.Chapter}:{p.Verse}" : $"book {p.Book}")
            .Take(200).ToList();
        return new BibleCheckReport(BibleBooks.TotalVerses, count, missing, extra, missingBooks, omitted, joinedList);
    }

    public static BibleCheckReport Run(BibleText t) =>
        Run((b, c, v) => t.Get(b, c, v) is not null, t.All().Select(x => (x.Book, x.Chapter, x.Verse)), (b, c, v) => t.Get(b, c, v) == "");
}

/// <summary>Writes NEXORA's bundled Bible format: gzip'd JSON, books[book][chapter][verse] = text.</summary>
public static class BibleBundle
{
    public static void Write(BibleText bible, string path)
    {
        var books = new List<List<List<string?>>>();
        var all = bible.All().ToList();
        for (var b = 1; b <= 66; b++)
        {
            var inBook = all.Where(x => x.Book == b).ToList();
            var chapters = new List<List<string?>>();
            var maxC = inBook.Count == 0 ? 0 : inBook.Max(x => x.Chapter);
            for (var c = 1; c <= maxC; c++)
            {
                var inCh = inBook.Where(x => x.Chapter == c).ToDictionary(x => x.Verse, x => x.Text);
                var maxV = inCh.Count == 0 ? 0 : inCh.Keys.Max();
                chapters.Add(Enumerable.Range(1, maxV).Select(v => inCh.TryGetValue(v, out var t) ? t : null).ToList());
            }
            books.Add(chapters);
        }
        var doc = new { abbrev = bible.Abbrev, name = bible.Name, language = bible.Language, books };
        using var fs = File.Create(path);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.SmallestSize);
        System.Text.Json.JsonSerializer.Serialize(gz, doc, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
