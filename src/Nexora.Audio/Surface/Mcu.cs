using System.Text;
using Nexora.Audio.Mix;
using Nexora.Audio.X32;

namespace Nexora.Audio.Surface;

/// <summary>What a press or move on a control surface asks NEX-Audio to do.</summary>
public abstract record SurfaceAction;
/// <summary>Change one mix setting (a path for <see cref="MixParams"/>).</summary>
public sealed record SetAction(string Path, object Value) : SurfaceAction;
/// <summary>Select a strip, as if it was tapped on the screen.</summary>
public sealed record SelectAction(string StripId) : SurfaceAction;

/// <summary>
/// A fader box that speaks the Mackie Control (MCU) language: Behringer X-Touch and X-Touch Extender, iCON,
/// PreSonus FaderPort 8/16, Mackie MCU Pro and most others (HUI-only boxes can usually be switched to MCU).
/// Eight motor faders and a master, mute, solo and select per strip, a knob for pan, bank buttons, and the
/// name and level on the little screen. Nothing here touches MIDI itself: bytes in, actions and bytes out.
/// </summary>
public sealed class McuSurface
{
    public const int Width = 8;
    // Notes for the buttons (Mackie Control).
    private const int Rec = 0x00, Solo = 0x08, Mute = 0x10, Select = 0x18, VPotPush = 0x20;
    private const int BankLeft = 0x2E, BankRight = 0x2F, ChannelLeft = 0x30, ChannelRight = 0x31, FaderTouch = 0x68, MasterTouch = 0x70;

    private readonly TouchGuard _guard = new(TimeSpan.FromMilliseconds(300));
    private readonly Dictionary<string, string> _sent = [];
    private string[] _order = [];

    /// <summary>The first strip on fader 1, counted along <see cref="Order"/>.</summary>
    public int Bank { get; private set; }

    /// <summary>The strips the faders step through: the channels, then effect returns, groups, mixes and matrices.</summary>
    public static string[] Order(MixState s) =>
        s.Channels.OrderBy(c => c.Number).Select(c => c.Id)
            .Concat(new[] { StripKind.Fx, StripKind.Group, StripKind.Mix, StripKind.Matrix }.SelectMany(k => s.BusesOf(k).OrderBy(b => b.Number).Select(b => b.Id)))
            .ToArray();

    /// <summary>The strip on fader 0 to 7 just now, or null for an empty fader.</summary>
    public string? StripAt(int fader) => Bank + fader < _order.Length && fader is >= 0 and < Width ? _order[Bank + fader] : null;

    /// <summary>Forget what was sent, so the next <see cref="Refresh"/> sends everything (a box was plugged in).</summary>
    public void Reset() => _sent.Clear();

    /// <summary>Reads one MIDI message from the box (a short message: status and one or two data bytes).</summary>
    public IReadOnlyList<SurfaceAction> Receive(ReadOnlySpan<byte> msg, MixState s, DateTime? now = null)
    {
        _order = Order(s);
        if (msg.Length < 2) return [];
        var status = msg[0] & 0xF0;
        var ch = msg[0] & 0x0F;
        var d1 = msg[1];
        var d2 = msg.Length > 2 ? msg[2] : (byte)0;
        var t = now ?? DateTime.UtcNow;

        // A fader moved: pitch bend, 14 bits, one MIDI channel per fader (8 is the master).
        if (status == 0xE0)
        {
            var pos = ((d2 << 7) | d1) / 16383f;
            var id = ch == 8 ? s.Main.Id : StripAt(ch);
            if (id is null) return [];
            _guard.Moved(FaderKey(ch), t);
            var db = X32Client.FaderToDb(pos);
            return [new SetAction($"{id}.fader", db <= Dsp.Db.Floor + 0.5 ? Dsp.Db.Floor : Math.Round(db, 1))];
        }
        // A knob turned: relative, 1 to 63 clockwise, 65 to 127 the other way.
        if (status == 0xB0 && d1 is >= 0x10 and < 0x18)
        {
            if (StripAt(d1 - 0x10) is not { } id || s.Find(id) is not { } st) return [];
            var steps = (d2 & 0x40) != 0 ? -(d2 & 0x3F) : d2 & 0x3F;
            return [new SetAction($"{id}.pan", Math.Round(Math.Clamp(st.Pan + steps * 0.04, -1, 1), 2))];
        }
        if (status is not (0x90 or 0x80)) return [];
        var down = status == 0x90 && d2 > 0;

        // Touch-sensitive faders: leave the motor alone while a hand is on it.
        if (d1 is >= FaderTouch and <= MasterTouch)
        {
            _guard.Touch(FaderKey(d1 - FaderTouch), down, t);
            return [];
        }
        if (!down) return [];
        switch (d1)
        {
            case BankLeft: Move(-Width); return [];
            case BankRight: Move(Width); return [];
            case ChannelLeft: Move(-1); return [];
            case ChannelRight: Move(1); return [];
        }
        var fader = d1 & 7;
        if (StripAt(fader) is not { } sid || s.Find(sid) is not { } strip) return [];
        return (d1 & ~7) switch
        {
            Mute => [new SetAction($"{sid}.mute", !strip.Mute)],
            Solo => [new SetAction($"{sid}.solo", !strip.Solo)],
            Select => [new SelectAction(sid)],
            VPotPush => [new SetAction($"{sid}.pan", 0.0)],
            _ => [],
        };

        void Move(int by)
        {
            var max = Math.Max(0, _order.Length - Width);
            Bank = Math.Clamp(Bank + by, 0, max);
        }
    }

