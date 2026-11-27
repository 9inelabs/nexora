using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Audio.Mix;

/// <summary>What a recall changes.</summary>
[Flags]
public enum RecallScope
{
    None = 0,
    /// <summary>Trim, polarity, delay, preamp gain and phantom power.</summary>
    Input = 1 << 0,
    /// <summary>Low and high cut, drive and EQ.</summary>
    Eq = 1 << 1,
    /// <summary>Gate, compressor, dynamic EQ and their order.</summary>
    Dynamics = 1 << 2,
    /// <summary>The strip's own rack: colour strips, nodes, effects and plugins.</summary>
    Inserts = 1 << 3,
    /// <summary>Faders, pans and stream levels, and the control groups' masters.</summary>
    Fader = 1 << 4,
    /// <summary>Mutes (and the stream on/off), and the mute groups.</summary>
    Mute = 1 << 5,
    /// <summary>Send levels to mixes, effects and matrices.</summary>
    Sends = 1 << 6,
    /// <summary>The shared effects (reverbs, delays) and their returns.</summary>
    Effects = 1 << 7,
    /// <summary>Which buses exist, what goes to the main and the groups, names and colours, solo and main settings.</summary>
    Patch = 1 << 8,
    /// <summary>The output patch and the speaker processing on the outputs.</summary>
    Outputs = 1 << 9,
    /// <summary>Who is in each control group (VCA, linked and mute groups).</summary>
    Groups = 1 << 10,

    /// <summary>Faders, mutes, pans and send levels.</summary>
    Levels = Fader | Mute | Sends,
    /// <summary>Everything on the strips that shapes the sound.</summary>
    Processing = Input | Eq | Dynamics | Inserts,
    /// <summary>The shape of the desk: buses, assignments, the output patch and control groups.</summary>
    Routing = Patch | Outputs | Groups,
    All = Levels | Processing | Effects | Routing,
}

public static class RecallScopes
{
    /// <summary>Each section on its own, in the order the screen lists them.</summary>
    public static readonly RecallScope[] Sections =
    [
        RecallScope.Input, RecallScope.Eq, RecallScope.Dynamics, RecallScope.Inserts, RecallScope.Fader, RecallScope.Mute,
        RecallScope.Sends, RecallScope.Effects, RecallScope.Patch, RecallScope.Outputs, RecallScope.Groups,
    ];

    public static string Name(RecallScope s) => s switch
    {
        RecallScope.Input => "input",
        RecallScope.Eq => "EQ",
        RecallScope.Dynamics => "dynamics",
        RecallScope.Inserts => "inserts",
        RecallScope.Fader => "faders",
        RecallScope.Mute => "mutes",
        RecallScope.Sends => "sends",
        RecallScope.Effects => "effects",
        RecallScope.Patch => "routing",
        RecallScope.Outputs => "outputs",
        RecallScope.Groups => "groups",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>"everything", "levels only", or the sections in words.</summary>
    public static string Text(RecallScope s)
    {
        if (s == RecallScope.All) return "everything";
        if (s == RecallScope.None) return "nothing";
        if (s == RecallScope.Levels) return "levels only";
        var parts = new List<string>();
        var left = s;
        foreach (var (whole, word) in new[] { (RecallScope.Levels, "levels"), (RecallScope.Processing, "processing"), (RecallScope.Routing, "routing and outputs") })
            if ((left & whole) == whole)
            {
                parts.Add(word);
                left &= ~whole;
            }
        parts.AddRange(Sections.Where(x => (left & x) != 0).Select(Name));
        return string.Join(", ", parts);
    }

    /// <summary>What a recall may change on this strip: the scope, less what the strip keeps safe.</summary>
    public static RecallScope For(Strip s, RecallScope scope) => s.RecallSafe ? RecallScope.None : scope & ~s.Safe;
}

/// <summary>A saved mix: a whole service's desk (a scene) or a moment within it (a snapshot).</summary>
public sealed class SavedMix
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public string SavedBy { get; set; } = "";
    /// <summary>Snapshots only: what the recall changes.</summary>
    public RecallScope Scope { get; set; } = RecallScope.All;
    /// <summary>Snapshots only: faders glide to the new levels over this many seconds.</summary>
    public double FadeSeconds { get; set; }
    public MixState Mix { get; set; } = new();
}

/// <summary>A short list for the screen (without the mix itself).</summary>
public sealed record SavedMixInfo(string Id, string Name, string Notes, DateTime SavedUtc, string SavedBy, RecallScope Scope, double FadeSeconds);

/// <summary>
/// Scenes (one per kind of service, the whole desk) and snapshots (quick cues inside a service: worship,
/// sermon, altar call), kept as files on the NEX-Audio PC.
/// </summary>
public sealed class SceneStore
{
    private readonly string _scenes, _snapshots;
    private readonly object _gate = new();

