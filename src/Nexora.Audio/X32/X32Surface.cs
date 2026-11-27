using System.Globalization;
using Nexora.Audio.Mix;

namespace Nexora.Audio.X32;

/// <summary>
/// The X32 as a control surface for NEX-Audio. Its faders, mutes and pans already follow both ways through
/// <see cref="ShadowMix"/>; this adds its SELECT and SOLO buttons, and keeps a moving fader from fighting the
/// engineer's hand (<see cref="TouchGuard"/>).
/// </summary>
public static class X32Surface
{
    /// <summary>The X32's select number (/-stat/selidx) for a strip, or -1 when it has none on the X32.</summary>
    public static int SelectIndex(MixState s, string id)
    {
        if (s.Find(id) is not { } st) return -1;
        return st.Kind switch
        {
            StripKind.Channel when st.Number is >= 1 and <= 32 => st.Number - 1,
            StripKind.Mix when st.Number is >= 1 and <= 16 => 47 + st.Number,
            StripKind.Main => 70,
            _ => -1,
        };
    }

    /// <summary>The strip an X32 select number points to, or null.</summary>
    public static string? FromSelectIndex(MixState s, int index) => index switch
    {
        >= 0 and <= 31 => s.Channels.FirstOrDefault(c => c.Number == index + 1)?.Id,
        >= 48 and <= 63 => s.BusesOf(StripKind.Mix).FirstOrDefault(b => b.Number == index - 47)?.Id,
        70 => s.Main.Id,
        _ => null,
    };

    /// <summary>The X32's solo button number (/-stat/solosw/NN, 1 to 72) for a strip, or 0.</summary>
    public static int SoloButton(Strip st) => st.Kind switch
    {
        StripKind.Channel when st.Number is >= 1 and <= 32 => st.Number,
        StripKind.Mix when st.Number is >= 1 and <= 16 => 48 + st.Number,
        StripKind.Main => 71,
        _ => 0,
    };

    /// <summary>The solo lights the X32 should show for this mix, by OSC address.</summary>
    public static Dictionary<string, int> Desired(MixState s)
    {
        var d = new Dictionary<string, int>();
        foreach (var st in s.Channels.Concat(s.BusesOf(StripKind.Mix)).Append(s.Main))
            if (SoloButton(st) is var b and > 0) d[$"/-stat/solosw/{b:00}"] = st.Solo ? 1 : 0;
        return d;
    }

    /// <summary>What a press on the X32 means for NEX-Audio: a strip to select, or a solo to set. Null when it is neither.</summary>
    public static SurfacePress? FromDesk(MixState s, string address, float value)
    {
        if (address == "/-stat/selidx")
            return FromSelectIndex(s, (int)Math.Round(value)) is { } id ? new SurfacePress(id, null) : null;
        if (address.StartsWith("/-stat/solosw/", StringComparison.Ordinal)
            && int.TryParse(address["/-stat/solosw/".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            var st = s.Channels.Concat(s.BusesOf(StripKind.Mix)).Append(s.Main).FirstOrDefault(x => SoloButton(x) == n);
            return st is null ? null : new SurfacePress(st.Id, value >= 0.5f);
        }
        return null;
    }

    /// <summary>Sends the solo lights that differ from what the X32 last showed.</summary>
    public static async Task<int> SyncAsync(X32Client x32, MixState s, Func<string, bool>? held = null)
    {
        if (!x32.Connected) return 0;
        var sent = 0;
        foreach (var (addr, v) in Desired(s))
        {
            if (held?.Invoke(addr) == true) continue;
            if (x32.Value(addr) is { } cur && Math.Abs(cur - v) < 0.5f) continue;
            await x32.SetAsync(addr, v);
            sent++;
        }
        return sent;
    }
}

/// <summary>A select (<see cref="Solo"/> null) or a solo press for one strip.</summary>
public readonly record struct SurfacePress(string StripId, bool? Solo);

/// <summary>
/// Remembers which controls the engineer has just moved on a surface. While a hand is on a fader (and for a
/// moment after), NEX-Audio does not send that fader back: a motor fader would otherwise twitch under the
/// finger as the two ends round the level slightly differently.
/// </summary>
public sealed class TouchGuard(TimeSpan hold)
{
    private readonly Dictionary<string, DateTime> _touched = [];
    private readonly HashSet<string> _down = [];

    public TouchGuard() : this(TimeSpan.FromMilliseconds(400)) { }

    /// <summary>The control moved just now.</summary>
    public void Moved(string key, DateTime? now = null)
    {
        lock (_touched) _touched[key] = now ?? DateTime.UtcNow;
    }

    /// <summary>A touch-sensitive fader: the hand is on it (true) or has let go (false).</summary>
    public void Touch(string key, bool down, DateTime? now = null)
    {
        lock (_touched)
        {
            if (down) _down.Add(key);
            else _down.Remove(key);
            _touched[key] = now ?? DateTime.UtcNow;
        }
    }

    /// <summary>True while NEX-Audio should leave this control alone.</summary>
    public bool Held(string key, DateTime? now = null)
    {
        lock (_touched)
        {
            if (_down.Contains(key)) return true;
            return _touched.TryGetValue(key, out var t) && (now ?? DateTime.UtcNow) - t < hold;
        }
    }
}
