using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Core.Model;

/// <summary>A labelled part of a song: "Verse 1", "Chorus", "Bridge".</summary>
public sealed record SongPart(string Label, string Text)
{
    public IReadOnlyList<string> Lines => Text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
}

/// <summary>An order of parts, for example V1 C V2 C B C C. The chorus is typed once and shown as often as needed.</summary>
public sealed record SongArrangement(string Name, IReadOnlyList<string> Order);

public sealed record Song
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Other titles people know it by, separated by semicolons.</summary>
    public string AltTitles { get; init; } = "";
    public string Author { get; init; } = "";
    public string Copyright { get; init; } = "";
    public string Ccli { get; init; } = "";
    public string Key { get; init; } = "";
    public string Tempo { get; init; } = "";
    /// <summary>Worship, praise, hymn, choir, local language … separated by commas.</summary>
    public string Tags { get; init; } = "";
    public IReadOnlyList<SongPart> Parts { get; init; } = [];
    public IReadOnlyList<SongArrangement> Arrangements { get; init; } = [];
    /// <summary>A theme just for this song (overrides the theme of the day).</summary>
    public string? ThemeId { get; init; }
    /// <summary>Lines on each slide: 2, 4 or 6, or 0 to show a whole part per slide.</summary>
    public int LinesPerSlide { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public string FirstLine => Parts.SelectMany(p => p.Lines).FirstOrDefault() ?? "";

    /// <summary>The parts in the order they are sung, using an arrangement when one is chosen.</summary>
    public IReadOnlyList<SongPart> Ordered(string? arrangement)
    {
        var arr = arrangement is null ? null : Arrangements.FirstOrDefault(a => a.Name.Equals(arrangement, StringComparison.OrdinalIgnoreCase));
        if (arr is null || arr.Order.Count == 0) return Parts;
        var list = new List<SongPart>();
        foreach (var key in arr.Order)
            if (SongText.FindPart(Parts, key) is { } p) list.Add(p);
        return list.Count > 0 ? list : Parts;
    }

    /// <summary>Lowercase words for searching: titles, first line and all the lyrics.</summary>
    public string SearchText => SongText.Fold($"{Title} {AltTitles} {Author} {Tags} {Ccli} {string.Join(" ", Parts.Select(p => p.Text))}");
}

/// <summary>A song in the list for the day.</summary>
public sealed record SongListEntry(string SongId, string? Arrangement = null);

public sealed record SongList(string Date, IReadOnlyList<SongListEntry> Entries)
{
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");
}

/// <summary>Church-wide Lyrics choices.</summary>
public sealed record LyricsSettings(
    string DefaultThemeId,
    LyricAnimation Animation,
    int AnimationMs,
    int LinesPerSlide,
    bool CopyrightOnFirstSlide,
    string CcliLicence)
{
    public static LyricsSettings Default => new("lyrics-classic", LyricAnimation.Fade, 450, 0, true, "");
}

/// <summary>One song shown in a service, for the CCLI usage report.</summary>
public sealed record SongUse(string Date, string SongId, string Title, string Author, string Ccli);

/// <summary>Splitting pasted lyrics into parts, and parts into slides.</summary>
public static class SongText
{
    private static readonly Regex LabelLine = new(
        @"^\s*[\[\(]?\s*(?<name>verse|v|chorus|ch|c|refrain|pre[\s-]?chorus|prechorus|pc|bridge|b|tag|ending|end|outro|intro|interlude|vamp|coda)\s*(?<num>\d+)?\s*[\]\)]?\s*[:.]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Verse 2" from "v2", "[Chorus]", "CHORUS:", "Pre-chorus" …; null when the line is lyrics.</summary>
    public static string? LabelOf(string line)
    {
        var m = LabelLine.Match(line);
        if (!m.Success) return null;
        var name = m.Groups["name"].Value.ToLowerInvariant().Replace(" ", "").Replace("-", "");
        var num = m.Groups["num"].Value;
        // A single letter alone ("C" or "B") is only a label in brackets or with a colon.
        if (name.Length == 1 && num.Length == 0 && !Regex.IsMatch(line, @"[\[\(:]")) return null;
        var label = name switch
        {
            "v" or "verse" => "Verse",
            "c" or "ch" or "chorus" or "refrain" => "Chorus",
            "prechorus" or "pc" => "Pre-chorus",
            "b" or "bridge" => "Bridge",
            "tag" => "Tag",
            "ending" or "end" or "outro" or "coda" => "Ending",
            "intro" => "Intro",
            "interlude" => "Interlude",
            "vamp" => "Vamp",
            _ => "Verse",
        };
        return num.Length > 0 ? $"{label} {int.Parse(num)}" : label;
    }

    /// <summary>
    /// Pasted lyrics from anywhere: split on blank lines (or label lines), and guess labels. A block that
    /// repeats an earlier block word for word is the chorus.
    /// </summary>
    public static IReadOnlyList<SongPart> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<(string? Label, List<string> Lines)>();
        (string? Label, List<string> Lines)? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (LabelOf(line) is { } label)
            {
                if (cur is { Lines.Count: > 0 } || cur?.Label is not null) blocks.Add(cur!.Value);
                cur = (label, new List<string>());
                continue;
            }
            if (line.Length == 0)
            {
                if (cur is { Lines.Count: > 0 }) { blocks.Add(cur.Value); cur = null; }
                continue;
            }
            cur ??= (null, new List<string>());
            cur.Value.Lines.Add(line);
        }
        if (cur is { Lines.Count: > 0 }) blocks.Add(cur.Value);
        blocks.RemoveAll(b => b.Lines.Count == 0);