    public SceneStore(string dataDir)
    {
        _scenes = Path.Combine(dataDir, "scenes");
        _snapshots = Path.Combine(dataDir, "snapshots");
        Directory.CreateDirectory(_scenes);
        Directory.CreateDirectory(_snapshots);
    }

    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    public IReadOnlyList<SavedMixInfo> Scenes => List(_scenes).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Snapshots in their running order.</summary>
    public IReadOnlyList<SavedMixInfo> Snapshots
    {
        get
        {
            var all = List(_snapshots).ToDictionary(s => s.Id);
            var order = LoadOrder();
            var result = order.Where(all.ContainsKey).Select(id => all[id]).ToList();
            result.AddRange(all.Values.Where(s => !order.Contains(s.Id)).OrderBy(s => s.SavedUtc));
            return result;
        }
    }

    private IEnumerable<SavedMixInfo> List(string dir)
    {
        lock (_gate)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (Path.GetFileName(f) == "order.json") continue;
                SavedMix? m = null;
                try { m = JsonSerializer.Deserialize<SavedMix>(File.ReadAllText(f), Json); }
                catch (Exception) { }
                if (m is not null) yield return new SavedMixInfo(m.Id, m.Name, m.Notes, m.SavedUtc, m.SavedBy, m.Scope, m.FadeSeconds);
            }
        }
    }

    public SavedMix? Load(string id, bool snapshot)
    {
        if (!SafeId(id)) return null;
        var f = Path.Combine(snapshot ? _snapshots : _scenes, id + ".json");
        lock (_gate)
        {
            try
            {
                if (!File.Exists(f)) return null;
                var m = JsonSerializer.Deserialize<SavedMix>(File.ReadAllText(f), Json);
                m?.Mix.Repair();
                return m;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public SavedMix Save(SavedMix m, bool snapshot)
    {
        if (!SafeId(m.Id)) m.Id = Guid.NewGuid().ToString("n")[..10];
        m.Name = string.IsNullOrWhiteSpace(m.Name) ? (snapshot ? "Snapshot" : "Scene") : m.Name.Trim();
        if (m.Name.Length > 40) m.Name = m.Name[..40];
        var dir = snapshot ? _snapshots : _scenes;
        lock (_gate)
        {
            var tmp = Path.Combine(dir, m.Id + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json));
            File.Move(tmp, Path.Combine(dir, m.Id + ".json"), true);
            if (snapshot)
            {
                var order = LoadOrder();
                if (!order.Contains(m.Id)) order.Add(m.Id);
                SaveOrder(order);
            }
        }
        return m;
    }

    public bool Delete(string id, bool snapshot)
    {
        if (!SafeId(id)) return false;
        lock (_gate)
        {
            var f = Path.Combine(snapshot ? _snapshots : _scenes, id + ".json");
            if (!File.Exists(f)) return false;
            File.Delete(f);
            if (snapshot)
            {
                var order = LoadOrder();
                order.Remove(id);
                SaveOrder(order);
            }
            return true;
        }
    }

    /// <summary>Puts the snapshots in a new running order.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        lock (_gate) SaveOrder(ids.Where(SafeId).Distinct().ToList());
    }

    private List<string> LoadOrder()
    {
        try
        {
            var f = Path.Combine(_snapshots, "order.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f)) ?? [];
        }
        catch (Exception) { }
        return [];
    }

    private void SaveOrder(List<string> order)
    {
        try { File.WriteAllText(Path.Combine(_snapshots, "order.json"), JsonSerializer.Serialize(order)); }
        catch (Exception) { }
    }

    private static bool SafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Merges a saved mix into the live one, as far as the scope allows and leaving recall-safe strips alone.</summary>
public static class SceneRecall
{
    public static MixState Apply(MixState current, MixState saved, RecallScope scope)
    {
        var result = current.Clone();
        var from = saved.Clone();
        from.Repair();
        bool Has(RecallScope f) => (scope & f) != 0;

        if (Has(RecallScope.Patch))
        {
            // The shape of the desk: which buses exist and where everything goes. A bus kept safe for routing stays as it is.
            var keepSafe = result.Buses.Where(b => (RecallScopes.For(b, scope) & RecallScope.Patch) == 0).ToDictionary(b => b.Id, b => b.Clone());
            result.Buses = from.Buses.Select(b => b.Clone()).ToList();
            result.StreamSettings = from.StreamSettings;
            result.Solo = from.Solo;
            result.MainSettings = from.MainSettings.Clone();
            foreach (var ch in result.Channels)
            {
                var src = from.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (src is null || (RecallScopes.For(ch, scope) & RecallScope.Patch) == 0) continue;
                ch.ToMain = src.ToMain;
                ch.ToCentre = src.ToCentre;
                ch.Groups = [.. src.Groups];
                ch.Sends = src.Sends.Select(x => x.Clone()).ToList();
                ch.Name = src.Name;
                ch.Colour = src.Colour;
            }
            for (var i = 0; i < result.Buses.Count; i++)
                if (keepSafe.TryGetValue(result.Buses[i].Id, out var safe)) result.Buses[i] = safe;
        }

        if (Has(RecallScope.Outputs)) result.Outputs = from.Outputs.Select(o => o.Clone()).ToList();

        // Control groups: who is in them comes with "groups"; their masters with the faders and their mutes with the mutes.
        if (Has(RecallScope.Groups))
        {
            var live = result.Controls.ToDictionary(g => g.Id);
            result.Controls = from.Controls.Select(g => g.Clone()).ToList();
            foreach (var g in result.Controls)
                if (live.TryGetValue(g.Id, out var was) && was.Mode == g.Mode)
                {
                    g.Fader = was.Fader;
                    g.Mute = was.Mute;
                }
        }
        foreach (var g in result.Controls)
            if (from.Control(g.Id) is { } sg && sg.Mode == g.Mode)
            {
                if (Has(RecallScope.Fader)) g.Fader = sg.Fader;
                if (Has(RecallScope.Mute)) g.Mute = sg.Mute;
            }

        foreach (var dst in result.AllStrips())
        {
            var my = RecallScopes.For(dst, scope);
            if (my == RecallScope.None) continue;
            var src = from.Find(dst.Id);
            if (src is null) continue;
            bool On(RecallScope f) => (my & f) != 0;
            if (On(RecallScope.Fader))
            {
                dst.Fader = src.Fader;
                dst.Pan = src.Pan;
                dst.StreamLevel = src.StreamLevel;
            }
            if (On(RecallScope.Mute))
            {
                dst.Mute = src.Mute;
                dst.StreamOn = src.StreamOn;
            }
            if (On(RecallScope.Sends))
            {
                foreach (var s in src.Sends)
                {
                    var d = dst.SendTo(s.Bus);
                    if (d is null)
                    {
                        if (result.Find(s.Bus) is { } bus && Routing.CanSend(dst, bus)) dst.Sends.Add(s.Clone());
                        continue;
                    }
                    d.Level = s.Level;
                    d.On = s.On;
                    d.Tap = s.Tap;
                    d.Pan = s.Pan;
                }
                // Sends the saved mix did not have go quiet.
                foreach (var d in dst.Sends.Where(d => src.SendTo(d.Bus) is null)) d.On = false;
            }
            // An effect return's processing is "effects"; everyone else's is in sections.
            var input = dst.Kind == StripKind.Fx ? On(RecallScope.Effects) : On(RecallScope.Input);
            var eq = dst.Kind == StripKind.Fx ? On(RecallScope.Effects) : On(RecallScope.Eq);
            var dyn = dst.Kind == StripKind.Fx ? On(RecallScope.Effects) : On(RecallScope.Dynamics);
            var ins = dst.Kind == StripKind.Fx ? On(RecallScope.Effects) : On(RecallScope.Inserts);
            if (input)
            {
                dst.Trim = src.Trim;
                dst.Invert = src.Invert;
                dst.DelayMs = src.DelayMs;
                dst.Preamp = src.Preamp;
                dst.Phantom = src.Phantom;
            }
            if (eq)
            {
                dst.Hpf = src.Hpf.Clone();
                dst.Lpf = src.Lpf.Clone();
                dst.Drive = src.Drive.Clone();
                dst.Eq = src.Eq.Clone();
            }
            if (dyn)
            {
                dst.Gate = src.Gate.Clone();
                dst.Comp = src.Comp.Clone();
                dst.DynEq = src.DynEq.Clone();
                dst.Order = src.Order;
            }
            if (ins) dst.Rack = src.Rack.Select(r => r.Clone()).ToList();
        }
        result.Repair();
        return result;
    }

    /// <summary>A mix part way between two (0 = from, 1 = to): faders, sends and stream levels glide, everything else is taken from "to".</summary>
    public static MixState Blend(MixState from, MixState to, double t)
    {
        if (t >= 1) return to.Clone();
        var r = to.Clone();
        foreach (var dst in r.AllStrips())
        {
            if (from.Find(dst.Id) is not { } a) continue;
            dst.Fader = Lerp(a.Fader, dst.Fader, t);
            dst.StreamLevel = Lerp(a.StreamLevel, dst.StreamLevel, t);
            dst.Pan = a.Pan + (dst.Pan - a.Pan) * t;
            foreach (var s in dst.Sends)
                if (a.SendTo(s.Bus) is { } sa)
                    s.Level = Lerp(sa.On ? sa.Level : Dsp.Db.Floor, s.On ? s.Level : Dsp.Db.Floor, t);
        }
        foreach (var g in r.Controls)
            if (from.Control(g.Id) is { } ga && ga.Mode == g.Mode && g.Mode == ControlMode.Vca) g.Fader = Lerp(ga.Fader, g.Fader, t);
        return r;
    }

    /// <summary>Glides in dB but eases out of silence, so a fade-in does not jump.</summary>
    private static double Lerp(double a, double b, double t)
    {
        a = Math.Max(a, -60);
        b = Math.Max(b, -60);
        var v = a + (b - a) * t;
        return v <= -59.9 ? Dsp.Db.Floor : v;
    }
}
