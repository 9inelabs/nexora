using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Core.Speech;

/// <summary>
/// Turns what speech recognition wrote into plain tokens: lower case, no punctuation, and numbers as digits
/// ("John three sixteen" and "John 3:16" both become john 3 : 16; "one hundred and nineteen" becomes 119).
/// </summary>
public static partial class SpokenText
{
    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>Spoken ordinals used for verses ("the sixteenth verse").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
        ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13, ["fourteenth"] = 14, ["fifteenth"] = 15, ["sixteenth"] = 16,
        ["seventeenth"] = 17, ["eighteenth"] = 18, ["nineteenth"] = 19, ["twentieth"] = 20,
    };

    [GeneratedRegex(@"(?<=\d)\s*[:.]\s*(?=\d)")]
    private static partial Regex ChapterVerse();
    [GeneratedRegex(@"(?<=\d)\s*[-–—]\s*(?=\d)")]
    private static partial Regex NumberRange();
    [GeneratedRegex(@"(?<=\d)(st|nd|rd|th)\b")]
    private static partial Regex OrdinalSuffix();
    [GeneratedRegex(@"(?<=[a-z])(?=\d)|(?<=\d)(?=[a-z])")]
    private static partial Regex LetterDigit();
    [GeneratedRegex(@"[^a-z0-9: ]+")]
    private static partial Regex Junk();

    /// <summary>The words and numbers in <paramref name="text"/>. ":" stays as its own token between chapter and verse.</summary>
    public static List<string> Tokens(string text)
    {
        var s = text.ToLowerInvariant().Replace('’', '\'').Replace("'s ", "s ");
        s = OrdinalSuffix().Replace(s, "");
        s = ChapterVerse().Replace(s, " : ");
        s = NumberRange().Replace(s, " to ");
        s = s.Replace('-', ' ').Replace('–', ' ').Replace('—', ' ');
        s = Junk().Replace(s, " ");
        s = LetterDigit().Replace(s, " ");
        var raw = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return JoinNumbers(raw);
    }

    /// <summary>The tokens as one string again (for searching and for tests).</summary>
    public static string Normalize(string text) => string.Join(' ', Tokens(text));

    private static List<string> JoinNumbers(string[] raw)
    {
        var result = new List<string>(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (TryNumber(raw, ref i, out var n))
            {
                result.Add(n.ToString());
                continue;
            }
            result.Add(raw[i]);
            i++;
        }
        return result;
    }

    /// <summary>
    /// Reads one number from words at <paramref name="i"/>: "sixteen", "twenty three", "one hundred and nineteen",
    /// "a hundred and fifty". Two numbers side by side ("three sixteen") are read as two numbers.
    /// </summary>
    private static bool TryNumber(string[] w, ref int i, out int value)
    {
        value = 0;
        var start = i;
        var j = i;
        int? hundreds = null;

        // "a hundred", "one hundred", "hundred"
        if (j + 1 < w.Length && w[j] == "a" && w[j + 1] == "hundred")
        {
            hundreds = 100;
            j += 2;
        }
        else if (j + 1 < w.Length && Units.TryGetValue(w[j], out var hu) && hu is >= 1 and <= 9 && w[j + 1] == "hundred")
        {
            hundreds = hu * 100;
            j += 2;
        }
        else if (w[j] == "hundred")
        {
            hundreds = 100;
            j += 1;
        }

        var rest = 0;
        var gotRest = false;
        var k = j;
        if (hundreds is not null && k < w.Length && w[k] == "and" && k + 1 < w.Length && (Units.ContainsKey(w[k + 1]) || Tens.ContainsKey(w[k + 1]))) k++;
        if (k < w.Length && Tens.TryGetValue(w[k], out var t))
        {
            rest = t;
            gotRest = true;
            k++;
            if (k < w.Length && Units.TryGetValue(w[k], out var u) && u is >= 1 and <= 9)
            {
                rest += u;
                k++;
            }
        }
        else if (k < w.Length && Units.TryGetValue(w[k], out var u2))
        {
            rest = u2;
            gotRest = true;
            k++;
        }
        else if (hundreds is null && k < w.Length && Ordinals.TryGetValue(w[k], out var o))
        {
            rest = o;
            gotRest = true;
            k++;
        }

        if (hundreds is null && !gotRest)
        {
            i = start;
            return false;
        }
        value = (hundreds ?? 0) + rest;
        i = gotRest ? k : j;
        return true;
    }

    /// <summary>Squashes a name to letters and digits only ("1 Corinthians" → "1corinthians").</summary>
    public static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public static bool IsNumber(string token) => token.Length > 0 && token.Length <= 4 && token.All(char.IsDigit);

    /// <summary>Edit distance, stopping early once it is past <paramref name="max"/>.</summary>
    public static int Distance(string a, string b, int max = 2)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (cur[j] < best) best = cur[j];
            }
            if (best > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
