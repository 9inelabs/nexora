using Nexora.Audio.Mix;

namespace Nexora.Audio.Remote;

/// <summary>Who is holding the tablet or phone.</summary>
public enum RemoteRole
{
    /// <summary>Not signed in yet: can only pick a monitor mix or type the PIN.</summary>
    None,
    /// <summary>A musician on their own monitor mix: only what goes into that mix, and its overall level.</summary>
    Musician,
    /// <summary>The engineer walking the room with the PIN: faders, mutes, pans, solos and every send.</summary>
    Engineer,
}

/// <summary>The tablet and phone remote's settings (Remote tab).</summary>
public sealed record RemoteSettings
{
    public const int DefaultPort = 7410;
    public bool On { get; init; }
    public int Port { get; init; } = DefaultPort;
    /// <summary>4 to 8 digits for the engineer's full remote. Empty: no full remote, only musicians' mixes.</summary>
    public string Pin { get; init; } = "";
    /// <summary>The mixes musicians may change from their phones ("mix1", "mix4").</summary>
    public List<string> Mixes { get; init; } = [];

    public RemoteSettings Clean() => this with
    {
        Port = Port is >= 1024 and <= 65535 ? Port : DefaultPort,
        Pin = new string((Pin ?? "").Where(char.IsDigit).Take(8).ToArray()) is { Length: >= 4 } p ? p : "",
        Mixes = (Mixes ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Take(MixState.MaxMix).ToList(),
    };
}

/// <summary>What a remote may change. Everything else is refused, whatever the page sends.</summary>
public static class RemoteAccess
{
    public static bool MayChange(RemoteRole role, string? mixId, string path, MixState s, RemoteSettings settings)
    {
        var seg = path.Split('.');
        if (seg.Length < 2 || s.Find(seg[0]) is not { } strip) return false;
        switch (role)
        {
            case RemoteRole.Engineer:
                if (seg.Length == 2) return seg[1] is "fader" or "mute" or "pan" or "solo";
                return seg.Length == 4 && seg[1] == "send" && seg[3] is "level" or "on" && s.Find(seg[2]) is not null;
            case RemoteRole.Musician:
                if (mixId is null || !settings.Mixes.Contains(mixId) || s.Find(mixId) is not { Kind: StripKind.Mix } mix) return false;
                // Their mix's overall level.
                if (seg.Length == 2) return strip.Id == mix.Id && seg[1] == "fader";
                // How much of each channel, effect or group is in it.
                return seg.Length == 4 && seg[1] == "send" && seg[2] == mix.Id && seg[3] is "level" or "on" && Routing.CanSend(strip, mix);
            default:
                return false;
        }
    }

    /// <summary>The mixes a musician can choose from.</summary>
    public static IEnumerable<Strip> MusicianMixes(MixState s, RemoteSettings settings) =>
        s.BusesOf(StripKind.Mix).Where(m => settings.Mixes.Contains(m.Id)).OrderBy(m => m.Number);

    /// <summary>The strips a remote shows: for a musician, the ones that can feed their mix; for the engineer, every strip.</summary>
    public static IEnumerable<Strip> Strips(RemoteRole role, string? mixId, MixState s)
    {
        if (role == RemoteRole.Engineer) return s.AllStrips().Where(x => x.Kind is not (StripKind.Centre or StripKind.Mono) || x.Kind == StripKind.Centre && s.IsLcr);
        if (mixId is null || s.Find(mixId) is not { } mix) return [];
        return s.Channels.Concat(s.BusesOf(StripKind.Fx)).Concat(s.BusesOf(StripKind.Group)).Where(x => Routing.CanSend(x, mix));
    }

    /// <summary>
    /// Five wrong PINs from one address and it must wait a minute. A phone on the church Wi-Fi cannot
    /// simply try every PIN.
    /// </summary>
    public sealed class PinGuard
    {
        private readonly Dictionary<string, (int Misses, DateTime Until)> _seen = [];

        public bool Blocked(string who, DateTime now)
        {
            lock (_seen) return _seen.TryGetValue(who, out var s) && s.Until > now;
        }

        public bool Check(string who, string typed, string pin, DateTime now)
        {
            lock (_seen)
            {
                if (_seen.TryGetValue(who, out var s) && s.Until > now) return false;
                if (pin.Length >= 4 && typed == pin)
                {
                    _seen.Remove(who);
                    return true;
                }
                var misses = s.Misses + 1;
                _seen[who] = misses >= 5 ? (0, now.AddMinutes(1)) : (misses, DateTime.MinValue);
                return false;
            }
        }
    }
}
