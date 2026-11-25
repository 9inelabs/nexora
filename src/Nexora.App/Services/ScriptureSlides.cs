using Nexora.App.Controls;
using Nexora.Core.Bible;
using Nexora.Core.Model;

namespace Nexora.App.Services;

/// <summary>
/// Turns a passage into slides that fit the screen with the chosen theme. A long passage is split
/// between verses; a verse too long for one screen is split between words, never inside a word.
/// </summary>
public static class ScriptureSlides
{
    public static IReadOnlyList<ScriptureSlide> Build(
        BibleBook book,
        IReadOnlyList<(int Chapter, int Verse)> verses,
        BibleTranslation primary,
        BibleTranslation? second,
        ScriptureTheme theme,
        bool onePerSlide)
    {
        var first = Collect(book, verses, primary);
        if (first.Count == 0)
        {
            var label = verses.Count > 0 ? Label(book, [new VerseText(verses[0].Chapter, verses[0].Verse, "")]) : book.Name;
            return [new ScriptureSlide(label, [new ScriptureColumn(primary.Abbrev, [new VerseText(verses.FirstOrDefault().Chapter, verses.FirstOrDefault().Verse, $"This verse is not in the {primary.Abbrev}.", false)])], theme)];
        }
        // The second translation follows the first one's verses.
        var secondList = second is null ? null : first.Select(v => SecondText(book, v, second)).ToList();

        var area = ScriptureRenderer.TextArea(theme);
        // Verses are grouped at the theme's full size so the words stay big; shrink-to-fit only
        // rescues a single verse that is a little too long, before it is split between words.
        var size = theme.FontSize;
        var smallest = theme.AutoFit ? Math.Min(theme.MinFontSize, theme.FontSize) : theme.FontSize;
        var groups = new List<(List<VerseText> A, List<VerseText>? B)>();

        var i = 0;
        while (i < first.Count)
        {
            var a = new List<VerseText> { first[i] };
            var b = secondList is null ? null : new List<VerseText> { secondList[i] };
            if (!FitsGroup(book, a, b, theme, size, area, primary, second))
            {
                if (FitsGroup(book, a, b, theme, smallest, area, primary, second)) groups.Add((a, b));
                else if (b is null)
                {
                    // One verse that does not fit even when shrunk: split it between words.
                    foreach (var piece in SplitVerse(book, first[i], theme, smallest, area, primary)) groups.Add(([piece], null));
                }
                else groups.Add((a, b)); // side by side: shown smaller rather than cut
                i++;
                continue;
            }
            i++;
            while (!onePerSlide && i < first.Count)
            {
                a.Add(first[i]);
                b?.Add(secondList![i]);
                if (FitsGroup(book, a, b, theme, size, area, primary, second))
                {
                    i++;
                    continue;
                }
                a.RemoveAt(a.Count - 1);
                b?.RemoveAt(b.Count - 1);
                break;
            }
            groups.Add((a, b));
        }

        var slides = new List<ScriptureSlide>();
        for (var g = 0; g < groups.Count; g++)
        {
            var (a, b) = groups[g];
            var cols = new List<ScriptureColumn> { new(primary.Abbrev, a) };
            if (b is not null) cols.Add(new ScriptureColumn(second!.Abbrev, b));
            slides.Add(new ScriptureSlide(Label(book, a), cols, theme, g + 1, groups.Count));
        }
        return slides;
    }

    private static bool FitsGroup(BibleBook book, List<VerseText> a, List<VerseText>? b, ScriptureTheme theme, double size,
        Avalonia.Rect area, BibleTranslation primary, BibleTranslation? second)
    {
        var cols = new List<ScriptureColumn> { new(primary.Abbrev, a) };
        if (b is not null) cols.Add(new ScriptureColumn(second!.Abbrev, b));
        var probe = new ScriptureSlide(Label(book, a), cols, theme);
        return ScriptureRenderer.Fits(cols, probe.ReferenceLine, theme, size, area);
    }

