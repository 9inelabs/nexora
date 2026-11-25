using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Core.Bible;

/// <summary>A run of verses. EndChapter/EndVerse are inclusive. Verse 0 never occurs.</summary>
public readonly record struct VerseRange(int StartChapter, int StartVerse, int EndChapter, int EndVerse)
{
    public bool IsSingleVerse => StartChapter == EndChapter && StartVerse == EndVerse;
}

/// <summary>A book and one or more verse ranges in it, for example John 3:16-18, 20.</summary>
public sealed record BibleReference(BibleBook Book, IReadOnlyList<VerseRange> Ranges)
{
    public VerseRange First => Ranges[0];

    /// <summary>Every verse in order, as (chapter, verse).</summary>
    public IEnumerable<(int Chapter, int Verse)> Verses()
    {
        foreach (var r in Ranges)
        {
            for (var c = r.StartChapter; c <= r.EndChapter; c++)
            {
                var from = c == r.StartChapter ? r.StartVerse : 1;
                var to = c == r.EndChapter ? r.EndVerse : Book.VersesIn(c);
                for (var v = from; v <= to; v++) yield return (c, v);
            }
        }
    }

    public override string ToString() => Format(Book, Ranges);

    public static string Format(BibleBook book, IReadOnlyList<VerseRange> ranges)
    {
        var sb = new StringBuilder(book.Name);
        var single = book.Chapters == 1;
        int? lastChapter = null;
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            var wholeChapters = r.StartVerse == 1 && r.EndVerse == book.VersesIn(r.EndChapter) && !single;
            if (i == 0) sb.Append(' ');
            else sb.Append(lastChapter == r.StartChapter && !wholeChapters ? ", " : "; ");

            if (wholeChapters)
            {
                sb.Append(r.StartChapter);
                if (r.EndChapter != r.StartChapter) sb.Append('-').Append(r.EndChapter);
            }
            else
            {
                if (single) sb.Append(r.StartVerse);
                else if (lastChapter == r.StartChapter && i > 0) sb.Append(r.StartVerse);
                else sb.Append(r.StartChapter).Append(':').Append(r.StartVerse);

                if (r.EndChapter != r.StartChapter) sb.Append('-').Append(r.EndChapter).Append(':').Append(r.EndVerse);
                else if (r.EndVerse != r.StartVerse) sb.Append('-').Append(r.EndVerse);
            }
            lastChapter = r.EndChapter;
        }
        return sb.ToString();
    }
}

public sealed record ParseResult(BibleReference? Reference, string? Error, IReadOnlyList<BibleBook> Suggestions)
{
    public bool Ok => Reference is not null;
}

/// <summary>
/// Reads Bible references typed the way people speak: "jn 3 16", "john 3:16", "Jn3.16",
/// "1 cor 13 4-7", "1co 13:4-7", "ps 23", "rev 22 21", "i john 1 9", "john 3:16-4:2",
/// "rom 8:28, 31". Common abbreviations and small misspellings are understood.
/// </summary>
public static class ReferenceParser
{
    private static readonly Dictionary<string, BibleBook> Exact = BuildExact();
    private static readonly (string Key, BibleBook Book)[] Keys = BuildKeys();

