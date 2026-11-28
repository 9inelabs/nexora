using Nexora.Core.Bible;

namespace Nexora.Core.Speech;

/// <summary>What a spoken phrase asks for.</summary>
public enum HeardKind
{
    /// <summary>A book with a chapter, and usually a verse: "John 3:16", "Romans chapter 8 verse 28", "Psalm 23".</summary>
    Reference,
    /// <summary>"Next verse", "the following verse".</summary>
    NextVerse,
    /// <summary>"Previous verse", "go back a verse".</summary>
    PreviousVerse,
    /// <summary>"Verse 13", "go to verse 13", "verses 4 to 7": in the chapter already open.</summary>
    Verse,
    /// <summary>"Read on to verse 20", "down to verse 20": carry the passage on to that verse.</summary>
    ReadTo,
    /// <summary>"Chapter 4" (with no book): that chapter of the book already open.</summary>
    Chapter,
    /// <summary>"Next chapter".</summary>
    NextChapter,
    /// <summary>"In the Amplified", "New Living Translation": switch translation.</summary>
    Translation,
}

/// <summary>
/// One thing heard. <see cref="AtEnd"/> means the phrase is the last thing in the text so far, so more words may
/// still change it ("John 3:1" may become "John 3:16"); a listener waits for the next words before acting on it.
/// </summary>
public sealed record Heard(HeardKind Kind, string Phrase, double Confidence, bool AtEnd)
{
    public BibleReference? Reference { get; init; }
    /// <summary>For Verse, ReadTo and Chapter: the numbers heard (0 when not said).</summary>
    public int Chapter { get; init; }
    public int Verse { get; init; }
    public int ToVerse { get; init; }
    /// <summary>For Translation: the abbreviation (NKJV, AMP…).</summary>
    public string? Translation { get; init; }
    /// <summary>A book, chapter and verse were all heard (not just a book and chapter).</summary>
    public bool Exact { get; init; }

    /// <summary>A short label for a card ("John 3:16", "Next verse", "Verse 13", "Amplified").</summary>
    public string Label => Kind switch
    {
        HeardKind.Reference => Reference?.ToString() ?? Phrase,
        HeardKind.NextVerse => "Next verse",
        HeardKind.PreviousVerse => "Previous verse",
        HeardKind.Verse => ToVerse > Verse ? $"Verses {Verse}–{ToVerse}" : $"Verse {Verse}",
        HeardKind.ReadTo => $"On to verse {ToVerse}",
        HeardKind.Chapter => Verse > 0 ? $"Chapter {Chapter}, verse {Verse}" : $"Chapter {Chapter}",
        HeardKind.NextChapter => "Next chapter",
        HeardKind.Translation => Translation ?? Phrase,
        _ => Phrase,
    };
}

/// <summary>
/// Finds Bible references and spoken commands in a transcript: "turn with me to John chapter three verse sixteen",
/// "first Corinthians 13, 4 to 7", "Psalm 23", "next verse", "go to verse 13", "read on to verse 20",
/// "in the Amplified". Every reference is checked against the real chapter and verse counts.
/// </summary>
public static class SpokenReferences
{
    private sealed record BookName(string[] Words, BibleBook Book, bool NeedsNumber);

    private static readonly List<BookName> Names = BuildNames();

    /// <summary>Book names that are also everyday words or people's names: a chapter alone is not enough for them.</summary>
    private static readonly HashSet<int> Ambiguous = [18, 4, 8, 30, 29, 41, 42, 43, 44, 59, 65, 56, 27, 17, 14, 13, 9, 10];
    // Job, Numbers, Ruth, Amos, Joel, Mark, Luke, John, Acts, James, Jude, Titus, Daniel, Esther, Chronicles x2, Samuel x2

    private static readonly HashSet<string> Cues = ["book", "turn", "turning", "open", "read", "reading", "in", "from", "says", "said", "to", "of", "look", "see", "go", "found", "find", "verse", "scripture", "text", "chapter"];

