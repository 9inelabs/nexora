namespace Nexora.Audio.Mix;

/// <summary>Quick select: finding a strip from a few typed letters or a number.</summary>
public static class MixSearch
{
    /// <summary>
    /// Strips that match what was typed: a channel number ("12"), a kind and number ("mix 2", "fx1", "group 3"),
    /// or any part of a name ("pastor"). Exact numbers come first.
    /// </summary>
    public static IEnumerable<Strip> Find(MixState mix, string? text, Func<Strip, string> name)
    {
        var all = mix.AllStrips().ToList();
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return all.Where(s => s.Kind != StripKind.Channel).Concat(all.Where(s => s.Kind == StripKind.Channel));
        var number = new string(t.Where(char.IsDigit).ToArray());
        var word = new string(t.Where(char.IsLetter).ToArray());
        int.TryParse(number, out var num);
        StripKind? kind = word switch
        {
            "ch" or "chan" or "channel" or "c" when number.Length > 0 => StripKind.Channel,
            "mix" or "m" or "wedge" or "aux" when number.Length > 0 => StripKind.Mix,
            "fx" or "effect" or "e" or "return" when number.Length > 0 => StripKind.Fx,
            "group" or "grp" or "g" or "sub" when number.Length > 0 => StripKind.Group,
            "matrix" or "mx" or "mtx" when number.Length > 0 => StripKind.Matrix,
            "" when number.Length > 0 => StripKind.Channel,
            _ => null,
        };
        var exact = kind is { } k ? all.Where(s => s.Kind == k && s.Number == num).ToList() : [];
        var byName = all.Where(s => !exact.Contains(s) &&
            (name(s).Contains(t, StringComparison.CurrentCultureIgnoreCase) || MixState.Label(s).Contains(t, StringComparison.CurrentCultureIgnoreCase)));
        // Names that start with what was typed come before names that only contain it.
        return exact.Concat(byName.OrderBy(s => name(s).StartsWith(t, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1));
    }

}