    private static readonly Regex Shape = new(
        @"^\s*(?<pre>[123]|iii|ii|i|first|second|third|1st|2nd|3rd)?\s*(?<name>[a-z][a-z .']*?)\s*\.?\s*(?<rest>\d[\d\s:.,;v\-–—]*)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParseResult Parse(string input)
    {
        var text = Normalize(input);
        if (text.Length == 0) return new(null, "Type a book, chapter and verse.", []);

        var m = Shape.Match(text);
        if (!m.Success) return new(null, "That does not look like a Bible reference.", []);

        var pre = m.Groups["pre"].Value;
        var name = m.Groups["name"].Value;
        var rest = m.Groups["rest"].Value.Trim();

        // "isa" and "isaiah" also match pre="i" + name="sa": a whole word that is a book wins.
        BibleBook? book;
        IReadOnlyList<BibleBook> suggestions;
        if (pre.Length > 0 && !char.IsDigit(pre[0]) && Exact.TryGetValue(Squash(pre + name), out var whole))
            (book, suggestions) = (whole, [whole]);
        else
        {
            (book, suggestions) = FindBook(PrefixDigit(pre) + Squash(name));
            if (book is null && pre.Length > 0 && !char.IsDigit(pre[0]))
            {
                var (b2, s2) = FindBook(Squash(pre + name));
                if (b2 is not null || s2.Count > 0) (book, suggestions) = (b2, s2);
            }
        }

        if (book is null)
        {
            if (suggestions.Count > 1)
                return new(null, "Did you mean " + string.Join(", ", suggestions.Take(5).Select(b => b.Name)) + "?", suggestions);
            return new(null, $"No book called \"{(pre + " " + name).Trim()}\".", []);
        }

        if (rest.Length == 0)
            return new(new BibleReference(book, [new VerseRange(1, 1, 1, book.VersesIn(1))]), null, [book]);

        var ranges = ParseNumbers(book, rest, out var error);
        if (ranges is null) return new(null, error, [book]);
        return new(new BibleReference(book, ranges), null, [book]);
    }

    /// <summary>Books matching what has been typed so far ("jo" offers John, Job, Joel, Jonah, Joshua).</summary>
    public static IReadOnlyList<BibleBook> Suggest(string input, int max = 6)
    {
        var text = Normalize(input);
        var m = Shape.Match(text);
        string key;
        if (m.Success) key = PrefixDigit(m.Groups["pre"].Value) + Squash(m.Groups["name"].Value);
        else key = Squash(Regex.Replace(text, @"[\d:.\-\s]+$", ""));
        if (key.Length == 0) return [];
        var found = FindBook(key);
        if (found.Book is not null && m.Groups["rest"].Success) return [found.Book];
        var list = found.Suggestions.ToList();
        if (found.Book is not null && !list.Contains(found.Book)) list.Insert(0, found.Book);
        return list.Take(max).ToList();
    }

    /// <summary>
    /// Finds every reference in pasted sermon notes, one per line or several in a line
    /// ("Read John 3:16 and Romans 8:28"). Returns the reference and the rest of the line as a note.
    /// </summary>
    public static IReadOnlyList<(BibleReference Reference, string Note)> ExtractAll(string text)
    {
        var results = new List<(BibleReference, string)>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Whole line first ("1 cor 13 4-7").
            var whole = Parse(line);
            if (whole.Ok)
            {
                results.Add((whole.Reference!, ""));
                continue;
            }

            var found = new List<(BibleReference Ref, Match M)>();
            var pos = 0;
            while (pos < line.Length)
            {
                var m = Candidate.Match(line, pos);
                if (!m.Success) break;
                var p = Parse(m.Value);
                if (p.Ok)
                {
                    found.Add((p.Reference!, m));
                    pos = m.Index + m.Length;
                }
                else pos = m.Index + 1; // "and 1 Cor 13": try again from the next letter
            }
            if (found.Count == 0) continue;
            var note = found.Count == 1
                ? line.Remove(found[0].M.Index, found[0].M.Length).Trim(' ', '-', '–', '—', ':', ',', '.', '·', '(', ')').Trim()
                : "";
            foreach (var f in found) results.Add((f.Ref, note));
        }
        return results;
    }

    private static readonly Regex Candidate = new(
        @"(?<![A-Za-z])(?:(?:[123]|I{1,3})\s*)?[A-Za-z][A-Za-z]+\.?(?:\s+of\s+[A-Za-z]+)?\s*\d+(?:\s*[:.]\s*\d+(?:\s*[-–—]\s*\d+(?:\s*[:.]\s*\d+)?)?(?:\s*,\s*\d+(?:\s*[-–—]\s*\d+)?)*|\s+\d+(?:\s*[-–—]\s*\d+)?(?=\s|$))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------- Numbers ----------

    private static List<VerseRange>? ParseNumbers(BibleBook book, string rest, out string? error)
    {
        error = null;
        var single = book.Chapters == 1;
        var s = Regex.Replace(rest, @"[–—]", "-");
        s = Regex.Replace(s, @"\s*-\s*", "-");
        s = Regex.Replace(s, @"\s*[:.v]\s*", ":");
        s = Regex.Replace(s, @"\s*[,;]\s*", ",");
        s = Regex.Replace(s, @"\s+", ":");   // "3 16" means 3:16
        s = s.Trim(':', ',', '-');

        var ranges = new List<VerseRange>();
        int? currentChapter = null;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (ends.Length is 0 or > 2) { error = "That reference has too many dashes."; return null; }
            var a = ends[0].Split(':', StringSplitOptions.RemoveEmptyEntries).Select(ParseInt).ToArray();
            var b = ends.Length == 2 ? ends[1].Split(':', StringSplitOptions.RemoveEmptyEntries).Select(ParseInt).ToArray() : null;
            if (a.Length is 0 or > 2 || a.Any(x => x <= 0) || (b is not null && (b.Length is 0 or > 2 || b.Any(x => x <= 0))))
            {
                error = "Chapters and verses start at 1.";
                return null;
            }

            VerseRange r;
            if (single)
            {
                // Jude 3, Jude 1:3, Jude 3-5
                if ((a.Length == 2 && a[0] != 1) || (b is { Length: 2 } && b[0] != 1))
                {
                    error = $"{book.Name} has only one chapter.";
                    return null;
                }
                var sv = a[^1];
                var ev = b is null ? sv : b[^1];
                r = new VerseRange(1, sv, 1, ev);
            }
            else if (a.Length == 2)
            {
                currentChapter = a[0];
                if (b is null) r = new VerseRange(a[0], a[1], a[0], a[1]);
                else if (b.Length == 1) r = new VerseRange(a[0], a[1], a[0], b[0]);
                else r = new VerseRange(a[0], a[1], b[0], b[1]);
            }
            else if (currentChapter is { } cc && ranges.Count > 0)
            {
                // "john 3:16, 18" or "3:16, 18-20": extra verses in the same chapter.
                if (b is { Length: 2 }) r = new VerseRange(cc, a[0], b[0], b[1]);
                else r = new VerseRange(cc, a[0], cc, b is null ? a[0] : b[0]);
            }
            else
            {
                // "ps 23" or "ps 23-24": whole chapters; "john 3-4:2": chapter 3 to 4:2.
                if (b is { Length: 2 }) r = new VerseRange(a[0], 1, b[0], b[1]);
                else
                {
                    var endCh = b is null ? a[0] : b[0];
                    r = new VerseRange(a[0], 1, endCh, book.VersesIn(endCh));
                }
                currentChapter = r.EndChapter;
            }

            if (!Validate(book, ref r, out error)) return null;
            ranges.Add(r);
            currentChapter = r.EndChapter;
        }
        if (ranges.Count == 0) { error = "Type a chapter and verse."; return null; }
        return ranges;
    }