    private static readonly (string[] Words, string Abbrev)[] TranslationNames =
    [
        (["new", "king", "james"], "NKJV"), (["nkjv"], "NKJV"),
        (["american", "king", "james"], "AKJV"), (["akjv"], "AKJV"),
        (["king", "james"], "KJV"), (["kjv"], "KJV"), (["authorized", "version"], "KJV"), (["authorised", "version"], "KJV"),
        (["amplified"], "AMP"), (["amp"], "AMP"),
        (["new", "living"], "NLT"), (["nlt"], "NLT"), (["living", "bible"], "NLT"),
        (["english", "standard"], "ESV"), (["esv"], "ESV"),
        (["new", "english", "translation"], "NET"), (["net", "bible"], "NET"),
        (["revised", "standard"], "RSV"), (["rsv"], "RSV"),
        (["american", "standard"], "ASV"), (["asv"], "ASV"),
        (["basic", "english"], "BBE"), (["bbe"], "BBE"),
        (["new", "international"], "NIV"), (["niv"], "NIV"),
        (["message", "bible"], "MSG"), (["message", "translation"], "MSG"),
    ];

    /// <summary>Everything heard in <paramref name="text"/>, in the order it was said.</summary>
    public static IReadOnlyList<Heard> Find(string text)
    {
        var t = SpokenText.Tokens(text);
        var found = new List<(int At, Heard H)>();
        var used = new bool[t.Count];
        var i = 0;
        while (i < t.Count)
        {
            if (TryReference(t, i, out var h, out var end))
            {
                found.Add((i, h! with { AtEnd = end >= t.Count }));
                for (var k = i; k < end; k++) used[k] = true;
                i = end;
                continue;
            }
            i++;
        }

        for (i = 0; i < t.Count; i++)
        {
            if (used[i]) continue;
            if (TryCommand(t, i, out var h, out var end))
            {
                found.Add((i, h! with { AtEnd = end >= t.Count }));
                for (var k = i; k < end && k < used.Length; k++) used[k] = true;
                i = end - 1;
            }
        }
        return found.OrderBy(f => f.At).Select(f => f.H).ToList();
    }

    // ---------- References ----------

    private static bool TryReference(List<string> t, int i, out Heard? heard, out int end)
    {
        heard = null;
        end = i;
        if (!MatchBook(t, i, out var book, out var after, out var fuzzy)) return false;

        var j = after;
        var sawChapterWord = false;
        if (j < t.Count && t[j] is "chapter" or "chapters") { sawChapterWord = true; j++; }
        // "Jude verse 3", "Philemon 4": books of one chapter go straight to the verse.
        if (book!.Chapters == 1)
        {
            if (j < t.Count && t[j] is "verse" or "verses") j++;
            if (j >= t.Count || !SpokenText.IsNumber(t[j])) return false;
            var v1 = int.Parse(t[j]);
            if (v1 < 1 || v1 > book.VersesIn(1)) return false;
            var e1 = j + 1;
            var to1 = ReadRangeEnd(t, ref e1, v1, book.VersesIn(1));
            heard = Make(book, 1, v1, to1, t, i, e1, exact: true, fuzzy);
            end = e1;
            return true;
        }

        if (j >= t.Count || !SpokenText.IsNumber(t[j])) return false;
        var chapter = int.Parse(t[j]);
        if (chapter < 1 || chapter > book.Chapters) return false;
        j++;

        // The verse: "3 : 16", "3 verse 16", "3 and verse 16", "3 from verse 16", "3 16".
        var k = j;
        var hadMarker = false;
        if (k < t.Count && t[k] == ":") { k++; hadMarker = true; }
        else
        {
            if (k < t.Count && t[k] is "and" or "from" or "at") k++;
            if (k < t.Count && t[k] is "verse" or "verses" or "vs" or "v") { k++; hadMarker = true; }
            else if (k > j) k = j; // "and" with no "verse" after it is not part of the reference
        }
        if (k < t.Count && SpokenText.IsNumber(t[k]) && (hadMarker || k == j))
        {
            var verse = int.Parse(t[k]);
            if (verse >= 1 && verse <= book.VersesIn(chapter))
            {
                var e = k + 1;
                var to = ReadRangeEnd(t, ref e, verse, book.VersesIn(chapter));
                heard = Make(book, chapter, verse, to, t, i, e, exact: true, fuzzy);
                end = e;
                return true;
            }
            if (hadMarker) return false; // "John 3 verse 99": not a real verse
        }

        // A book and chapter only. Names that are also ordinary words need "chapter" or a cue before them.
        if (Ambiguous.Contains(book.Number) && !sawChapterWord && !(i > 0 && Cues.Contains(t[i - 1]))) return false;
        heard = new Heard(HeardKind.Reference, Phrase(t, i, j), fuzzy ? 0.55 : 0.75, false)
        {
            Reference = new BibleReference(book, [new VerseRange(chapter, 1, chapter, book.VersesIn(chapter))]),
            Chapter = chapter,
            Exact = false,
        };
        end = j;
        return true;
    }