        // Blocks that appear more than once (and are unlabelled) are the chorus.
        var counts = blocks.GroupBy(b => Fold(string.Join(" ", b.Lines))).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<SongPart>();
        var seen = new HashSet<string>();
        var verse = 0;
        foreach (var (label, blockLines) in blocks)
        {
            var key = Fold(string.Join(" ", blockLines));
            string name;
            if (label is not null) name = label;
            else if (counts[key] > 1) name = "Chorus";
            else name = $"Verse {++verse}";
            if (label is not null && label.StartsWith("Verse") && int.TryParse(label.Split(' ').Last(), out var n)) verse = Math.Max(verse, n);
            // Repeats of the same words are kept once; the arrangement repeats them.
            if (!seen.Add(name + "|" + key)) continue;
            var unique = name;
            var i = 2;
            while (parts.Any(p => p.Label == unique)) unique = $"{name} {i++}";
            parts.Add(new SongPart(unique, string.Join("\n", blockLines)));
        }
        return parts;
    }

    /// <summary>The order the parts were pasted in, as part labels (repeats included).</summary>
    public static IReadOnlyList<string> PastedOrder(string text)
    {
        var parts = Split(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var order = new List<string>();
        var block = new List<string>();
        string? label = null;
        void Flush()
        {
            if (block.Count == 0 && label is null) return;
            var key = Fold(string.Join(" ", block));
            var p = label is not null && block.Count == 0
                ? FindPart(parts, label)
                : parts.FirstOrDefault(x => Fold(string.Join(" ", x.Lines)) == key);
            if (p is not null) order.Add(p.Label);
            block.Clear();
            label = null;
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (LabelOf(line) is { } l) { Flush(); label = l; continue; }
            if (line.Length == 0) { if (block.Count > 0) Flush(); continue; }
            block.Add(line);
        }
        Flush();
        return order;
    }

    /// <summary>Parts as editable text with [Label] lines, the way the song editor shows them.</summary>
    public static string ToEditorText(IEnumerable<SongPart> parts) =>
        string.Join("\n\n", parts.Select(p => $"[{p.Label}]\n{p.Text.Trim()}"));

    /// <summary>The short code for a part: V1, C, PC, B, T, E.</summary>
    public static string Code(string label)
    {
        var l = label.Trim();
        var num = Regex.Match(l, @"\d+$").Value;
        var name = l.ToLowerInvariant();
        var code = name.StartsWith("pre") ? "PC"
            : name.StartsWith("verse") ? "V"
            : name.StartsWith("chorus") || name.StartsWith("refrain") ? "C"
            : name.StartsWith("bridge") ? "B"
            : name.StartsWith("tag") ? "T"
            : name.StartsWith("ending") || name.StartsWith("outro") ? "E"
            : name.StartsWith("intro") ? "I"
            : l.Length > 0 ? char.ToUpperInvariant(l[0]).ToString() : "?";
        return code + num;
    }

    /// <summary>Finds a part by label or short code ("V2", "C", "Chorus", "bridge").</summary>
    public static SongPart? FindPart(IReadOnlyList<SongPart> parts, string key)
    {
        var k = key.Trim();
        return parts.FirstOrDefault(p => p.Label.Equals(k, StringComparison.OrdinalIgnoreCase))
            ?? parts.FirstOrDefault(p => Code(p.Label).Equals(k, StringComparison.OrdinalIgnoreCase))
            ?? parts.FirstOrDefault(p => Code(p.Label).Equals(k + "1", StringComparison.OrdinalIgnoreCase))
            ?? (LabelOf(k) is { } l ? parts.FirstOrDefault(p => p.Label.Equals(l, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>"V1 C V2 C B C" or "V1, C, V2" into part labels.</summary>
    public static IReadOnlyList<string> ParseOrder(string text, IReadOnlyList<SongPart> parts) =>
        text.Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(k => FindPart(parts, k)?.Label)
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

    public static string OrderText(IEnumerable<string> labels) => string.Join(" ", labels.Select(Code));

    /// <summary>Splits a part into slides of a number of lines (0 = the whole part on one slide).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Slides(SongPart part, int linesPerSlide)
    {
        var lines = part.Lines;
        if (lines.Count == 0) return [];
        if (linesPerSlide <= 0)
        {
            // A whole part, but very long parts still split in two.
            if (lines.Count <= 8) return [lines];
            linesPerSlide = (int)Math.Ceiling(lines.Count / Math.Ceiling(lines.Count / 6.0));
        }
        var slides = new List<IReadOnlyList<string>>();
        for (var i = 0; i < lines.Count; i += linesPerSlide)
            slides.Add(lines.Skip(i).Take(linesPerSlide).ToList());
        return slides;
    }

    /// <summary>Lowercase, accents removed, punctuation as spaces: for searching.</summary>
    public static string Fold(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        var space = false;
        foreach (var ch in norm)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (ch == '\'' || ch == '’') continue;
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Songs matching every word typed, best first (title matches, then first line, then lyrics).</summary>
    public static IEnumerable<Song> Search(IEnumerable<Song> songs, string query)
    {
        var q = Fold(query);
        if (q.Length == 0) return songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return songs
            .Select(s => (Song: s, Title: Fold(s.Title + " " + s.AltTitles), First: Fold(s.FirstLine), All: s.SearchText))
            .Where(x => words.All(w => x.All.Contains(w)))
            .OrderByDescending(x => x.Title.StartsWith(q) ? 4 : x.Title.Contains(q) ? 3 : words.All(w => x.Title.Contains(w)) ? 2 : x.First.Contains(q) ? 1 : 0)
            .ThenBy(x => x.Song.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Song);
    }
}
