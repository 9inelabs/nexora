using System.Globalization;
using System.Text.Json;
using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

/// <summary>One thing a recall would change.</summary>
public sealed record RecallChange(string StripId, string Strip, string What, string From, string To);

/// <summary>What a scene, snapshot or cue would change if it were recalled now, before it is.</summary>
public sealed record RecallPreview(
    string Id,
    string Name,
    bool Snapshot,
    RecallScope Scope,
    double Fade,
    IReadOnlyList<RecallChange> Changes,
    IReadOnlyList<string> KeptSafe,
    int Strips,
    string? CueId = null);

public static class RecallDiff
{
    private const int MaxLines = 400;

    /// <summary>Lists the differences between the live mix and the mix a recall would make.</summary>
    public static (List<RecallChange> Changes, List<string> Safe, int Strips) Compare(MixState now, MixState after, MixState saved)
    {
        var lines = new List<RecallChange>();
        var touched = new HashSet<string>();
        void Add(Strip s, string what, string from, string to)
        {
            if (lines.Count >= MaxLines) return;
            lines.Add(new RecallChange(s.Id, Label(s), what, from, to));
            touched.Add(s.Id);
        }

        foreach (var b in after.Buses.Where(b => now.Find(b.Id) is null)) Add(b, "Bus", "—", "added");
        foreach (var b in now.Buses.Where(b => after.Find(b.Id) is null)) Add(b, "Bus", "there", "removed");

        foreach (var a in after.AllStrips())
        {
            if (now.Find(a.Id) is not { } n) continue;
            if (n.Name != a.Name) Add(a, "Name", n.Name, a.Name);
            if (Math.Abs(n.Fader - a.Fader) > 0.05) Add(a, "Fader", Db(n.Fader), Db(a.Fader));
            if (n.Mute != a.Mute) Add(a, "Mute", n.Mute ? "muted" : "on", a.Mute ? "muted" : "on");
            if (Math.Abs(n.Pan - a.Pan) > 0.005) Add(a, "Pan", Pan(n.Pan), Pan(a.Pan));
            if (n.ToMain != a.ToMain) Add(a, "To main", n.ToMain ? "yes" : "no", a.ToMain ? "yes" : "no");
            if (a.Kind == StripKind.Channel)
            {
                if (n.Input != a.Input) Add(a, "Input", In(n.Input), In(a.Input));
                if (n.AltInput != a.AltInput) Add(a, "Alternate input", In(n.AltInput), In(a.AltInput));
                if (n.UseAlt != a.UseAlt) Add(a, "Listening to", n.UseAlt ? "alternate" : "main input", a.UseAlt ? "alternate" : "main input");
                if (n.DirectTap != a.DirectTap) Add(a, "Direct out from", MixState.TapShort(n.DirectTap), MixState.TapShort(a.DirectTap));
            }
            foreach (var g in a.Groups.Where(g => !n.Groups.Contains(g)))
                Add(a, "Group " + (after.Find(g) is { } gb ? Label(gb) : g), "off", "on");
            foreach (var g in n.Groups.Where(g => !a.Groups.Contains(g)))
                Add(a, "Group " + (now.Find(g) is { } gb ? Label(gb) : g), "on", "off");
            if (Math.Abs(n.StreamLevel - a.StreamLevel) > 0.05 || n.StreamOn != a.StreamOn)
                Add(a, "Stream", n.StreamOn ? Db(n.StreamLevel) : "off", a.StreamOn ? Db(a.StreamLevel) : "off");

            foreach (var s in a.Sends)
            {
                var o = n.SendTo(s.Bus);
                var bus = after.Find(s.Bus);
                var label = "Send to " + (bus is null ? s.Bus : Label(bus));
                var was = o is null || !o.On ? "off" : Db(o.Level);
                var will = !s.On ? "off" : Db(s.Level);
                if (was != will) Add(a, label, was, will);
                else if (o is not null && o.Tap != s.Tap) Add(a, label + " tap", o.Tap.ToString(), s.Tap.ToString());
            }
            foreach (var o in n.Sends.Where(o => o.On && a.SendTo(o.Bus) is null))
                Add(a, "Send to " + (now.Find(o.Bus) is { } b ? Label(b) : o.Bus), Db(o.Level), "off");

            var parts = new List<string>();
            if (Differs(n.Hpf, a.Hpf) || Differs(n.Lpf, a.Lpf)) parts.Add("cuts");
            if (Differs(n.Drive, a.Drive)) parts.Add("drive");
            if (Differs(n.Eq, a.Eq)) parts.Add("EQ");
            if (Differs(n.DynEq, a.DynEq)) parts.Add("dynamic EQ");
            if (Differs(n.Gate, a.Gate)) parts.Add("gate");
            if (Differs(n.Comp, a.Comp)) parts.Add("compressor");
            if (n.Order != a.Order) parts.Add("order");
            if (Differs(n.Rack, a.Rack)) parts.Add("effects");
            if (Math.Abs(n.Trim - a.Trim) > 0.05) parts.Add("trim");
            if (n.Invert != a.Invert) parts.Add("polarity");
            if (Math.Abs(n.DelayMs - a.DelayMs) > 0.01) parts.Add("delay");
            if (parts.Count > 0) Add(a, "Processing", "", string.Join(", ", parts));
        }

        var outs = after.Outputs.Count(o => now.Outputs.FirstOrDefault(x => x.Number == o.Number) is not { } x || x.Source != o.Source);
        if (outs > 0 && lines.Count < MaxLines)
            lines.Add(new RecallChange("out", "Outputs", "Patch", "", outs == 1 ? "1 output repatched" : $"{outs} outputs repatched"));

        // Recall-safe strips the saved mix would have changed.
        var safe = now.AllStrips()
            .Where(s => (s.RecallSafe || s.Safe != RecallScope.None) && saved.Find(s.Id) is { } sv && Differs(Strip(s), Strip(sv)))
            .Select(Label)
            .ToList();
        return (lines, safe, touched.Count);
    }

    private static string Label(Strip s) => s.Name.Length > 0 ? s.Name : s.Id;

    private static string In(int n) => n <= 0 ? "none" : $"In {n}";

    private static string Db(double v) => v <= Dsp.Db.Floor + 0.5 ? "−∞" : v.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    private static string Pan(double p) => Math.Abs(p) < 0.005 ? "centre" : p < 0 ? $"L{-p * 100:0}" : $"R{p * 100:0}";

    private static readonly JsonSerializerOptions J = MixState.Json;

    private static bool Differs<T>(T a, T b) => JsonSerializer.Serialize(a, J) != JsonSerializer.Serialize(b, J);

    /// <summary>A strip without its live-only bits, for comparing.</summary>
    private static Strip Strip(Strip s)
    {
        var c = s.Clone();
        c.Solo = false;
        c.RecallSafe = false;
        c.Safe = RecallScope.None;
        return c;
    }
}