    private static Heard Make(BibleBook book, int chapter, int verse, int to, List<string> t, int start, int end, bool exact, bool fuzzy)
    {
        var confidence = fuzzy ? 0.7 : Ambiguous.Contains(book.Number) && !(start > 0 && Cues.Contains(t[start - 1])) ? 0.85 : 0.95;
        return new Heard(HeardKind.Reference, Phrase(t, start, end), confidence, false)
        {
            Reference = new BibleReference(book, [new VerseRange(chapter, verse, chapter, Math.Max(verse, to))]),
            Chapter = chapter,
            Verse = verse,
            ToVerse = Math.Max(verse, to),
            Exact = exact,
        };
    }

    /// <summary>"to 7", "through 7", "and 5" (the next verse), "to verse 7". Returns the end verse, or the start.</summary>
    private static int ReadRangeEnd(List<string> t, ref int k, int start, int max)
    {
        if (k >= t.Count) return start;
        var word = t[k];
        if (word is "to" or "through" or "thru" or "till" or "until" or "and")
        {
            var m = k + 1;
            if (m < t.Count && t[m] is "verse" or "verses") m++;
            if (m < t.Count && SpokenText.IsNumber(t[m]))
            {
                var e = int.Parse(t[m]);
                // "and" only joins the very next verse ("verses 4 and 5"); otherwise it is a new sentence.
                if (e > start && e <= max && (word != "and" || e == start + 1))
                {
                    k = m + 1;
                    return e;
                }
            }
        }
        return start;
    }

    private static bool MatchBook(List<string> t, int i, out BibleBook? book, out int after, out bool fuzzy)
    {
        book = null;
        after = i;
        fuzzy = false;
        var prefix = 0;
        var p = i;
        if (p < t.Count && Prefix(t[p]) is var pn and > 0 && p + 1 < t.Count && !SpokenText.IsNumber(t[p + 1]))
        {
            prefix = pn;
            p++;
        }
        // "the book of" in front is fine; "the" in front of a book ("the Romans") is not a book.
        BookName? best = null;
        var bestLen = 0;
        foreach (var n in Names)
        {
            if (n.NeedsNumber != (prefix > 0)) continue;
            if (prefix > 0 && n.Book.Name[0] - '0' != prefix) continue;
            if (p + n.Words.Length > t.Count || n.Words.Length <= bestLen) continue;
            var ok = true;
            for (var w = 0; w < n.Words.Length; w++)
                if (t[p + w] != n.Words[w]) { ok = false; break; }
            if (ok)
            {
                best = n;
                bestLen = n.Words.Length;
            }
        }
        if (best is null)
        {
            // Small misspellings of longer names ("Phillipians", "Habakuk", "Revelations").
            if (p >= t.Count || t[p].Length < 6 || SpokenText.IsNumber(t[p])) return false;
            var close = Names
                .Where(n => n.NeedsNumber == (prefix > 0) && (prefix == 0 || n.Book.Name[0] - '0' == prefix) && n.Words.Length == 1 && n.Words[0].Length >= 6)
                .Select(n => (n, d: SpokenText.Distance(t[p], n.Words[0], 2)))
                .Where(x => x.d <= (t[p].Length >= 9 ? 2 : 1))
                .OrderBy(x => x.d)
                .ToList();
            if (close.Count == 0 || (close.Count > 1 && close[0].d == close[1].d && close[0].n.Book != close[1].n.Book)) return false;
            best = close[0].n;
            bestLen = 1;
            fuzzy = close[0].d > 0;
        }
        book = best.Book;
        after = p + bestLen;
        return true;
    }

    private static int Prefix(string w) => w switch
    {
        "1" or "first" or "one" or "i" => 1,
        "2" or "second" or "two" or "ii" => 2,
        "3" or "third" or "three" or "iii" => 3,
        _ => 0,
    };