    private static bool Validate(BibleBook book, ref VerseRange r, out string? error)
    {
        error = null;
        if (r.StartChapter > book.Chapters || r.EndChapter > book.Chapters)
        {
            error = book.Chapters == 1 ? $"{book.Name} has only one chapter." : $"{book.Name} has {book.Chapters} chapters.";
            return false;
        }
        if (r.StartVerse > book.VersesIn(r.StartChapter))
        {
            error = $"{book.Name} {r.StartChapter} has {book.VersesIn(r.StartChapter)} verses.";
            return false;
        }
        // An end verse past the chapter end is trimmed ("ps 23:1-10" shows the 6 verses there are).
        if (r.EndVerse > book.VersesIn(r.EndChapter)) r = r with { EndVerse = book.VersesIn(r.EndChapter) };
        if (r.EndChapter < r.StartChapter || (r.EndChapter == r.StartChapter && r.EndVerse < r.StartVerse))
        {
            error = "The end of the range is before the start.";
            return false;
        }
        return true;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : -1;

    // ---------- Books ----------

    private static (BibleBook? Book, IReadOnlyList<BibleBook> Suggestions) FindBook(string key)
    {
        if (key.Length == 0) return (null, []);
        if (Exact.TryGetValue(key, out var exact)) return (exact, [exact]);

        // Unique prefix of a name or abbreviation: "phile" is Philemon, "jo" is ambiguous.
        var prefixed = Keys.Where(k => k.Key.StartsWith(key, StringComparison.Ordinal))
            .Select(k => k.Book).Distinct().OrderBy(b => b.Number).ToList();
        if (prefixed.Count == 1) return (prefixed[0], prefixed);
        if (prefixed.Count > 1) return (null, prefixed);

        // Small misspellings: "mathew", "revalations", "genisis".
        if (key.Length >= 4)
        {
            var max = key.Length >= 7 ? 2 : 1;
            var close = Keys
                .Select(k => (k.Book, D: Distance(key, k.Key)))
                .Where(x => x.D <= max)
                .GroupBy(x => x.Book).Select(g => (Book: g.Key, D: g.Min(x => x.D)))
                .OrderBy(x => x.D).ThenBy(x => x.Book.Number).ToList();
            if (close.Count == 1 || (close.Count > 1 && close[0].D < close[1].D)) return (close[0].Book, [close[0].Book]);
            if (close.Count > 1) return (null, close.Select(x => x.Book).ToList());
        }
        return (null, []);
    }

    private static Dictionary<string, BibleBook> BuildExact()
    {
        var d = new Dictionary<string, BibleBook>(StringComparer.Ordinal);
        foreach (var b in BibleBooks.All)
        {
            d.TryAdd(Squash(b.Name), b);
            d.TryAdd(Squash(b.ShortName), b);
            d.TryAdd(Squash(b.Osis), b);
            d.TryAdd(Squash(b.Usfm), b);
            foreach (var a in b.Aliases) d.TryAdd(a, b);
        }
        return d;
    }

    private static (string, BibleBook)[] BuildKeys() =>
        BibleBooks.All.SelectMany(b => new[] { Squash(b.Name) }.Concat(b.Aliases).Distinct().Select(k => (k, b))).ToArray();

    private static string Normalize(string s) =>
        s.ToLowerInvariant().Replace(' ', ' ').Replace('’', '\'').Trim();

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string PrefixDigit(string pre) => pre switch
    {
        "" => "",
        "1" or "i" or "first" or "1st" => "1",
        "2" or "ii" or "second" or "2nd" => "2",
        "3" or "iii" or "third" or "3rd" => "3",
        _ => pre,
    };

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 99;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