    /// <summary>Puts the box's banks back where they were (at the start), and the order of strips.</summary>
    public void SetBank(int bank, MixState s)
    {
        _order = Order(s);
        Bank = Math.Clamp(bank, 0, Math.Max(0, _order.Length - Width));
    }

    /// <summary>
    /// The MIDI messages that bring the box in step with the mix: motor faders (not the ones under a hand),
    /// button lights, knob rings and the scribble strip. Only what changed since the last call.
    /// </summary>
    public IReadOnlyList<byte[]> Refresh(MixState s, string? selected, Func<Strip, string> name, DateTime? now = null)
    {
        _order = Order(s);
        Bank = Math.Clamp(Bank, 0, Math.Max(0, _order.Length - Width));
        var t = now ?? DateTime.UtcNow;
        var output = new List<byte[]>();
        var top = new StringBuilder();
        var bottom = new StringBuilder();
        for (var f = 0; f <= Width; f++)
        {
            var st = f == Width ? s.Main : StripAt(f) is { } id ? s.Find(id) : null;
            var pos = st is null ? 0 : (int)Math.Round(X32Client.DbToFader(st.Fader) * 16383);
            if (!_guard.Held(FaderKey(f), t))
                Send($"f{f}", [(byte)(0xE0 | f), (byte)(pos & 0x7F), (byte)(pos >> 7)]);
            if (f == Width) break;
            Send($"m{f}", [0x90, (byte)(Mute + f), (byte)(st is { Mute: true } || st is { HardMute: true } ? 0x7F : 0)]);
            Send($"s{f}", [0x90, (byte)(Solo + f), (byte)(st is { Solo: true } ? 0x7F : 0)]);
            Send($"x{f}", [0x90, (byte)(Select + f), (byte)(st is not null && st.Id == selected ? 0x7F : 0)]);
            Send($"r{f}", [0x90, (byte)(Rec + f), 0]);
            // The knob ring: one dot for pan, 1 (left) to 11 (right), off for an empty fader.
            var ring = st is null ? 0 : 1 + (int)Math.Round((Math.Clamp(st.Pan, -1, 1) + 1) / 2 * 10);
            Send($"p{f}", [0xB0, (byte)(0x30 + f), (byte)ring]);
            top.Append(Cell(st is null ? "" : name(st)));
            bottom.Append(Cell(st is null ? "" : st.Mute || st.HardMute ? "MUTE" : st.Fader <= Dsp.Db.Floor + 0.5 ? "-inf" : st.Fader.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)));
        }
        SendText("t", 0, top.ToString());
        SendText("b", 56, bottom.ToString());
        return output;

        void Send(string key, byte[] m)
        {
            var k = Convert.ToHexString(m);
            if (_sent.TryGetValue(key, out var was) && was == k) return;
            _sent[key] = k;
            output.Add(m);
        }

        void SendText(string key, int offset, string text)
        {
            if (_sent.TryGetValue(key, out var was) && was == text) return;
            _sent[key] = text;
            output.Add(Lcd(offset, text));
        }
    }

    /// <summary>Text on the scribble strip: 7 characters a fader, top row from 0, bottom row from 56.</summary>
    public static byte[] Lcd(int offset, string text)
    {
        var bytes = new List<byte> { 0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, (byte)offset };
        foreach (var c in text) bytes.Add(c is >= ' ' and <= '~' ? (byte)c : (byte)'?');
        bytes.Add(0xF7);
        return [.. bytes];
    }

    /// <summary>The level meter on fader 0 to 7 (MCU channel pressure), from a peak level 0 to 1.</summary>
    public static byte[] Meter(int fader, float peak)
    {
        var db = peak <= 0 ? -100 : 20 * Math.Log10(peak);
        var level = (int)Math.Clamp(Math.Round((db + 60) / 5), 0, 12);
        return [0xD0, (byte)((fader << 4) | level)];
    }

    private static string Cell(string s)
    {
        s = new string(s.Where(c => c is >= ' ' and <= '~').ToArray());
        if (s.Length > 6) s = s[..6];
        return s.PadRight(7);
    }

    private static string FaderKey(int f) => "fader" + f;
}