    private static List<BookName> BuildNames()
    {
        var list = new List<BookName>();
        void Add(BibleBook b, string spoken)
        {
            var numbered = char.IsDigit(b.Name[0]);
            var words = spoken.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (numbered && words.Length > 0 && char.IsDigit(words[0][0])) words = words[1..];
            if (words.Length == 0) return;
            if (list.Any(n => n.Book == b && n.Words.SequenceEqual(words))) return;
            list.Add(new BookName(words, b, numbered));
        }
        foreach (var b in BibleBooks.All)
        {
            Add(b, b.Name);
            // Longer spelled-out aliases (common misspellings); short abbreviations are never spoken.
            foreach (var a in b.Aliases)
            {
                var plain = a.TrimStart('1', '2', '3');
                if (plain.StartsWith("st") || plain.StartsWith("nd") || plain.StartsWith("rd") || plain.StartsWith("first") || plain.StartsWith("second")) continue;
                if (plain.Length >= 5 && !plain.Contains("of")) Add(b, (char.IsDigit(b.Name[0]) ? b.Name[0] + " " : "") + plain);
            }
        }
        var get = (int n) => BibleBooks.Get(n);
        Add(get(19), "Psalm");
        Add(get(19), "Psalms");
        Add(get(22), "Song of Songs");
        Add(get(22), "Songs of Solomon");
        Add(get(66), "Revelations");
        Add(get(66), "the Revelation");
        Add(get(44), "the Acts");
        Add(get(44), "Acts of the Apostles");
        Add(get(21), "Ecclesiastics");
        Add(get(7), "Judge");
        Add(get(20), "Proverb");
        Add(get(25), "Lamentation");
        Add(get(57), "Philemon");
        Add(get(50), "Phillipians");
        Add(get(50), "Philippines");
        Add(get(52), "1 Thessalonian");
        Add(get(53), "2 Thessalonian");
        Add(get(46), "1 Corinthian");
        Add(get(47), "2 Corinthian");
        return list;
    }

    // ---------- Commands ----------

    private static bool TryCommand(List<string> t, int i, out Heard? heard, out int end)
    {
        heard = null;
        end = i;
        bool At(int k, params string[] words) => k >= 0 && k < t.Count && words.Contains(t[k]);

        // Next / previous verse and chapter.
        if (At(i, "next", "following") && At(i + 1, "verse"))
        {
            heard = new Heard(HeardKind.NextVerse, Phrase(t, i, i + 2), 0.9, false);
            end = i + 2;
            return true;
        }
        if (At(i, "verse") && At(i + 1, "after") && At(i + 2, "that", "this"))
        {
            heard = new Heard(HeardKind.NextVerse, Phrase(t, i, i + 3), 0.85, false);
            end = i + 3;
            return true;
        }
        if (At(i, "previous", "prior", "preceding") && At(i + 1, "verse"))
        {
            heard = new Heard(HeardKind.PreviousVerse, Phrase(t, i, i + 2), 0.9, false);
            end = i + 2;
            return true;
        }
        if (At(i, "back") && At(i + 1, "a", "one", "1") && At(i + 2, "verse"))
        {
            heard = new Heard(HeardKind.PreviousVerse, Phrase(t, i, i + 3), 0.85, false);
            end = i + 3;
            return true;
        }
        if (At(i, "verse") && At(i + 1, "before") && At(i + 2, "that", "this"))
        {
            heard = new Heard(HeardKind.PreviousVerse, Phrase(t, i, i + 3), 0.85, false);
            end = i + 3;
            return true;
        }
        if (At(i, "next") && At(i + 1, "chapter"))
        {
            heard = new Heard(HeardKind.NextChapter, Phrase(t, i, i + 2), 0.85, false);
            end = i + 2;
            return true;
        }

        // "verse 13", "verses 4 to 7", "read on to verse 20".
        if (At(i, "verse", "verses") && i + 1 < t.Count && SpokenText.IsNumber(t[i + 1]))
        {
            var v = int.Parse(t[i + 1]);
            if (v < 1 || v > 176) return false;
            var e = i + 2;
            var to = ReadRangeEnd(t, ref e, v, 176);
            var readTo = At(i - 1, "through", "thru", "till", "until")
                         || (At(i - 1, "to") && At(i - 2, "on", "down", "up", "read", "reading", "continue", "continuing", "way", "further") && !At(i - 2, "go", "going", "turn"));
            heard = readTo
                ? new Heard(HeardKind.ReadTo, Phrase(t, Math.Max(0, i - 2), e), 0.85, false) { ToVerse = v }
                : new Heard(HeardKind.Verse, Phrase(t, i, e), 0.85, false) { Verse = v, ToVerse = to };
            end = e;
            return true;
        }

        // "chapter 4", "chapter 4 verse 2" (the book already open).
        if (At(i, "chapter") && i + 1 < t.Count && SpokenText.IsNumber(t[i + 1]))
        {
            var c = int.Parse(t[i + 1]);
            if (c < 1 || c > 150) return false;
            var e = i + 2;
            var v = 0;
            var to = 0;
            var k = e;
            if (At(k, "and", "from")) k++;
            if ((At(k, "verse", "verses") || At(k, ":")) && k + 1 < t.Count && SpokenText.IsNumber(t[k + 1]))
            {
                v = int.Parse(t[k + 1]);
                e = k + 2;
                to = ReadRangeEnd(t, ref e, v, 176);
            }
            heard = new Heard(HeardKind.Chapter, Phrase(t, i, e), 0.8, false) { Chapter = c, Verse = v, ToVerse = Math.Max(v, to) };
            end = e;
            return true;
        }

        // A translation by name: "in the Amplified", "the New King James says".
        foreach (var (words, abbrev) in TranslationNames)
        {
            if (i + words.Length > t.Count) continue;
            var ok = true;
            for (var w = 0; w < words.Length; w++)
                if (t[i + w] != words[w]) { ok = false; break; }
            if (!ok) continue;
            // A longer name starting here wins ("new king james" before "king james").
            var e = i + words.Length;
            var spoken = At(i - 1, "the", "in", "from") || At(e, "version", "translation", "bible", "says", "reads", "puts");
            if (!spoken && words.Length < 2) continue;
            heard = new Heard(HeardKind.Translation, Phrase(t, i, e), 0.8, false) { Translation = abbrev };
            end = e;
            return true;
        }
        return false;
    }

