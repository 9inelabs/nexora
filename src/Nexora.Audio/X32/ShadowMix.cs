using System.Globalization;
using Nexora.Audio.Mix;

namespace Nexora.Audio.X32;

/// <summary>
/// Keeps the X32's own mix close to NEX-Audio's: channel faders, mutes, pans, the sends to mix buses 1 to 16,
/// the bus masters, the main fader, preamp gain and phantom power. If the failsafe ever hands the room back
/// to the X32, the desk then already sounds close to what people were hearing (without NEX-Audio's effects).
/// Optionally it works the other way too: moving a fader on the X32 moves it in NEX-Audio.
/// </summary>
public sealed class ShadowMix
{
    /// <summary>The values the X32 should have for this mix, by OSC address.</summary>
    public static Dictionary<string, float> Desired(MixState s)
    {
        var d = new Dictionary<string, float>();
        foreach (var c in s.Channels)
        {
            var n = c.Number;
            if (n is < 1 or > 32) continue;
            var ch = $"/ch/{n:00}";
            d[$"{ch}/mix/fader"] = X32Client.DbToFader(c.Fader);
            d[$"{ch}/mix/on"] = c.Mute ? 0 : 1;
            d[$"{ch}/mix/pan"] = (float)Math.Clamp((c.Pan + 1) / 2, 0, 1);
            foreach (var mix in s.BusesOf(StripKind.Mix))
            {
                if (mix.Number is < 1 or > 16) continue;
                var send = c.SendTo(mix.Id);
                d[$"{ch}/mix/{mix.Number:00}/level"] = send is { On: true } ? X32Client.DbToFader(send.Level) : 0f;
                d[$"{ch}/mix/{mix.Number:00}/on"] = send is { On: true } ? 1 : 0;
            }
            d[$"{ch}/preamp/hpon"] = c.Hpf.On ? 1 : 0;
            d[$"{ch}/preamp/hpf"] = (float)Math.Clamp(Math.Log(Math.Clamp(c.Hpf.Freq, 20, 400) / 20) / Math.Log(20), 0, 1);
            if (c.Preamp is { } gain)
            {
                d[$"/headamp/{n - 1:000}/gain"] = X32Client.GainToHeadamp(gain);
                d[$"/headamp/{n - 1:000}/phantom"] = c.Phantom ? 1 : 0;
            }
        }
        foreach (var mix in s.BusesOf(StripKind.Mix))
        {
            if (mix.Number is < 1 or > 16) continue;
            d[$"/bus/{mix.Number:00}/mix/fader"] = X32Client.DbToFader(mix.Fader);
            d[$"/bus/{mix.Number:00}/mix/on"] = mix.Mute ? 0 : 1;
        }
        d["/main/st/mix/fader"] = X32Client.DbToFader(s.Main.Fader);
        d["/main/st/mix/on"] = s.Main.Mute ? 0 : 1;
        return d;
    }

    /// <summary>Sends whatever differs from what the desk last showed. Returns how many values were sent.</summary>
    public static async Task<int> SyncAsync(X32Client x32, MixState s, int max = 400)
    {
        if (!x32.Connected) return 0;
        var sent = 0;
        foreach (var (addr, v) in Desired(s))
        {
            var now = x32.Value(addr);
            if (now is { } cur && Math.Abs(cur - v) < 0.001f) continue;
            if (addr.EndsWith("/on") || addr.EndsWith("/hpon") || addr.EndsWith("/phantom")) await x32.SetAsync(addr, (int)v);
            else await x32.SetAsync(addr, v);
            if (++sent >= max) break;
        }
        return sent;
    }

    /// <summary>
    /// Turns a change made on the X32 into the NEX-Audio setting it matches (a path for <see cref="MixParams"/>
    /// and its value), or null when it is not one NEX-Audio follows.
    /// </summary>
    public static (string Path, object Value)? FromDesk(MixState s, string address, float value)
    {
        var p = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 3 && p[0] == "ch" && int.TryParse(p[1], out var ch) && ch is >= 1 and <= 32)
        {
            var id = $"ch{ch}";
            if (p is [_, _, "mix", "fader"]) return ($"{id}.fader", Math.Round(X32Client.FaderToDb(value), 1));
            if (p is [_, _, "mix", "on"]) return ($"{id}.mute", value < 0.5f);
            if (p is [_, _, "mix", "pan"]) return ($"{id}.pan", Math.Round(value * 2 - 1, 2));
            if (p.Length == 5 && p[2] == "mix" && int.TryParse(p[3], out var bus) && s.BusesOf(StripKind.Mix).FirstOrDefault(b => b.Number == bus) is { } mix)
            {
                if (p[4] == "level") return ($"{id}.send.{mix.Id}.level", Math.Round(X32Client.FaderToDb(value), 1));
                if (p[4] == "on") return ($"{id}.send.{mix.Id}.on", value >= 0.5f);
            }
            return null;
        }
        if (p is ["bus", var b, "mix", var what] && int.TryParse(b, out var bn) && s.BusesOf(StripKind.Mix).FirstOrDefault(x => x.Number == bn) is { } m)
            return what switch
            {
                "fader" => ($"{m.Id}.fader", Math.Round(X32Client.FaderToDb(value), 1)),
                "on" => ($"{m.Id}.mute", value < 0.5f),
                _ => null,
            };
        if (p is ["main", "st", "mix", "fader"]) return ("main.fader", Math.Round(X32Client.FaderToDb(value), 1));
        if (p is ["main", "st", "mix", "on"]) return ("main.mute", value < 0.5f);
        if (p is ["headamp", var h, "gain"] && int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hn) && hn is >= 0 and < 32)
            return ($"ch{hn + 1}.preamp", X32Client.HeadampToGain(value));
        return null;
    }
}