    private static IEnumerable<VerseText> SplitVerse(BibleBook book, VerseText v, ScriptureTheme theme, double size, Avalonia.Rect area, BibleTranslation primary)
    {
        var words = v.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        var firstPiece = true;
        while (start < words.Length)
        {
            // The most words from 'start' that still fit (at least one).
            int lo = 1, hi = words.Length - start, best = 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var piece = v with { Text = string.Join(' ', words, start, mid) + (start + mid < words.Length ? " …" : ""), ShowNumber = firstPiece };
                if (FitsGroup(book, [piece], null, theme, size, area, primary, null)) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            yield return v with { Text = string.Join(' ', words, start, best) + (start + best < words.Length ? " …" : ""), ShowNumber = firstPiece };
            start += best;
            firstPiece = false;
        }
    }

    /// <summary>The verses' text, joining verses a translation prints together and skipping ones it leaves out.</summary>
    private static List<VerseText> Collect(BibleBook book, IReadOnlyList<(int Chapter, int Verse)> verses, BibleTranslation t)
    {
        var list = new List<VerseText>();
        foreach (var (c, v) in verses)
        {
            var text = t.Verse(book.Number, c, v);
            if (text is null) continue;                       // left out by this translation
            if (text.Length == 0)
            {
                // Printed together with the verse before ("Numbers 1:20-21").
                if (list.Count > 0 && list[^1].Chapter == c)
                {
                    list[^1] = list[^1] with { ThroughVerse = v };
                    continue;
                }
                var back = v - 1;
                while (back >= 1 && t.Verse(book.Number, c, back) is "") back--;
                if (back >= 1 && t.Verse(book.Number, c, back) is { Length: > 0 } before)
                    list.Add(new VerseText(c, back, before, true, v));
                continue;
            }
            list.Add(new VerseText(c, v, text));
        }
        return list;
    }

    private static VerseText SecondText(BibleBook book, VerseText v, BibleTranslation t)
    {
        var parts = new List<string>();
        for (var n = v.Verse; n <= (v.ThroughVerse ?? v.Verse); n++)
            if (t.Verse(book.Number, v.Chapter, n) is { Length: > 0 } s) parts.Add(s);
        return v with { Text = parts.Count > 0 ? string.Join(" ", parts) : "—" };
    }

    /// <summary>"John 3:16-18" for the verses on a slide.</summary>
    public static string Label(BibleBook book, IReadOnlyList<VerseText> verses)
    {
        if (verses.Count == 0) return book.Name;
        var ranges = new List<VerseRange>();
        foreach (var v in verses)
        {
            var end = v.ThroughVerse ?? v.Verse;
            if (ranges.Count > 0)
            {
                var last = ranges[^1];
                if (last.EndChapter == v.Chapter && (last.EndVerse == v.Verse - 1 || last.EndVerse == v.Verse))
                {
                    ranges[^1] = last with { EndVerse = Math.Max(last.EndVerse, end) };
                    continue;
                }
                if (v.Chapter == last.EndChapter + 1 && v.Verse == 1 && last.EndVerse >= book.VersesIn(last.EndChapter))
                {
                    ranges[^1] = last with { EndChapter = v.Chapter, EndVerse = end };
                    continue;
                }
            }
            ranges.Add(new VerseRange(v.Chapter, v.Verse, v.Chapter, end));
        }
        // A whole chapter on one slide still reads as verses ("Psalms 23:1-6").
        var text = BibleReference.Format(book, ranges);
        if (!text.Contains(':') && book.Chapters > 1)
            text = $"{book.Name} {ranges[0].StartChapter}:{ranges[0].StartVerse}-{(ranges[^1].EndChapter != ranges[0].StartChapter ? ranges[^1].EndChapter + ":" : "")}{ranges[^1].EndVerse}";
        return text;
    }
}