    private static string Phrase(List<string> t, int from, int to) => string.Join(' ', t.Skip(from).Take(Math.Max(0, to - from)));

    // ---------- Acting on what was heard ----------

    /// <summary>
    /// What a command means given what is open now: "next verse" after John 3:16 is John 3:17, "verse 20" in John 3
    /// is John 3:20, "read on to verse 20" after John 3:16 is John 3:16-20. Null when it makes no sense here.
    /// </summary>
    public static BibleReference? Resolve(Heard h, BibleReference? current, Func<int, int, int>? versesIn = null)
    {
        int Count(BibleBook b, int c) => Math.Max(b.VersesIn(c), versesIn?.Invoke(b.Number, c) ?? 0);
        switch (h.Kind)
        {
            case HeardKind.Reference:
                return h.Reference;
            case HeardKind.NextVerse:
            {
                if (current is null) return null;
                var b = current.Book;
                var last = current.Ranges[^1];
                var (c, v) = (last.EndChapter, last.EndVerse + 1);
                if (v > Count(b, c)) { c++; v = 1; }
                return c > b.Chapters ? null : One(b, c, v);
            }
            case HeardKind.PreviousVerse:
            {
                if (current is null) return null;
                var b = current.Book;
                var first = current.First;
                var (c, v) = (first.StartChapter, first.StartVerse - 1);
                if (v < 1)
                {
                    c--;
                    if (c < 1) return null;
                    v = Count(b, c);
                }
                return One(b, c, v);
            }
            case HeardKind.Verse:
            {
                if (current is null) return null;
                var b = current.Book;
                var c = current.Ranges[^1].EndChapter;
                if (h.Verse > Count(b, c)) return null;
                return new BibleReference(b, [new VerseRange(c, h.Verse, c, Math.Min(Math.Max(h.Verse, h.ToVerse), Count(b, c)))]);
            }
            case HeardKind.ReadTo:
            {
                if (current is null) return null;
                var b = current.Book;
                var first = current.First;
                var c = current.Ranges[^1].EndChapter;
                if (h.ToVerse > Count(b, c)) return null;
                if (c == first.StartChapter && h.ToVerse <= first.StartVerse) return null;
                return new BibleReference(b, [new VerseRange(first.StartChapter, first.StartVerse, c, h.ToVerse)]);
            }
            case HeardKind.Chapter:
            {
                if (current is null) return null;
                var b = current.Book;
                if (h.Chapter > b.Chapters) return null;
                if (h.Verse > 0)
                {
                    if (h.Verse > Count(b, h.Chapter)) return null;
                    return new BibleReference(b, [new VerseRange(h.Chapter, h.Verse, h.Chapter, Math.Min(Math.Max(h.Verse, h.ToVerse), Count(b, h.Chapter)))]);
                }
                return One(b, h.Chapter, 1);
            }
            case HeardKind.NextChapter:
            {
                if (current is null) return null;
                var c = current.Ranges[^1].EndChapter + 1;
                return c > current.Book.Chapters ? null : One(current.Book, c, 1);
            }
            default:
                return null;
        }
    }

    private static BibleReference One(BibleBook b, int c, int v) => new(b, [new VerseRange(c, v, c, v)]);
}
